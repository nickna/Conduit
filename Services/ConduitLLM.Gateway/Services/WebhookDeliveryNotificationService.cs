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
        Task NotifyDeliveryAttemptAsync(int virtualKeyId, string webhookUrl, string taskId, string taskType, string eventType, int attemptNumber);
        
        /// <summary>
        /// Notifies about a successful webhook delivery.
        /// </summary>
        Task NotifyDeliverySuccessAsync(int virtualKeyId, string webhookUrl, string taskId, int statusCode, long responseTimeMs, int totalAttempts);
        
        /// <summary>
        /// Notifies about a failed webhook delivery.
        /// </summary>
        Task NotifyDeliveryFailureAsync(int virtualKeyId, string webhookUrl, string taskId, string errorMessage, int? statusCode, int attemptNumber, bool isPermanent);
        
        /// <summary>
        /// Notifies about a scheduled retry.
        /// </summary>
        Task NotifyRetryScheduledAsync(int virtualKeyId, string webhookUrl, string taskId, DateTime retryTime, int retryNumber, int maxRetries);
        
        /// <summary>
        /// Notifies about circuit breaker state change.
        /// </summary>
        Task NotifyCircuitBreakerStateChangeAsync(int virtualKeyId, string webhookUrl, string newState, string previousState, string reason, int failureCount);
        
        /// <summary>
        /// Gets current webhook delivery statistics.
        /// </summary>
        Task<WebhookStatistics> GetStatisticsAsync(string period = "last_hour");
        
    }

    /// <summary>
    /// Implementation of webhook delivery notification service.
    /// </summary>
    public class WebhookDeliveryNotificationService : IWebhookDeliveryNotificationService, IHostedService, IDisposable
    {
        private readonly IHubContext<WebhookDeliveryHub> _hubContext;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<WebhookDeliveryNotificationService> _logger;

        
        private readonly TimeProvider _clock;
        private readonly SemaphoreSlim _generationLock = new(1, 1);
        private readonly object _generationSync = new();
        private PeriodicTimer? _statisticsTimer;
        private CancellationTokenSource? _statisticsCts;
        private Task? _statisticsWorker;
        private int _disposed;

        public WebhookDeliveryNotificationService(
            IHubContext<WebhookDeliveryHub> hubContext,
            IServiceProvider serviceProvider,
            ILogger<WebhookDeliveryNotificationService> logger,
            TimeProvider? clock = null)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? TimeProvider.System;

        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _generationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_generationSync)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    if (_statisticsWorker is not null) return;
                    _statisticsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _statisticsTimer = new PeriodicTimer(TimeSpan.FromMinutes(1), _clock);
                    _statisticsWorker = RunStatisticsAsync(_statisticsTimer, _statisticsCts.Token);
                }
            }
            finally { _generationLock.Release(); }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _generationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Task worker;
                CancellationTokenSource cancellation;
                lock (_generationSync)
                {
                    if (_statisticsWorker is null) return;
                    worker = _statisticsWorker;
                    cancellation = _statisticsCts!;
                }
                try
                {
                    try { await cancellation.CancelAsync().ConfigureAwait(false); }
                    finally { await worker.ConfigureAwait(false); }
                }
                finally
                {
                    lock (_generationSync)
                    {
                        _statisticsTimer!.Dispose();
                        cancellation.Dispose();
                        _statisticsTimer = null;
                        _statisticsCts = null;
                        _statisticsWorker = null;
                    }
                }
                _logger.LogInformation("WebhookDeliveryNotificationService stopped");
            }
            finally { _generationLock.Release(); }
        }

        private async Task RunStatisticsAsync(PeriodicTimer timer, CancellationToken cancellationToken)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    await BroadcastStatisticsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_generationSync)
            {
                // Close the timer locally, but retain cancellation until Stop joins a held
                // callback. A later Stop can then drain safely without touching a disposed CTS.
                _statisticsTimer?.Dispose();
                try { _statisticsCts?.Cancel(); }
                catch (Exception exception) { _logger.LogWarning(exception, "Webhook statistics cancellation failed"); }
            }
        }

        public async Task NotifyDeliveryAttemptAsync(
            int virtualKeyId,
            string webhookUrl, 
            string taskId, 
            string taskType, 
            string eventType, 
            int attemptNumber)
        {
            // Legacy ownerless deliveries remain durable but have no authorized notification recipient.
            if (virtualKeyId <= 0) return;

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
                
                var groupName = SignalRConstants.Groups.Webhook(virtualKeyId, webhookUrl);
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
            int virtualKeyId,
            string webhookUrl, 
            string taskId, 
            int statusCode, 
            long responseTimeMs, 
            int totalAttempts)
        {
            // Legacy ownerless deliveries remain durable but have no authorized notification recipient.
            if (virtualKeyId <= 0) return;

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
                var groupName = SignalRConstants.Groups.Webhook(virtualKeyId, webhookUrl);
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
            int virtualKeyId,
            string webhookUrl, 
            string taskId, 
            string errorMessage, 
            int? statusCode, 
            int attemptNumber, 
            bool isPermanent)
        {
            // Legacy ownerless deliveries remain durable but have no authorized notification recipient.
            if (virtualKeyId <= 0) return;

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
                var groupName = SignalRConstants.Groups.Webhook(virtualKeyId, webhookUrl);
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
            int virtualKeyId,
            string webhookUrl, 
            string taskId, 
            DateTime retryTime, 
            int retryNumber, 
            int maxRetries)
        {
            // Legacy ownerless deliveries remain durable but have no authorized notification recipient.
            if (virtualKeyId <= 0) return;

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
                var groupName = SignalRConstants.Groups.Webhook(virtualKeyId, webhookUrl);
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
            int virtualKeyId,
            string webhookUrl, 
            string newState, 
            string previousState, 
            string reason, 
            int failureCount)
        {
            // Legacy ownerless deliveries remain durable but have no authorized notification recipient.
            if (virtualKeyId <= 0) return;

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
                
                // Destination details are visible only to the delivery owner.
                var groupName = SignalRConstants.Groups.Webhook(virtualKeyId, webhookUrl);
                await _hubContext.Clients.Group(groupName).SendAsync("CircuitBreakerStateChanged", stateChange);
                
                _logger.LogWarning(
                    "Circuit breaker state changed for {WebhookUrl}: {PreviousState} -> {NewState}, reason: {Reason}",
                    DestinationKey(webhookUrl), previousState, newState, reason);
            }
            catch (Exception)
            {
                _logger.LogWarning( "Error sending circuit breaker state change notification");
            }
        }

        public Task<WebhookStatistics> GetStatisticsAsync(string period = "last_hour") =>
            GetStatisticsAsync(CancellationToken.None);

        private async Task<WebhookStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
        {
            ThrowIfStatisticsStopped(cancellationToken);
            using var scope = _serviceProvider.CreateScope();
            var backlog = await scope.ServiceProvider.GetRequiredService<IWebhookRecovery>().BacklogAsync(cancellationToken);
            ThrowIfStatisticsStopped(cancellationToken);
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

        private async Task BroadcastStatisticsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var stats = await GetStatisticsAsync(cancellationToken);
                ThrowIfStatisticsStopped(cancellationToken);
                // Retained backlog totals span all owners and are administrator-only.
                await _hubContext.Clients.Group("admin").SendAsync("DeliveryStatisticsUpdated", stats, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) { }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Error broadcasting webhook statistics");
            }
        }

        private void ThrowIfStatisticsStopped(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0)
                throw new OperationCanceledException("Webhook statistics service was disposed", cancellationToken);
        }

        private static string DestinationKey(string url) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16];

        private static string GenerateWebhookId(string webhookUrl, string taskId)
        {
            return $"{taskId}_{DestinationKey(webhookUrl)}";
        }

    }
}
