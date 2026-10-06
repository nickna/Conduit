using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services.Strategies
{
    /// <summary>
    /// Processes media from external URLs by downloading and storing.
    /// </summary>
    public class UrlMediaProcessor : IMediaProcessingStrategy<IGeneratedMediaData>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMediaStorageService _storageService;
        private readonly IEventBus _eventBus;
        private readonly ILogger<UrlMediaProcessor> _logger;

        public UrlMediaProcessor(
            IHttpClientFactory httpClientFactory,
            IMediaStorageService storageService,
            IEventBus eventBus,
            ILogger<UrlMediaProcessor> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ProcessedMediaItem> ProcessAsync(
            IGeneratedMediaData mediaData,
            MediaProcessingContext context,
            CancellationToken cancellationToken)
        {
            // Extract URL from the media object
            string? url = mediaData.Url;
            if (string.IsNullOrEmpty(url) || !UrlBuilder.IsValidUrl(url))
            {
                throw new InvalidOperationException($"Invalid or missing URL in media data");
            }

            _logger.LogInformation("Downloading {MediaType} from {Url} for task {RequestId}",
                context.MediaType, url, context.RequestId);

            var downloadStopwatch = Stopwatch.StartNew();

            try
            {
                using var httpClient = CreateHttpClient(context.MediaType);

                // Use ResponseHeadersRead for streaming
                using var response = await httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                downloadStopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to download {MediaType} from {Url}: {StatusCode}",
                        context.MediaType, url, response.StatusCode);

                    // Return original URL as fallback
                    return new ProcessedMediaItem
                    {
                        Url = url,
                        Index = context.Index,
                        Metadata = new Dictionary<string, object>
                        {
                            ["originalUrl"] = url,
                            ["downloadFailed"] = true
                        }
                    };
                }

                // Get content information
                var contentLength = response.Content.Headers.ContentLength ?? 0;
                var contentType = DetermineContentType(response, url, context.MediaType);

                // Create media metadata
                var metadata = CreateMediaMetadata(context, contentType, contentLength, url);

                // Stream directly to storage
                using var mediaStream = await response.Content.ReadAsStreamAsync();

                var storageStopwatch = Stopwatch.StartNew();

                // Create progress callback for video uploads
                Action<long>? progressCallback = null;
                if (context.MediaType == MediaType.Video)
                {
                    progressCallback = bytesProcessed =>
                    {
                        var percentage = contentLength > 0
                            ? (int)((bytesProcessed * 100) / contentLength)
                            : -1;

                        _logger.LogDebug("{MediaType} upload progress: {BytesProcessed} bytes ({Percentage}%)",
                            context.MediaType, bytesProcessed, percentage);
                    };
                }

                // Store the media
                var storageResult = context.MediaType == MediaType.Video
                    ? await _storageService.StoreVideoAsync(mediaStream, metadata as VideoMediaMetadata ?? new VideoMediaMetadata(), progressCallback)
                    : await _storageService.StoreAsync(mediaStream, metadata);

                storageStopwatch.Stop();

                _logger.LogInformation("Downloaded and stored {MediaType} from {OriginalUrl} to {StorageUrl} (Download: {DownloadMs}ms, Storage: {StorageMs}ms)",
                    context.MediaType, url, storageResult.Url,
                    downloadStopwatch.ElapsedMilliseconds,
                    storageStopwatch.ElapsedMilliseconds);

                // Publish media generation completed event
                await PublishMediaCompletedEvent(storageResult, context, metadata, contentLength);

                return new ProcessedMediaItem
                {
                    Url = storageResult.Url,
                    StorageKey = storageResult.StorageKey,
                    Index = context.Index,
                    Metadata = new Dictionary<string, object>
                    {
                        ["provider"] = context.ModelInfo?.ProviderName ?? "unknown",
                        ["model"] = context.ModelInfo?.ModelId ?? "unknown",
                        ["index"] = context.Index,
                        ["originalUrl"] = url,
                        ["downloadMs"] = downloadStopwatch.ElapsedMilliseconds,
                        ["storageMs"] = storageStopwatch.ElapsedMilliseconds
                    }
                };
            }

            catch (TaskCanceledException)
            {
                // Cancellation is not a recoverable media-download failure.
                throw;
            }
            catch (RateLimitExceededException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download and store {MediaType} from URL: {Url}",
                    context.MediaType, url);

                // Return original URL as fallback
                return new ProcessedMediaItem
                {
                    Url = url,
                    Index = context.Index,
                    Metadata = new Dictionary<string, object>
                    {
                        ["originalUrl"] = url,
                        ["error"] = ex.Message
                    }
                };
            }
        }

        private HttpClient CreateHttpClient(MediaType mediaType)
        {
            var clientName = mediaType == MediaType.Video ? "VideoDownload" : "ImageDownload";
            var httpClient = _httpClientFactory.CreateClient(clientName);

            // Set appropriate timeout based on media type
            httpClient.Timeout = mediaType == MediaType.Video
                ? TimeSpan.FromMinutes(15)  // Videos need longer timeout
                : TimeSpan.FromSeconds(60); // Images can be faster

            return httpClient;
        }

        private string DetermineContentType(HttpResponseMessage response, string url, MediaType mediaType)
        {
            // Try to get from response headers
            if (response.Content.Headers.ContentType != null)
            {
                return response.Content.Headers.ContentType.MediaType ?? GetDefaultContentType(mediaType);
            }

            // Infer from URL extension
            if (url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                url.Contains(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                return "image/jpeg";
            }

            if (url.Contains(".png", StringComparison.OrdinalIgnoreCase))
            {
                return "image/png";
            }

            if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                return "video/mp4";
            }

            return GetDefaultContentType(mediaType);
        }

        private string GetDefaultContentType(MediaType mediaType)
        {
            return mediaType == MediaType.Video ? "video/mp4" : "image/png";
        }

        private MediaMetadata CreateMediaMetadata(
            MediaProcessingContext context,
            string contentType,
            long contentLength,
            string originalUrl)
        {
            var extension = GetFileExtension(contentType, context.MediaType);

            var metadata = new MediaMetadata
            {
                ContentType = contentType,
                FileName = $"generated_{DateTime.UtcNow:yyyyMMddHHmmss}_{context.Index}.{extension}",
                MediaType = context.MediaType,
                CustomMetadata = new Dictionary<string, string>
                {
                    ["prompt"] = context.Prompt,
                    ["model"] = context.ModelInfo?.ModelId ?? "",
                    ["provider"] = context.ModelInfo?.ProviderName ?? "",
                    ["originalUrl"] = originalUrl
                }
            };

            // Add CreatedBy if we have virtual key info
            metadata.CreatedBy = context.CreatedBy ??
                (context.VirtualKeyId > 0 ? context.VirtualKeyId.ToString() : null);

            // For video, create VideoMediaMetadata with additional properties
            if (context.MediaType == MediaType.Video)
            {
                return new VideoMediaMetadata
                {
                    ContentType = metadata.ContentType,
                    FileName = metadata.FileName,
                    MediaType = metadata.MediaType,
                    CustomMetadata = metadata.CustomMetadata,
                    CreatedBy = metadata.CreatedBy,
                    FileSizeBytes = contentLength,
                    Width = 1280,  // Default values, should be extracted from actual video
                    Height = 720,
                    Duration = 6,
                    FrameRate = 30,
                    Codec = "h264",
                    GeneratedByModel = context.ModelInfo?.ModelId ?? "",
                    GenerationPrompt = context.Prompt,
                    Resolution = "1280x720"
                };
            }

            return metadata;
        }

        private string GetFileExtension(string contentType, MediaType mediaType)
        {
            // Shared map returns a leading dot; this processor uses dotless extensions.
            return Utilities.MediaContentTypes.GetExtension(contentType)?.TrimStart('.')
                ?? (mediaType == MediaType.Video ? "mp4" : "png");
        }

        private async Task PublishMediaCompletedEvent(
            MediaStorageResult storageResult,
            MediaProcessingContext context,
            MediaMetadata metadata,
            long contentLength)
        {
            var eventMetadata = new Dictionary<string, object>
            {
                ["provider"] = context.ModelInfo?.ProviderName ?? "",
                ["model"] = context.ModelInfo?.ModelId ?? "",
                ["index"] = context.Index
            };

            // Add video-specific metadata
            if (metadata is VideoMediaMetadata videoMetadata)
            {
                eventMetadata["width"] = videoMetadata.Width;
                eventMetadata["height"] = videoMetadata.Height;
                eventMetadata["duration"] = videoMetadata.Duration;
                eventMetadata["frameRate"] = videoMetadata.FrameRate;
                eventMetadata["resolution"] = videoMetadata.Resolution;
            }

            await _eventBus.PublishAsync(new MediaGenerationCompleted
            {
                MediaType = context.MediaType,
                VirtualKeyId = context.VirtualKeyId,
                MediaUrl = storageResult.Url,
                StorageKey = storageResult.StorageKey,
                FileSizeBytes = contentLength,
                ContentType = metadata.ContentType,
                GeneratedByModel = context.ModelInfo?.ModelId ?? "",
                Provider = context.ModelInfo?.ProviderName ?? "",
                GenerationPrompt = context.Prompt,
                GeneratedAt = DateTime.UtcNow,
                Metadata = eventMetadata,
                CorrelationId = context.CorrelationId ?? string.Empty
            });
        }
    }
}
