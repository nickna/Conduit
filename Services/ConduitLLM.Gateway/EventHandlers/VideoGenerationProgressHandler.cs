using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Interfaces;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConduitLLM.Gateway.EventHandlers
{
    /// <summary>
    /// Handles VideoGenerationProgress events to track generation progress and enable real-time updates.
    /// </summary>
    public class VideoGenerationProgressHandler : IEventHandler<VideoGenerationProgress>
    {
        private readonly IAsyncTaskService _asyncTaskService;
        private readonly IMemoryCache _progressCache;
        private readonly IVideoGenerationNotificationService _notificationService;
        private readonly ILogger<VideoGenerationProgressHandler> _logger;

        public VideoGenerationProgressHandler(
            IAsyncTaskService asyncTaskService,
            IMemoryCache progressCache,
            IVideoGenerationNotificationService notificationService,
            ILogger<VideoGenerationProgressHandler> logger)
        {
            _asyncTaskService = asyncTaskService;
            _progressCache = progressCache;
            _notificationService = notificationService;
            _logger = logger;
        }

        public async Task HandleAsync(VideoGenerationProgress message, IEventContext context)
        {
            _logger.LogDebug("Video generation progress for request {RequestId}: {Progress}% - {Status}",
                message.RequestId, message.ProgressPercentage, message.Status);

            // Update progress cache for real-time queries
            var cacheKey = CacheKeys.MediaProgress.VideoProgress(message.RequestId);
            var progressData = new
            {
                RequestId = message.RequestId,
                Status = message.Status,
                ProgressPercentage = message.ProgressPercentage,
                Message = message.Message,
                FramesCompleted = message.FramesCompleted,
                TotalFrames = message.TotalFrames,
                LastUpdated = DateTime.UtcNow
            };

            // Cache progress for 1 hour (long-running tasks)
            _progressCache.Set(cacheKey, progressData, TimeSpan.FromHours(1));

            // Update task status with progress info
            var taskStatus = await _asyncTaskService.GetTaskStatusAsync(message.RequestId, context.CancellationToken);
            if (taskStatus != null)
            {
                // Update progress percentage and message
                taskStatus.Progress = message.ProgressPercentage;
                taskStatus.ProgressMessage = message.Message ?? message.Status;

                var result = taskStatus.Result is JsonElement { ValueKind: JsonValueKind.Object } element
                    ? JsonNode.Parse(element.GetRawText()) as JsonObject ?? new JsonObject()
                    : taskStatus.Result as JsonObject ?? new JsonObject();
                result["progress"] = new JsonObject
                {
                    ["requestId"] = message.RequestId,
                    ["status"] = message.Status,
                    ["progressPercentage"] = message.ProgressPercentage,
                    ["message"] = message.Message,
                    ["framesCompleted"] = message.FramesCompleted,
                    ["totalFrames"] = message.TotalFrames,
                    ["lastUpdated"] = DateTime.UtcNow
                };

                await _asyncTaskService.UpdateTaskStatusAsync(
                    message.RequestId,
                    TaskState.Processing,
                    progress: message.ProgressPercentage,
                    result: result,
                    error: null,
                    cancellationToken: context.CancellationToken);
            }

            // Log significant progress milestones
            LogProgressMilestone(message);

            // Send real-time updates to WebAdmin via notification service
            await _notificationService.NotifyVideoGenerationProgressAsync(
                message.RequestId,
                message.ProgressPercentage,
                message.Status,
                message.Message,
                message.FramesCompleted,
                message.TotalFrames);
        }

        private void LogProgressMilestone(VideoGenerationProgress progress)
        {
            // Log significant milestones for monitoring
            if (progress.ProgressPercentage == 0 && progress.Status.Contains("start", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Video generation started for request {RequestId}", progress.RequestId);
            }
            else if (progress.ProgressPercentage == 25)
            {
                _logger.LogInformation("Video generation 25% complete for request {RequestId}", progress.RequestId);
            }
            else if (progress.ProgressPercentage == 50)
            {
                _logger.LogInformation("Video generation 50% complete for request {RequestId}", progress.RequestId);
            }
            else if (progress.ProgressPercentage == 75)
            {
                _logger.LogInformation("Video generation 75% complete for request {RequestId}", progress.RequestId);
            }
            else if (progress.Status.Contains("encoding", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Video encoding phase for request {RequestId}", progress.RequestId);
            }
            else if (progress.Status.Contains("upload", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Video upload phase for request {RequestId}", progress.RequestId);
            }

            // Log frame progress if available
            if (progress.FramesCompleted.HasValue && progress.TotalFrames.HasValue)
            {
                _logger.LogDebug("Video generation frames: {FramesCompleted}/{TotalFrames} for request {RequestId}",
                    progress.FramesCompleted, progress.TotalFrames, progress.RequestId);
            }
        }
    }
}
