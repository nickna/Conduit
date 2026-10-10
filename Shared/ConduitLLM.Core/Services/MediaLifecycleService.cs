using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Service implementation for managing the lifecycle of media files.
    /// </summary>
    public class MediaLifecycleService : IMediaLifecycleService
    {
        private readonly IMediaRuntimeStore _mediaStore;
        private readonly IMediaQuotaService? _mediaQuotaService;
        private readonly ILogger<MediaLifecycleService> _logger;
        private const int VirtualKeyStatsLimit = 100;

        /// <summary>
        /// Initializes a new instance of the MediaLifecycleService class.
        /// </summary>
        /// <param name="mediaStore">The media runtime store.</param>
        /// <param name="logger">The logger instance.</param>
        /// <param name="mediaQuotaService">Optional group quota reporting service.</param>
        public MediaLifecycleService(
            IMediaRuntimeStore mediaStore,
            ILogger<MediaLifecycleService> logger,
            IMediaQuotaService? mediaQuotaService = null)
        {
            _mediaStore = mediaStore ?? throw new ArgumentNullException(nameof(mediaStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediaQuotaService = mediaQuotaService;
        }

        /// <inheritdoc/>
        public async Task<MediaRecord> TrackMediaAsync(
            int virtualKeyId,
            string storageKey,
            string mediaType,
            MediaLifecycleMetadata metadata)
        {
            if (virtualKeyId <= 0)
                throw new ArgumentException("Virtual key ID must be positive", nameof(virtualKeyId));

            if (string.IsNullOrWhiteSpace(storageKey))
                throw new ArgumentException("Storage key cannot be empty", nameof(storageKey));

            if (string.IsNullOrWhiteSpace(mediaType))
                throw new ArgumentException("Media type cannot be empty", nameof(mediaType));

            var mediaRecord = new MediaRuntimeRecord
            {
                Id = Guid.NewGuid(),
                StorageKey = storageKey,
                VirtualKeyId = virtualKeyId,
                MediaType = mediaType,
                ContentType = metadata?.ContentType,
                SizeBytes = metadata?.SizeBytes,
                ContentHash = metadata?.ContentHash,
                Provider = metadata?.Provider,
                Model = metadata?.Model,
                Prompt = metadata?.Prompt,
                StorageUrl = metadata?.StorageUrl,
                PublicUrl = metadata?.PublicUrl,
                ExpiresAt = metadata?.ExpiresAt,
                CreatedAt = DateTime.UtcNow,
                AccessCount = 0
            };

            await _mediaStore.CreateAsync(mediaRecord);

            _logger.LogInformation(
                "Tracked media {StorageKey} of type {MediaType} for virtual key {VirtualKeyId}",
                storageKey, mediaType, virtualKeyId);

            return ToEntity(mediaRecord);
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAccessStatsAsync(string storageKey)
        {
            if (string.IsNullOrWhiteSpace(storageKey))
                return false;

            try
            {
                var mediaRecord = await _mediaStore.GetByStorageKeyAsync(storageKey);
                if (mediaRecord == null)
                    return false;

                return await _mediaStore.UpdateAccessStatsAsync(mediaRecord.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating access stats for media {StorageKey}", storageKey);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<MediaStorageStats> GetStorageStatsByVirtualKeyAsync(int virtualKeyId)
        {
            var mediaRecords = await _mediaStore.GetByVirtualKeyIdAsync(virtualKeyId);

            var stats = new MediaStorageStats
            {
                VirtualKeyId = virtualKeyId,
                TotalFiles = mediaRecords.Count,
                TotalSizeBytes = mediaRecords.Sum(m => m.SizeBytes ?? 0)
            };

            // Group by media type
            var typeGroups = mediaRecords.GroupBy(m => m.MediaType);
            foreach (var group in typeGroups)
            {
                stats.ByMediaType[group.Key] = new MediaTypeStats
                {
                    FileCount = group.Count(),
                    SizeBytes = group.Sum(m => m.SizeBytes ?? 0)
                };
            }

            return stats;
        }

        /// <inheritdoc/>
        public async Task<OverallMediaStorageStats> GetOverallStorageStatsAsync(int? virtualKeyGroupId = null)
        {
            var aggregate = await _mediaStore.GetAggregateStorageStatsAsync(
                virtualKeyGroupId,
                VirtualKeyStatsLimit);
            return new OverallMediaStorageStats
            {
                TotalSizeBytes = aggregate.TotalSizeBytes,
                TotalFiles = aggregate.TotalFiles,
                OrphanedFiles = 0,
                ByProvider = aggregate.ByProvider.ToDictionary(),
                ByMediaType = aggregate.ByMediaType.ToDictionary(
                    row => row.MediaType,
                    row => new MediaTypeStats
                    {
                        FileCount = row.FileCount,
                        SizeBytes = row.SizeBytes
                    }),
                StorageByVirtualKey = aggregate.TopVirtualKeys.ToDictionary(
                    row => row.VirtualKeyId.ToString(),
                    row => row.SizeBytes),
                GroupQuotaUsage = _mediaQuotaService == null
                    ? Array.Empty<MediaGroupQuotaUsage>()
                    : await _mediaQuotaService.GetGroupUsagesAsync(virtualKeyGroupId)
            };
        }

        /// <inheritdoc/>
        public async Task<List<MediaRecord>> GetMediaByVirtualKeyAsync(int virtualKeyId)
        {
            return (await _mediaStore.GetByVirtualKeyIdAsync(virtualKeyId))
                .Select(ToEntity)
                .ToList();
        }

        private static MediaRecord ToEntity(MediaRuntimeRecord media) => new()
        {
            Id = media.Id,
            StorageKey = media.StorageKey,
            VirtualKeyId = media.VirtualKeyId,
            MediaType = media.MediaType,
            ContentType = media.ContentType,
            SizeBytes = media.SizeBytes,
            ContentHash = media.ContentHash,
            Provider = media.Provider,
            Model = media.Model,
            Prompt = media.Prompt,
            StorageUrl = media.StorageUrl,
            PublicUrl = media.PublicUrl,
            ExpiresAt = media.ExpiresAt,
            CreatedAt = media.CreatedAt,
            LastAccessedAt = media.LastAccessedAt,
            AccessCount = media.AccessCount,
            DeletedAt = media.DeletedAt
        };
    }

}
