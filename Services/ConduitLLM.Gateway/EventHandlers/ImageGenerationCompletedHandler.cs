using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

using ConduitLLM.Gateway.Interfaces;
namespace ConduitLLM.Gateway.EventHandlers
{
    /// <summary>
    /// Handles ImageGenerationCompleted events to update task status, trigger post-processing, and send notifications.
    /// </summary>
    public class ImageGenerationCompletedHandler : IEventHandler<ImageGenerationCompleted>
    {
        private readonly IAsyncTaskService _asyncTaskService;
        private readonly IMemoryCache _progressCache;
        private readonly IImageGenerationNotificationService _notificationService;
        private readonly ILogger<ImageGenerationCompletedHandler> _logger;
        private const string CompletedTasksCacheKey = "completed_image_tasks";

        public ImageGenerationCompletedHandler(
            IAsyncTaskService asyncTaskService,
            IMemoryCache progressCache,
            IImageGenerationNotificationService notificationService,
            ILogger<ImageGenerationCompletedHandler> logger)
        {
            _asyncTaskService = asyncTaskService;
            _progressCache = progressCache;
            _notificationService = notificationService;
            _logger = logger;
        }

        public async Task HandleAsync(ImageGenerationCompleted message, IEventContext context)
        {

            _logger.LogInformation("Processing image generation completion for task {TaskId}: {ImageCount} images generated in {Duration}s (cost: ${Cost})",
                message.TaskId, message.Images.Count(), message.Duration.TotalSeconds, message.Cost);

            // Update task status to completed (if async task record exists)
            var taskStatus = await _asyncTaskService.GetTaskStatusAsync(message.TaskId, context.CancellationToken);
            if (taskStatus != null)
            {
                var images = new JsonArray();
                foreach (var image in message.Images)
                {
                    images.Add((JsonNode?)new JsonObject
                    {
                        ["url"] = image.Url,
                        ["revisedPrompt"] = image.RevisedPrompt
                    });
                }

                var result = new JsonObject
                {
                    ["images"] = images,
                    ["imageCount"] = message.Images.Count(),
                    ["provider"] = message.Provider,
                    ["model"] = message.Model,
                    ["duration"] = message.Duration.TotalSeconds,
                    ["cost"] = message.Cost
                };

                await _asyncTaskService.UpdateTaskStatusAsync(
                    message.TaskId,
                    TaskState.Completed,
                    progress: 100,
                    result: result,
                    error: null,
                    cancellationToken: context.CancellationToken);
            }

            // Clear progress cache for this task
            var progressCacheKey = CacheKeys.MediaProgress.ImageProgress(message.TaskId);
            _progressCache.Remove(progressCacheKey);

            // Store completion info for analytics and audit
            var completionData = new
            {
                TaskId = message.TaskId,
                VirtualKeyId = message.VirtualKeyId,
                ImageCount = message.Images.Count(),
                Provider = message.Provider,
                Model = message.Model,
                Duration = message.Duration.TotalSeconds,
                Cost = message.Cost,
                CompletedAt = DateTime.UtcNow,
                ImageUrls = message.Images.Select(img => img.Url).ToList()
            };

            // Cache completion data for recent tasks (24 hours)
            MediaGenerationHandlerHelper.UpdateCompletedTasksCache(_progressCache, CompletedTasksCacheKey, completionData);

            // Log performance metrics
            var avgTimePerImage = message.Duration.TotalSeconds / Math.Max(1, message.Images.Count());
            _logger.LogInformation("Image generation performance - Provider: {Provider}, Model: {Model}, Avg time per image: {AvgTime}s, Total cost: ${Cost}",
                LoggingSanitizer.S(message.Provider), LoggingSanitizer.S(message.Model), avgTimePerImage, message.Cost);

            // Track provider-specific metrics
            LogProviderMetrics(message.Provider, message.Model, message.Images.Count(), message.Duration, message.Cost);

            // Send completion notification to WebAdmin
            await _notificationService.NotifyImageGenerationCompletedAsync(
                message.TaskId,
                message.Images.Select(img => img.Url ?? string.Empty).ToArray(),
                message.Duration,
                message.Cost);
        }

        private void LogProviderMetrics(string provider, string model, int imageCount, TimeSpan duration, decimal cost)
        {
            // Log provider-specific metrics for monitoring and optimization
            var metrics = new Dictionary<string, object>
            {
                ["provider"] = provider,
                ["model"] = model,
                ["image_count"] = imageCount,
                ["duration_seconds"] = duration.TotalSeconds,
                ["cost"] = cost,
                ["cost_per_image"] = imageCount > 0 ? cost / imageCount : 0,
                ["images_per_second"] = duration.TotalSeconds > 0 ? imageCount / duration.TotalSeconds : 0
            };

            _logger.LogInformation("Image generation metrics: {Metrics}", metrics);
        }
    }
}
