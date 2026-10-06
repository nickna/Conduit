using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Gateway.Interfaces;
namespace ConduitLLM.Gateway.EventHandlers
{
    /// <summary>
    /// Handles ImageGenerationProgress events to track generation progress and enable real-time updates.
    /// </summary>
    public class ImageGenerationProgressHandler : IEventHandler<ImageGenerationProgress>
    {
        private readonly IMemoryCache _progressCache;
        private readonly IAsyncTaskService _taskService;
        private readonly IImageGenerationNotificationService _notificationService;
        private readonly ILogger<ImageGenerationProgressHandler> _logger;

        public ImageGenerationProgressHandler(
            IMemoryCache progressCache,
            IAsyncTaskService taskService,
            IImageGenerationNotificationService notificationService,
            ILogger<ImageGenerationProgressHandler> logger)
        {
            _progressCache = progressCache;
            _taskService = taskService;
            _notificationService = notificationService;
            _logger = logger;
        }

        public async Task HandleAsync(ImageGenerationProgress message, IEventContext context)
        {

            _logger.LogInformation("Processing image generation progress for task {TaskId}: {Status} ({ImagesCompleted}/{TotalImages})",
                message.TaskId, message.Status, message.ImagesCompleted, message.TotalImages);

            // Update progress cache for real-time queries
            var cacheKey = CacheKeys.MediaProgress.ImageProgress(message.TaskId);
            var progressData = new
            {
                TaskId = message.TaskId,
                Status = message.Status,
                ImagesCompleted = message.ImagesCompleted,
                TotalImages = message.TotalImages,
                ProgressPercentage = message.ProgressPercentage,
                Message = message.Message,
                LastUpdated = DateTime.UtcNow
            };

            // Cache progress for 1 hour (long-running tasks)
            _progressCache.Set(cacheKey, progressData, TimeSpan.FromHours(1));

            // Update task metadata with progress info
            var taskStatus = await _taskService.GetTaskStatusAsync(message.TaskId);
            if (taskStatus != null)
            {
                var result = taskStatus.Result is JsonElement { ValueKind: JsonValueKind.Object } element
                    ? JsonNode.Parse(element.GetRawText()) as JsonObject ?? new JsonObject()
                    : taskStatus.Result as JsonObject ?? new JsonObject();
                result["progress"] = new JsonObject
                {
                    ["taskId"] = message.TaskId,
                    ["status"] = message.Status,
                    ["imagesCompleted"] = message.ImagesCompleted,
                    ["totalImages"] = message.TotalImages,
                    ["progressPercentage"] = message.ProgressPercentage,
                    ["message"] = message.Message,
                    ["lastUpdated"] = DateTime.UtcNow
                };
                await _taskService.UpdateTaskStatusAsync(
                    message.TaskId,
                    taskStatus.State,
                    progress: null,
                    result: result,
                    cancellationToken: context.CancellationToken);
            }

            // Track generation metrics
            if (message.Status == "processing" && message.ImagesCompleted == 0)
            {
                _logger.LogInformation("Image generation started for task {TaskId} - generating {TotalImages} images",
                    message.TaskId, message.TotalImages);
            }
            else if (message.Status == "storing")
            {
                _logger.LogDebug("Storing image {ImagesCompleted} of {TotalImages} for task {TaskId}",
                    message.ImagesCompleted + 1, message.TotalImages, message.TaskId);
            }

            // Send real-time updates to WebAdmin
            await _notificationService.NotifyImageGenerationProgressAsync(
                message.TaskId,
                message.ProgressPercentage,
                message.Status,
                message.ImagesCompleted,
                message.TotalImages,
                message.Message);

        }
    }
}
