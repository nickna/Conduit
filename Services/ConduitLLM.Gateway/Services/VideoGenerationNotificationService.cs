using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Services;
using ConduitLLM.Contracts.SignalR;
using ConduitLLM.Gateway.Interfaces;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Implementation of video generation notification service using SignalR.
    /// Inherits from SignalRNotificationServiceBase for common functionality.
    /// </summary>
    public class VideoGenerationNotificationService
        : SignalRNotificationServiceBase<VideoGenerationHub>,
          IVideoGenerationNotificationService
    {
        private readonly IHubContext<PublicVideoGenerationHub> _publicHubContext;

        public VideoGenerationNotificationService(
            IHubContext<VideoGenerationHub> hubContext,
            IHubContext<PublicVideoGenerationHub> publicHubContext,
            ILogger<VideoGenerationNotificationService> logger)
            : base(hubContext, logger)
        {
            _publicHubContext = publicHubContext ?? throw new ArgumentNullException(nameof(publicHubContext));
        }

        public async Task NotifyVideoGenerationStartedAsync(string requestId, string provider, DateTime startedAt, int? estimatedSeconds)
        {
            var groupName = SignalRConstants.Groups.VideoTask(requestId);

            await SendToGroupAsync(groupName, SignalRConstants.ClientMethods.VideoGenerationStarted,
                new VideoGenerationStartedNotification(requestId, provider, startedAt, estimatedSeconds));
            await SendToPublicGroupAsync(groupName, SignalRConstants.ClientMethods.TaskProgress,
                new PublicVideoGenerationStartedNotification(requestId, "started", 0,
                    $"Video generation started with {provider}", startedAt, estimatedSeconds));

            Logger.LogDebug("Sent VideoGenerationStarted notification for task {TaskId}", requestId);
        }

        public async Task NotifyVideoGenerationProgressAsync(string requestId, int progressPercentage, string status, string? message = null, int? framesCompleted = null, int? totalFrames = null)
        {
            var groupName = SignalRConstants.Groups.VideoTask(requestId);

            await SendToGroupAsync(groupName, SignalRConstants.ClientMethods.VideoGenerationProgress,
                new VideoGenerationProgressNotification(requestId, progressPercentage, status, message,
                    framesCompleted, totalFrames, DateTime.UtcNow));
            await SendToPublicGroupAsync(groupName, SignalRConstants.ClientMethods.TaskProgress,
                new PublicVideoGenerationProgressNotification(requestId, status, progressPercentage, message,
                    framesCompleted, totalFrames, DateTime.UtcNow));

            Logger.LogDebug("Sent VideoGenerationProgress notification for task {TaskId}: {Progress}%",
                requestId, progressPercentage);
        }

        public async Task NotifyVideoGenerationCompletedAsync(string requestId, string videoUrl, TimeSpan duration, decimal cost, string? previewUrl = null, string? resolution = null, long? fileSize = null, string? provider = null, string? model = null, DateTime? completedAt = null, double? generationDurationSeconds = null)
        {
            var groupName = SignalRConstants.Groups.VideoTask(requestId);

            await SendToGroupAsync(groupName, SignalRConstants.ClientMethods.VideoGenerationCompleted,
                new VideoGenerationCompletedNotification(requestId, "completed", videoUrl, previewUrl,
                    duration.TotalSeconds, resolution, fileSize, cost, provider, model,
                    completedAt ?? DateTime.UtcNow, generationDurationSeconds));
            await SendToPublicGroupAsync(groupName, SignalRConstants.ClientMethods.TaskCompleted,
                new PublicVideoGenerationCompletedNotification(requestId, videoUrl));

            Logger.LogDebug("Sent VideoGenerationCompleted notification for task {TaskId}", requestId);
        }

        public async Task NotifyVideoGenerationFailedAsync(string requestId, string error, bool isRetryable, string? errorCode = null, int? retryCount = null, int? maxRetries = null, DateTime? nextRetryAt = null, DateTime? failedAt = null)
        {
            var groupName = SignalRConstants.Groups.VideoTask(requestId);

            await SendToGroupAsync(groupName, SignalRConstants.ClientMethods.VideoGenerationFailed,
                new VideoGenerationFailedNotification(requestId, "failed", error, errorCode, isRetryable,
                    retryCount, maxRetries, nextRetryAt, failedAt ?? DateTime.UtcNow));
            await SendToPublicGroupAsync(groupName, SignalRConstants.ClientMethods.TaskFailed,
                new PublicVideoGenerationFailedNotification(requestId, error));

            Logger.LogDebug("Sent VideoGenerationFailed notification for task {TaskId}", requestId);
        }

        public async Task NotifyVideoGenerationCancelledAsync(string requestId, string? reason)
        {
            var groupName = SignalRConstants.Groups.VideoTask(requestId);

            await SendToGroupAsync(groupName, SignalRConstants.ClientMethods.VideoGenerationCancelled,
                new VideoGenerationCancelledNotification(requestId, reason, DateTime.UtcNow));
            await SendToPublicGroupAsync(groupName, SignalRConstants.ClientMethods.TaskFailed,
                new PublicVideoGenerationFailedNotification(requestId, reason ?? "Video generation was cancelled."));

            Logger.LogDebug("Sent VideoGenerationCancelled notification for task {TaskId}", requestId);
        }

        private async Task SendToPublicGroupAsync(string groupName, string methodName, object payload)
        {
            try
            {
                await _publicHubContext.Clients.Group(groupName).SendAsync(methodName, payload);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    "Failed to send {MethodName} notification to public video group {GroupName}",
                    methodName,
                    groupName);
            }
        }
    }
}
