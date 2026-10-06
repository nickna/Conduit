using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Serialization;

using Microsoft.Extensions.Logging;

using System.Text.Json;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Orchestrates video generation progress tracking through event-driven architecture.
    /// Replaces the fire-and-forget anti-pattern with proper event handling.
    /// </summary>
    public class VideoProgressTrackingOrchestrator : IEventHandler<VideoProgressCheckRequested>
    {
        private readonly IAsyncTaskService _taskService;
        private readonly IEventBus _eventBus;
        private readonly IWebhookNotificationService _webhookService;
        private readonly ILogger<VideoProgressTrackingOrchestrator> _logger;

        private static readonly int[] ProgressIntervals = { 10, 30, 50, 70, 90 };
        private const int ProgressCheckDelaySeconds = 12; // ~1 minute per 10% progress

        public VideoProgressTrackingOrchestrator(
            IAsyncTaskService taskService,
            IEventBus eventBus,
            IWebhookNotificationService webhookService,
            ILogger<VideoProgressTrackingOrchestrator> logger)
        {
            _taskService = taskService;
            _eventBus = eventBus;
            _webhookService = webhookService;
            _logger = logger;
        }

        public async Task HandleAsync(VideoProgressCheckRequested request, IEventContext context)
        {
            try
            {
                // Check if task is still running
                var taskStatus = await _taskService.GetTaskStatusAsync(request.RequestId);
                if (taskStatus == null)
                {
                    _logger.LogDebug("Task {RequestId} not found, stopping progress tracking", request.RequestId);
                    return;
                }
                
                // Stop tracking if task is no longer processing
                if (taskStatus.State != TaskState.Processing)
                {
                    _logger.LogDebug("Task {RequestId} is no longer processing (state: {State}), stopping progress tracking", 
                        request.RequestId, taskStatus.State);
                    
                    return;
                }
                
                // Calculate elapsed time since start
                var elapsed = DateTime.UtcNow - request.StartTime;
                
                // Check if we should report progress for the current interval
                if (request.IntervalIndex < ProgressIntervals.Length && 
                    elapsed.TotalSeconds >= request.IntervalIndex * ProgressCheckDelaySeconds)
                {
                    var progress = ProgressIntervals[request.IntervalIndex];
                    
                    // Update task progress
                    await _taskService.UpdateTaskStatusAsync(
                        request.RequestId, 
                        TaskState.Processing, 
                        progress: progress, 
                        cancellationToken: context.CancellationToken);
                    
                    // Publish progress event
                    await _eventBus.PublishAsync(new VideoGenerationProgress
                    {
                        RequestId = request.RequestId,
                        ProgressPercentage = progress,
                        Status = GetProgressStatus(progress),
                        Message = GetProgressMessage(progress),
                        CorrelationId = request.CorrelationId
                    });
                    
                    _logger.LogInformation("Video generation progress for {RequestId}: {Progress}%", 
                        request.RequestId, progress);
                    
                    // Send webhook notification if configured
                    if (taskStatus.Metadata is IDictionary<string, object> metadata)
                    {
                        if (metadata.TryGetValue("webhook_url", out var webhookUrlObj) && 
                            webhookUrlObj is string webhookUrl && 
                            !string.IsNullOrEmpty(webhookUrl))
                        {
                            string? webhookHeaders = null;
                            if (metadata.TryGetValue("webhook_headers", out var headersObj))
                            {
                                webhookHeaders = headersObj as string;
                            }
                            
                            await SendProgressWebhook(request.RequestId, progress, webhookUrl, webhookHeaders);
                        }
                    }
                    
                    // Schedule next progress check if not at final interval
                    if (request.IntervalIndex + 1 < ProgressIntervals.Length)
                    {
                        var nextCheck = new VideoProgressCheckRequested
                        {
                            RequestId = request.RequestId,
                            VirtualKeyId = request.VirtualKeyId,
                            ScheduledAt = DateTime.UtcNow.AddSeconds(5), // Check again in 5 seconds
                            IntervalIndex = request.IntervalIndex + 1,
                            TotalIntervals = ProgressIntervals.Length,
                            StartTime = request.StartTime,
                            CorrelationId = request.CorrelationId
                        };
                        
                        await _eventBus.PublishAsync(nextCheck);
                        _logger.LogDebug("Scheduled next progress check for {RequestId} at interval {IntervalIndex}", 
                            request.RequestId, nextCheck.IntervalIndex);
                    }
                }
                else
                {
                    // Not time for next interval yet, schedule another check
                    var nextCheck = request with
                    {
                        ScheduledAt = DateTime.UtcNow.AddSeconds(5)
                    };
                    
                    await _eventBus.PublishAsync(nextCheck);
                    _logger.LogDebug("Rescheduled progress check for {RequestId}, waiting for interval {IntervalIndex}", 
                        request.RequestId, request.IntervalIndex);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing progress check for {RequestId}", request.RequestId);
                
                // Don't rethrow - we don't want to retry progress checks
                // The main task will handle actual failures
            }
        }
        
        private static string GetProgressStatus(int progress)
        {
            return progress switch
            {
                <= 10 => "initializing",
                <= 30 => "processing_frames",
                <= 50 => "generating_video",
                <= 70 => "encoding",
                <= 90 => "finalizing",
                _ => "completing"
            };
        }
        
        private static string GetProgressMessage(int progress)
        {
            return progress switch
            {
                10 => "Video generation initialized",
                30 => "Processing video frames",
                50 => "Generating video content",
                70 => "Encoding video",
                90 => "Finalizing video generation",
                _ => "Completing video generation"
            };
        }
        
        private async Task SendProgressWebhook(string requestId, int progress, string webhookUrl, string? webhookHeaders)
        {
            try
            {
                var webhookPayload = new
                {
                    TaskId = requestId,
                    Status = "processing",
                    ProgressPercentage = progress,
                    Message = GetProgressMessage(progress),
                    EstimatedSecondsRemaining = (int)((100 - progress) * 0.6) // Rough estimate
                };

                // Parse webhook headers from JSON string if provided
                Dictionary<string, string>? headers = null;
                if (!string.IsNullOrEmpty(webhookHeaders))
                {
                    try
                    {
                        headers = JsonSerializer.Deserialize(
                            webhookHeaders,
                            CoreInternalJsonContext.Default.DictionaryStringString);
                    }
                    catch
                    {
                        _logger.LogWarning("Failed to parse webhook headers for {RequestId}", requestId);
                    }
                }

                // Publish webhook delivery event for scalable processing
                await _eventBus.PublishAsync(new WebhookDeliveryRequested
                {
                    TaskId = requestId,
                    TaskType = "video",
                    WebhookUrl = webhookUrl,
                    EventType = WebhookEventType.TaskProgress,
                    PayloadJson = ConduitLLM.Core.Helpers.WebhookPayloadHelper.SerializePayload(webhookPayload),
                    Headers = headers,
                    CorrelationId = Guid.NewGuid().ToString()
                });
                
                _logger.LogDebug("Published webhook delivery event for video progress: {RequestId} at {Progress}%", requestId, progress);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish progress webhook event for {RequestId}", requestId);
                // Don't fail the progress check due to webhook failures
            }
        }
    }
}
