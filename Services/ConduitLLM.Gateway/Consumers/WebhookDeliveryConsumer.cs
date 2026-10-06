using System.Diagnostics;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Services;

namespace ConduitLLM.Gateway.Consumers
{
    /// <summary>
    /// Event handler for processing webhook delivery requests
    /// Handles deduplication, retry logic (via deferred/scheduled publish), and delivery tracking
    ///
    /// For detailed architecture and troubleshooting information, see:
    /// - Architecture: docs/architecture/webhook-delivery-system.md
    /// - Operations: docs/operations/webhook-monitoring.md
    /// </summary>
    public class WebhookDeliveryConsumer : IEventHandler<WebhookDeliveryRequested>
    {
        private readonly IWebhookNotificationService _webhookService;
        private readonly IWebhookDeliveryTracker _deliveryTracker;
        private readonly IWebhookCircuitBreaker _circuitBreaker;
        private readonly IWebhookDeliveryNotificationService _notificationService;
        private readonly ILogger<WebhookDeliveryConsumer> _logger;
        private const int MAX_RETRY_COUNT = 3;
        private const int WEBHOOK_TIMEOUT_SECONDS = 10;
        
        public WebhookDeliveryConsumer(
            IWebhookNotificationService webhookService,
            IWebhookDeliveryTracker deliveryTracker,
            IWebhookCircuitBreaker circuitBreaker,
            IWebhookDeliveryNotificationService notificationService,
            ILogger<WebhookDeliveryConsumer> logger)
        {
            _webhookService = webhookService ?? throw new ArgumentNullException(nameof(webhookService));
            _deliveryTracker = deliveryTracker ?? throw new ArgumentNullException(nameof(deliveryTracker));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        
        public async Task HandleAsync(WebhookDeliveryRequested request, IEventContext context)
        {
            // Create unique delivery key for deduplication
            var deliveryKey = $"{request.TaskId}:{request.EventType}:{context.MessageId}";
            
            _logger.LogInformation(
                "Processing webhook delivery request: TaskId={TaskId}, Type={TaskType}, Event={EventType}, RetryCount={RetryCount}",
                request.TaskId, request.TaskType, request.EventType, request.RetryCount);
            
            // Notify about delivery attempt
            await _notificationService.NotifyDeliveryAttemptAsync(
                request.WebhookUrl,
                request.TaskId,
                request.TaskType,
                request.EventType.ToString(),
                request.RetryCount + 1);
            
            try
            {
                // Check if already delivered
                if (await _deliveryTracker.IsDeliveredAsync(deliveryKey))
                {
                    _logger.LogDebug(
                        "Webhook already delivered for {DeliveryKey}, skipping duplicate delivery",
                        deliveryKey);
                    return;
                }
                
                // Check circuit breaker
                if (_circuitBreaker.IsOpen(request.WebhookUrl))
                {
                    var stats = _circuitBreaker.GetStats(request.WebhookUrl);
                    _logger.LogWarning(
                        "Circuit breaker is open for webhook URL {WebhookUrl}. " +
                        "Skipping delivery for task {TaskId}. Circuit opened at {OpenedAt}",
                        request.WebhookUrl, request.TaskId, stats.CircuitOpenedAt);
                    
                    // Record as failed but don't retry
                    await _deliveryTracker.RecordFailureAsync(
                        deliveryKey, 
                        request.WebhookUrl, 
                        "Circuit breaker open - endpoint temporarily disabled");
                    
                    // Notify about circuit breaker blocking
                    await _notificationService.NotifyDeliveryFailureAsync(
                        request.WebhookUrl,
                        request.TaskId,
                        "Circuit breaker open - endpoint temporarily disabled",
                        null,
                        request.RetryCount + 1,
                        true);
                    
                    // Don't throw - just abandon this delivery
                    return;
                }
                
                // Determine which method to call based on event type
                WebhookSendResult sendResult;
                var stopwatch = Stopwatch.StartNew();

                if (request.EventType == WebhookEventType.TaskProgress)
                {
                    var payload = ParseWebhookPayload(request.PayloadJson);

                    sendResult = await _webhookService.SendTaskProgressWebhookAsync(
                        request.WebhookUrl,
                        payload,
                        request.Headers,
                        context.CancellationToken);
                }
                else
                {
                    var payload = ParseWebhookPayload(request.PayloadJson);

                    sendResult = await _webhookService.SendTaskCompletionWebhookAsync(
                        request.WebhookUrl,
                        payload,
                        request.Headers,
                        context.CancellationToken);
                }

                stopwatch.Stop();

                if (sendResult.Success)
                {
                    // Mark as delivered
                    await _deliveryTracker.MarkDeliveredAsync(deliveryKey, request.WebhookUrl);

                    // Record success in circuit breaker
                    _circuitBreaker.RecordSuccess(request.WebhookUrl);

                    // Notify about successful delivery with the endpoint's actual status code
                    await _notificationService.NotifyDeliverySuccessAsync(
                        request.WebhookUrl,
                        request.TaskId,
                        sendResult.StatusCode ?? 0,
                        stopwatch.ElapsedMilliseconds,
                        request.RetryCount + 1);
                    
                    _logger.LogInformation(
                        "Successfully delivered webhook for TaskId={TaskId}, Event={EventType} to {WebhookUrl}",
                        request.TaskId, request.EventType, request.WebhookUrl);
                }
                else if (request.RetryCount < MAX_RETRY_COUNT)
                {
                    // Schedule retry with exponential backoff
                    var retryDelay = TimeSpan.FromSeconds(Math.Pow(2, request.RetryCount + 1));
                    var retryTime = DateTime.UtcNow.Add(retryDelay);
                    
                    _logger.LogWarning(
                        "Webhook delivery failed for TaskId={TaskId}, scheduling retry {RetryCount}/{MaxRetry} in {RetryDelay}s",
                        request.TaskId, request.RetryCount + 1, MAX_RETRY_COUNT, retryDelay.TotalSeconds);
                    
                    // Notify about failure and retry, with the endpoint's actual status code
                    await _notificationService.NotifyDeliveryFailureAsync(
                        request.WebhookUrl,
                        request.TaskId,
                        $"Delivery failed ({sendResult.Error}), retry scheduled",
                        sendResult.StatusCode,
                        request.RetryCount + 1,
                        false);
                    
                    await _notificationService.NotifyRetryScheduledAsync(
                        request.WebhookUrl,
                        request.TaskId,
                        retryTime,
                        request.RetryCount + 1,
                        MAX_RETRY_COUNT);
                    
                    // Schedule a new message with incremented retry count
                    await context.SchedulePublishAsync(
                        retryTime,
                        new WebhookDeliveryRequested
                        {
                            EventId = request.EventId,
                            Timestamp = request.Timestamp,
                            CorrelationId = request.CorrelationId,
                            TaskId = request.TaskId,
                            TaskType = request.TaskType,
                            WebhookUrl = request.WebhookUrl,
                            EventType = request.EventType,
                            PayloadJson = request.PayloadJson,
                            Headers = request.Headers,
                            RetryCount = request.RetryCount + 1,
                            NextRetryAt = retryTime
                        });
                    
                    // Record the failure
                    await _deliveryTracker.RecordFailureAsync(
                        deliveryKey, 
                        request.WebhookUrl, 
                        "Delivery failed, retry scheduled");
                    
                    // Record failure in circuit breaker
                    _circuitBreaker.RecordFailure(request.WebhookUrl);
                }
                else
                {
                    // Max retries reached
                    _logger.LogError(
                        "Webhook delivery failed after {RetryCount} attempts for TaskId={TaskId} to {WebhookUrl}",
                        request.RetryCount, request.TaskId, request.WebhookUrl);
                    
                    // Notify about permanent failure with the endpoint's actual status code
                    await _notificationService.NotifyDeliveryFailureAsync(
                        request.WebhookUrl,
                        request.TaskId,
                        $"Max retries ({MAX_RETRY_COUNT}) exceeded ({sendResult.Error})",
                        sendResult.StatusCode,
                        request.RetryCount + 1,
                        true);
                    
                    // Record final failure
                    await _deliveryTracker.RecordFailureAsync(
                        deliveryKey, 
                        request.WebhookUrl, 
                        $"Max retries ({MAX_RETRY_COUNT}) exceeded");
                    
                    // Record failure in circuit breaker
                    _circuitBreaker.RecordFailure(request.WebhookUrl);
                    
                    throw new NonRetryableMessageException(
                        $"Webhook delivery failed after {MAX_RETRY_COUNT} attempts to {request.WebhookUrl}");
                }
            }
            catch (Exception ex) when (ex is not NonRetryableMessageException)
            {
                _logger.LogError(ex, 
                    "Unexpected error processing webhook delivery for TaskId={TaskId}",
                    request.TaskId);
                
                if (request.RetryCount < MAX_RETRY_COUNT)
                {
                    var retryDelay = TimeSpan.FromSeconds(Math.Pow(2, request.RetryCount + 1));
                    var retryTime = DateTime.UtcNow.Add(retryDelay);
                    await _notificationService.NotifyDeliveryFailureAsync(
                        request.WebhookUrl, request.TaskId, $"Unexpected error: {ex.Message}", null,
                        request.RetryCount + 1, false);
                    await _notificationService.NotifyRetryScheduledAsync(
                        request.WebhookUrl, request.TaskId, retryTime,
                        request.RetryCount + 1, MAX_RETRY_COUNT);
                    await context.SchedulePublishAsync(retryTime, request with
                    {
                        RetryCount = request.RetryCount + 1,
                        NextRetryAt = retryTime
                    });
                    await _deliveryTracker.RecordFailureAsync(deliveryKey, request.WebhookUrl, ex.Message);
                    _circuitBreaker.RecordFailure(request.WebhookUrl);
                    return;
                }

                var finalError = $"Max retries ({MAX_RETRY_COUNT}) exceeded: {ex.Message}";
                await _notificationService.NotifyDeliveryFailureAsync(
                    request.WebhookUrl, request.TaskId, finalError, null,
                    request.RetryCount + 1, true);
                await _deliveryTracker.RecordFailureAsync(deliveryKey, request.WebhookUrl, finalError);
                _circuitBreaker.RecordFailure(request.WebhookUrl);
                throw new NonRetryableMessageException(
                    $"Webhook delivery failed after {MAX_RETRY_COUNT} retries to {request.WebhookUrl}");
            }
        }

        private static System.Text.Json.JsonElement ParseWebhookPayload(string payloadJson)
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                return document.RootElement.Clone();
            }

            using var fallback = System.Text.Json.JsonDocument.Parse(
                """{"error":"Failed to deserialize payload"}""");
            return fallback.RootElement.Clone();
        }
    }
}
