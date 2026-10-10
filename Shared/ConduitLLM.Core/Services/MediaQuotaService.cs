using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Database-backed media quota evaluator. Media rows are aggregated in SQL by group;
/// no media collection is materialized on the generation hot path.
/// </summary>
public sealed class MediaQuotaService : IMediaQuotaService
{
    private readonly IMediaRuntimeStore _mediaStore;
    private readonly ILogger<MediaQuotaService> _logger;

    public MediaQuotaService(
        IMediaRuntimeStore mediaStore,
        ILogger<MediaQuotaService> logger)
    {
        _mediaStore = mediaStore ?? throw new ArgumentNullException(nameof(mediaStore));
        _logger = logger;
    }

    public async Task EnsureCanStoreAsync(
        int virtualKeyId,
        long prospectiveSizeBytes,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _mediaStore.GetQuotaSnapshotAsync(
            virtualKeyId,
            cancellationToken);
        if (snapshot == null)
        {
            _logger.LogWarning(
                "Skipping media quota evaluation because virtual key {VirtualKeyId} was not found",
                virtualKeyId);
            return;
        }

        if (!snapshot.MaxStorageSizeBytes.HasValue && !snapshot.MaxFileCount.HasValue)
        {
            return;
        }

        var wouldUseBytes = checked(snapshot.TotalSizeBytes + Math.Max(0, prospectiveSizeBytes));
        var wouldUseFiles = checked(snapshot.TotalFiles + 1);
        var exceedsBytes = snapshot.MaxStorageSizeBytes.HasValue &&
            wouldUseBytes > snapshot.MaxStorageSizeBytes.Value;
        var exceedsFiles = snapshot.MaxFileCount.HasValue &&
            wouldUseFiles > snapshot.MaxFileCount.Value;

        if ((!exceedsBytes && !exceedsFiles) ||
            snapshot.QuotaExceededBehavior == (int)MediaQuotaExceededBehavior.AllowAndEvict)
        {
            if (exceedsBytes || exceedsFiles)
            {
                _logger.LogInformation(
                    "Allowing media write for group {GroupId} above quota; scheduled eviction is configured",
                    snapshot.VirtualKeyGroupId);
            }

            return;
        }

        throw new RateLimitExceededException(
            BuildQuotaExceededMessage(
                snapshot.VirtualKeyGroupId,
                wouldUseBytes,
                wouldUseFiles,
                snapshot));
    }

    public async Task<IReadOnlyList<MediaGroupQuotaUsage>> GetGroupUsagesAsync(
        int? virtualKeyGroupId = null,
        CancellationToken cancellationToken = default)
    {
        return (await _mediaStore.GetGroupQuotaUsagesAsync(
            virtualKeyGroupId,
            cancellationToken))
            .Select(usage => new MediaGroupQuotaUsage
            {
                VirtualKeyGroupId = usage.VirtualKeyGroupId,
                VirtualKeyGroupName = usage.VirtualKeyGroupName,
                MediaRetentionPolicyId = usage.MediaRetentionPolicyId,
                MediaRetentionPolicyName = usage.MediaRetentionPolicyName,
                TotalFiles = usage.TotalFiles,
                TotalSizeBytes = usage.TotalSizeBytes,
                MaxStorageSizeBytes = usage.MaxStorageSizeBytes,
                MaxFileCount = usage.MaxFileCount,
                QuotaExceededBehavior = (MediaQuotaExceededBehavior)usage.QuotaExceededBehavior,
                RespectRecentAccess = usage.RespectRecentAccess,
                RecentAccessWindowDays = usage.RecentAccessWindowDays
            })
            .ToArray();
    }

    private static string BuildQuotaExceededMessage(
        int groupId,
        long wouldUseBytes,
        int wouldUseFiles,
        MediaRuntimeQuotaSnapshot snapshot)
    {
        var limits = new List<string>();
        if (snapshot.MaxStorageSizeBytes.HasValue)
        {
            limits.Add(
                $"storage {wouldUseBytes:N0}/{snapshot.MaxStorageSizeBytes.Value:N0} bytes");
        }
        if (snapshot.MaxFileCount.HasValue)
        {
            limits.Add($"files {wouldUseFiles:N0}/{snapshot.MaxFileCount.Value:N0}");
        }

        return $"Media quota exceeded for virtual key group {groupId} " +
            $"({string.Join(", ", limits)}). Delete media or ask an administrator to raise the quota.";
    }
}

/// <summary>
/// Bridges singleton storage services to the scoped quota evaluator.
/// </summary>
public sealed class MediaQuotaGuard : IMediaQuotaGuard
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public MediaQuotaGuard(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async Task EnsureCanStoreAsync(
        string? createdBy,
        long prospectiveSizeBytes,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(createdBy, out var virtualKeyId) || virtualKeyId <= 0)
            return;

        using var scope = _serviceScopeFactory.CreateScope();
        var quotaService = scope.ServiceProvider.GetRequiredService<IMediaQuotaService>();
        await quotaService.EnsureCanStoreAsync(
            virtualKeyId,
            prospectiveSizeBytes,
            cancellationToken);
    }
}
