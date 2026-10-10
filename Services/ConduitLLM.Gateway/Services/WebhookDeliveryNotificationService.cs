using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Core.Interfaces;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Service interface for sending webhook delivery notifications through SignalR.
    /// </summary>
    public interface IWebhookDeliveryNotificationService
    {
        /// <summary>
        /// Notifies about a webhook delivery attempt.
        /// </summary>
        Task NotifyDeliveryAttemptAsync(string webhookUrl, string taskId, string taskType, string eventType, int attemptNumber);
        
        /// <summary>
        /// Notifies about a successful webhook delivery.
        /// </summary>
        Task NotifyDeliverySuccessAsync(string webhookUrl, string taskId, int statusCode, long responseTimeMs, int totalAttempts);
        
        /// <summary>
        /// Notifies about a failed webhook delivery.
        /// </summary>
        Task NotifyDeliveryFailureAsync(string webhookUrl, string taskId, string errorMessage, int? statusCode, int attemptNumber, bool isPermanent);
        
        /// <summary>
        /// Notifies about a scheduled retry.
        /// </summary>
        Task NotifyRetryScheduledAsync(string webhookUrl, string taskId, DateTime retryTime, int retryNumber, int maxRetries);
        
        /// <summary>
        /// Notifies about circuit breaker state change.
        /// </summary>
        Task NotifyCircuitBreakerStateChangeAsync(string webhookUrl, string newState, string previousState, string reason, int failureCount);
        
        /// <summary>
        /// Gets current webhook delivery statistics.
        /// </summary>
        Task<WebhookStatistics> GetStatisticsAsync(string period = "last_hour");
        
    }

    /// <summary>
    /// Implementation of webhook delivery notification service.
    /// </summary>
    public class WebhookDeliveryNotificationService : IWebhookDeliveryNotificationService, IHostedService
    {
        private readonly IHubContext<WebhookDeliveryHub> _hubContext;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<WebhookDeliveryNotificationService> _logger;

        
        private Timer? _statisticsTimer;

        public WebhookDeliveryNotificationService(
            IHubContext<WebhookDeliveryHub> hubContext,
            IServiceProvider serviceProvider,
            ILogger<WebhookDeliveryNotificationService> logger)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _statisticsTimer = new Timer(async _ => await BroadcastStatisticsAsync(), null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _statisticsTimer?.Dispose();
            _logger.LogInformation("WebhookDeliveryNotificationService stopped");
            return Task.CompletedTask;
        }

        public async Task NotifyDeliveryAttemptAsync(
            string webhookUrl, 
            string taskId, 
            string taskType, 
            string eventType, 
            int attemptNumber)
        {
            try
            {
                var attempt = new WebhookDeliveryAttempt
                {
                    WebhookId = GenerateWebhookId(webhookUrl, taskId),
                    TaskId = taskId,
                    TaskType = taskType,
                    Url = webhookUrl,
                    EventType = eventType,
                    AttemptNumber = attemptNumber,
                    Timestamp = DateTime.UtcNow
                };
                
                var groupName = SignalRConstants.Groups.Webhook(webhookUrl);
                await _hubContext.Clients.Group(groupName).SendAsync("DeliveryAttempted", attempt);
                
                _logger.LogDebug(
                    "Sent delivery attempt notification for {WebhookUrl}, attempt {AttemptNumber}",
                    DestinationKey(webhookUrl), attemptNumber);
            }
            catch (Exception)
            {
                _logger.LogWarning( "Error sending delivery attempt notification");
            }
        }

        public async Task NotifyDeliverySuccessAsync(
            string webhookUrl, 
            string taskId, 
            int statusCode, 
            long responseTimeMs, 
            int totalAttempts)
        {
            try
            {
                var success = new WebhookDeliverySuccess
                {
                    WebhookId = GenerateWebhookId(webhookUrl, taskId),
                    TaskId = taskId,
                    Url = webhookUrl,
                    StatusCode = statusCode,
                    ResponseTimeMs = responseTimeMs,
                    TotalAttempts = totalAttempts,
                    Timestamp = DateTime.UtcNow
                };
                
                // Broadcast to webhook-specific group
                var groupName = SignalRConstants.Groups.Webhook(webhookUrl);
                await _hubContext.Clients.Group(groupName).SendAsync("DeliverySucceeded", success);
                
                _logger.LogInformation(
                    "Sent delivery success notification for {WebhookUrl}, response time: {ResponseTime}ms",
                    DestinationKey(webhookUrl), responseTimeMs);
            }
            catch (Exception)
            {
                _logger.LogWarning( "Error sending delivery success notification");
            }
        }

        public async Task NotifyDeliveryFailureAsync(
            string webhookUrl, 
            string taskId, 
            string errorMessage, 
            int? statusCode, 
            int attemptNumber, 
            bool isPermanent)
        {
            try
            {
                var failure = new WebhookDeliveryFailure
                {
                    WebhookId = GenerateWebhookId(webhookUrl, taskId),
                    TaskId = taskId,
                    Url = webhookUrl,
                    ErrorMessage = errorMessage,
                    StatusCode = statusCode,
                    AttemptNumber = attemptNumber,
                    IsPermanentFailure = isPermanent,
                    Timestamp = DateTime.UtcNow
                };
                
                // Broadcast to webhook-specific group
                var groupName = SignalRConstants.Groups.Webhook(webhookUrl);
                await _hubContext.Clients.Group(groupName).SendAsync("DeliveryFailed", failure);
                
                _logger.LogWarning(
                    "Sent delivery failure notification for {WebhookUrl}, attempt {AttemptNumber}, permanent: {IsPermanent}",
                    DestinationKey(webhookUrl), attemptNumber, isPermanent);
            }
            catch (Exception)
            {
                _logger.LogWarning( "Error sending delivery failure notification");
            }
        }

        public async Task NotifyRetryScheduledAsync(
            string webhookUrl, 
            string taskId, 
            DateTime retryTime, 
            int retryNumber, 
            int maxRetries)
        {
            try
            {
                var retry = new WebhookRetryInfo
                {
                    WebhookId = GenerateWebhookId(webhookUrl, taskId),
                    DeliveryId = $"{taskId}-retry-{retryNumber}",
                    Url = webhookUrl,
                    EventType = "webhook.delivery",
                    ScheduledAt = retryTime,
                    NextAttemptNumber = retryNumber,
                    DelaySeconds = (retryTime - DateTime.UtcNow).TotalSeconds,
                    Reason = $"Retry {retryNumber} of {maxRetries}"
                };
                
                // Broadcast to webhook-specific group
                var groupName = SignalRConstants.Groups.Webhook(webhookUrl);
                await _hubContext.Clients.Group(groupName).SendAsync("RetryScheduled", retry);
                
                _logger.LogInformation(
                    "Sent retry scheduled notification for {WebhookUrl}, retry {RetryNumber}/{MaxRetries} at {RetryTime}",
                    DestinationKey(webhookUrl), retryNumber, maxRetries, retryTime);
            }
            catch (Exception)
            {
                _logger.LogWarning( "Error sending retry scheduled notification");
            }
        }

        public async Task NotifyCircuitBreakerStateChangeAsync(
            string webhookUrl, 
            string newState, 
            string previousState, 
            string reason, 
            int failureCount)
        {
            try
            {
                var stateChange = new WebhookCircuitBreakerState
                {
                    Url = webhookUrl,
                    CurrentState = newState,
                    PreviousState = previousState,
                    Reason = reason,
                    FailureCount = failureCount,
                    SuccessCount = 0,
                    Timestamp = DateTime.UtcNow
                };
                
                // Broadcast to webhook-specific group and all clients
                var groupName = SignalRConstants.Groups.Webhook(webhookUrl);
                await _hubContext.Clients.Group(groupName).SendAsync("CircuitBreakerStateChanged", stateChange);
                await _hubContext.Clients.All.SendAsync("CircuitBreakerStateChanged", stateChange);
                
                _logger.LogWarning(
                    "Circuit breaker state changed for {WebhookUrl}: {PreviousState} -> {NewState}, reason: {Reason}",
                    DestinationKey(webhookUrl), previousState, newState, reason);
            }
            catch (Exception)
            {
                _logger.LogWarning( "Error sending circuit breaker state change notification");
            }
        }

        public async Task<WebhookStatistics> GetStatisticsAsync(string period = "last_hour")
        {
            using var scope = _serviceProvider.CreateScope();
            var backlog = await scope.ServiceProvider.GetRequiredService<IWebhookRecovery>().BacklogAsync(CancellationToken.None);
            WebhookDeliveryTelemetry.Snapshot(backlog);
            var total = backlog.Pending + backlog.Delivered + backlog.Exhausted;
            return new WebhookStatistics
            {
                Period = "retained", TotalDeliveries = (int)Math.Min(int.MaxValue, total),
                PendingDeliveries = (int)Math.Min(int.MaxValue, backlog.Pending),
                SuccessfulDeliveries = (int)Math.Min(int.MaxValue, backlog.Delivered),
                FailedDeliveries = (int)Math.Min(int.MaxValue, backlog.Exhausted),
                SuccessRate = total == 0 ? 0 : 100.0 * backlog.Delivered / total
            };
        }

        private async Task BroadcastStatisticsAsync()
        {
            try
            {
                var stats = await GetStatisticsAsync();
                await _hubContext.Clients.All.SendAsync("DeliveryStatisticsUpdated", stats);
            }
            catch (Exception)
            {
                _logger.LogWarning( "Error broadcasting webhook statistics");
            }
        }

        private static string DestinationKey(string url) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16];

        private static string GenerateWebhookId(string webhookUrl, string taskId)
        {
            return $"{taskId}_{DestinationKey(webhookUrl)}";
        }

    }
}
