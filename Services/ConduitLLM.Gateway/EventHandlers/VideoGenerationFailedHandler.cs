using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Interfaces;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace ConduitLLM.Gateway.EventHandlers
{
    /// <summary>
    /// Handles VideoGenerationFailed events to update task status, track failure metrics, and analyze error patterns.
    /// </summary>
    public class VideoGenerationFailedHandler : IEventHandler<VideoGenerationFailed>
    {
        private readonly IAsyncTaskService _asyncTaskService;
        private readonly IMemoryCache _progressCache;
        private readonly IVideoGenerationNotificationService _notificationService;
        private readonly ILogger<VideoGenerationFailedHandler> _logger;
        private const string FailureCountCacheKeyPrefix = "video_generation_failures_";

        public VideoGenerationFailedHandler(
            IAsyncTaskService asyncTaskService,
            IMemoryCache progressCache,
            IVideoGenerationNotificationService notificationService,
            ILogger<VideoGenerationFailedHandler> logger)
        {
            _asyncTaskService = asyncTaskService;
            _progressCache = progressCache;
            _notificationService = notificationService;
            _logger = logger;
        }

        public async Task HandleAsync(VideoGenerationFailed message, IEventContext context)
        {
            var provider = message.Provider ?? "unknown";

            _logger.LogError("Video generation failed for request {RequestId}: {Error} (Provider: {Provider}, Retryable: {IsRetryable}, Retry: {RetryCount}/{MaxRetries})",
                message.RequestId, message.Error, provider, message.IsRetryable, message.RetryCount, message.MaxRetries);

            // Check if task exists (only async tasks will have task records)
            var taskStatus = await _asyncTaskService.GetTaskStatusAsync(message.RequestId, context.CancellationToken);
            if (taskStatus != null)
            {
                var errorDetails = new VideoGenerationFailureResult(
                    message.Error, message.ErrorCode, message.Provider,
                    message.IsRetryable, message.FailedAt);

                await _asyncTaskService.UpdateTaskStatusAsync(
                    message.RequestId,
                    TaskState.Failed,
                    progress: null,
                    errorDetails,
                    message.Error,
                    context.CancellationToken);
            }
            else
            {
                _logger.LogInformation("Sync video generation failed (no task record) for request {RequestId}", message.RequestId);
            }

            // Clear progress cache for this task
            var progressCacheKey = CacheKeys.MediaProgress.VideoProgress(message.RequestId);
            _progressCache.Remove(progressCacheKey);

            // Track per-provider failure count
            var failureCount = MediaGenerationHandlerHelper.TrackFailureMetrics(
                _progressCache, FailureCountCacheKeyPrefix, provider, "video", _logger);
            var providerErrorType = MediaGenerationHandlerHelper.ClassifyFailure(
                message.Error, message.ErrorCode);

            // Log structured metrics for monitoring/alerting pipelines
            _logger.LogInformation("Video generation failure metrics: {@Metrics}", new
            {
                RequestId = message.RequestId,
                Provider = provider,
                ErrorCode = message.ErrorCode ?? "unknown",
                IsRetryable = message.IsRetryable,
                FailedAt = message.FailedAt,
                ErrorType = ProviderErrorClassifier.ToMetricLabel(providerErrorType),
                ProviderFailureCount = failureCount
            });

            // Analyze error patterns for actionable diagnostics
            MediaGenerationHandlerHelper.AnalyzeErrorPattern(
                message.Error, message.RequestId, _logger,
                VideoSpecificErrorPatterns);

            // Send failure notification via notification service
            await _notificationService.NotifyVideoGenerationFailedAsync(
                message.RequestId,
                message.Error,
                message.IsRetryable,
                errorCode: message.ErrorCode,
                retryCount: message.RetryCount,
                maxRetries: message.MaxRetries,
                nextRetryAt: message.NextRetryAt,
                failedAt: message.FailedAt);

            // Log retry state or permanent failure
            if (message.IsRetryable)
            {
                _logger.LogInformation("Video generation failure is retryable for request {RequestId} (Retry {RetryCount}/{MaxRetries})",
                    message.RequestId, message.RetryCount, message.MaxRetries);

                if (message.NextRetryAt.HasValue)
                {
                    _logger.LogInformation("Video generation will be retried at {NextRetryAt} for request {RequestId}",
                        message.NextRetryAt.Value, message.RequestId);
                }
            }
            else
            {
                _logger.LogError("Video generation failed permanently for request {RequestId}: {Error}",
                    message.RequestId, message.Error);
            }

            // Flag critical failures (auth, account, credits) for immediate attention
            if (ProviderErrorClassifier.IsFatal(providerErrorType))
            {
                _logger.LogCritical("Critical video generation failure detected for provider {Provider}: {Error}",
                    provider, message.Error);
            }
        }

        /// <summary>
        /// Video-specific error patterns beyond the common set.
        /// </summary>
        private static readonly Dictionary<string, string> VideoSpecificErrorPatterns = new()
        {
            ["quota"] = "Provider quota exhausted - check account limits",
            ["duration"] = "Requested video duration may exceed provider limits",
            ["resolution"] = "Requested video resolution may not be supported",
            ["codec"] = "Unsupported video codec or output format",
            ["format"] = "Unsupported video format requested"
        };
    }
}
