using ConduitLLM.Core.Extensions;
using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;

namespace ConduitLLM.Gateway.Services.SpendNotification
{
    /// <summary>
    /// Manages budget alerts and threshold notifications
    /// </summary>
    public interface IBudgetAlertManager
    {
        /// <summary>
        /// Checks budget thresholds and sends alerts if needed
        /// </summary>
        Task CheckBudgetThresholdsAsync(int virtualKeyId, decimal totalSpend, decimal budget, decimal percentageUsed, CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Sends a budget alert notification
        /// </summary>
        Task SendBudgetAlertAsync(int virtualKeyId, int threshold, decimal totalSpend, decimal budget, decimal percentageUsed, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Implementation of budget alert manager
    /// </summary>
    public class BudgetAlertManager : IBudgetAlertManager
    {
        private readonly IHubContext<SpendNotificationHub> _hubContext;
        private readonly ISpendDataRepository _repository;
        private readonly IDistributedLockProvider _lockService;
        private readonly ILogger<BudgetAlertManager> _logger;
        private readonly CancellationToken _shutdownToken;
        
        private readonly int[] _budgetThresholds = { 50, 75, 80, 90, 95, 100 };
        private readonly TimeSpan _alertCooldownPeriod = TimeSpan.FromHours(4);
        private readonly TimeSpan _alertTtl = TimeSpan.FromHours(24);
        
        public BudgetAlertManager(
            IHubContext<SpendNotificationHub> hubContext,
            ISpendDataRepository repository,
            IDistributedLockProvider lockService,
            ILogger<BudgetAlertManager> logger, IHostApplicationLifetime? applicationLifetime = null)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _lockService = lockService ?? throw new ArgumentNullException(nameof(lockService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _shutdownToken = applicationLifetime?.ApplicationStopping ?? CancellationToken.None;
        }

        public async Task CheckBudgetThresholdsAsync(
            int virtualKeyId, decimal totalSpend, decimal budget, decimal percentageUsed, CancellationToken cancellationToken = default)
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
            cancellationToken = request.Token;
            try
            {
                foreach (var threshold in _budgetThresholds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (percentageUsed >= threshold)
                    {
                        await ProcessThresholdAlertAsync(virtualKeyId, threshold, totalSpend, budget, percentageUsed, cancellationToken);
                    }
                }
                
                // Reset alerts if spending drops below 50%
                if (percentageUsed < 50)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _repository.ResetBudgetAlertsAsync(virtualKeyId);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking budget thresholds for VirtualKey {VirtualKeyId}", virtualKeyId);
            }
        }

        private async Task ProcessThresholdAlertAsync(
            int virtualKeyId, int threshold, decimal totalSpend, decimal budget, decimal percentageUsed, CancellationToken cancellationToken)
        {
            var lockKey = RedisKeys.Lock.AlertThreshold(virtualKeyId.ToString(), threshold.ToString());
            
            // Try to acquire distributed lock
            await using var lockHandle = await _lockService.TryAcquireAsync(
                lockKey, TimeSpan.Zero, cancellationToken);
            
            if (lockHandle == null)
            {
                _logger.LogDebug("Could not acquire lock for alert VK:{VirtualKeyId} Threshold:{Threshold}", 
                    virtualKeyId, threshold);
                return;
            }

            using var work = lockHandle.CreateOperationCancellation(cancellationToken);
            cancellationToken = work.Token;
            cancellationToken.ThrowIfCancellationRequested();
            
            // Check if alert is in cooldown
            var cooldownType = $"budget:{threshold}";
            var inCooldown = await _repository.IsAlertInCooldownAsync(virtualKeyId, cooldownType);
            cancellationToken.ThrowIfCancellationRequested();
            if (inCooldown)
            {
                _logger.LogDebug("Alert for VirtualKey {VirtualKeyId} at {Threshold}% is in cooldown", 
                    virtualKeyId, threshold);
                return;
            }
            
            // Try to mark alert as sent (idempotent operation)
            cancellationToken.ThrowIfCancellationRequested();
            var marked = await _repository.MarkAlertSentAsync(virtualKeyId, threshold, _alertTtl);
            cancellationToken.ThrowIfCancellationRequested();
            if (!marked)
            {
                _logger.LogDebug("Alert already sent for VirtualKey {VirtualKeyId} at {Threshold}%", 
                    virtualKeyId, threshold);
                return;
            }
            
            // Set cooldown to prevent alert spam
            cancellationToken.ThrowIfCancellationRequested();
            await _repository.SetAlertCooldownAsync(virtualKeyId, cooldownType, _alertCooldownPeriod);
            
            // Send the budget alert
            cancellationToken.ThrowIfCancellationRequested();
            await SendBudgetAlertAsync(virtualKeyId, threshold, totalSpend, budget, percentageUsed, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task SendBudgetAlertAsync(
            int virtualKeyId, int threshold, decimal totalSpend, decimal budget, decimal percentageUsed, CancellationToken cancellationToken = default)
        {
            try
            {
                var (alertType, severity, message) = GetAlertDetails(threshold, totalSpend, budget, percentageUsed);
                var recommendations = GetBudgetRecommendations(threshold);

                var notification = new BudgetAlertNotification
                {
                    AlertType = alertType,
                    Message = message,
                    CurrentSpend = totalSpend,
                    BudgetLimit = budget,
                    PercentageUsed = (double)percentageUsed,
                    Severity = severity,
                    Recommendations = recommendations
                };

                var groupName = $"vkey-{virtualKeyId}";
                cancellationToken.ThrowIfCancellationRequested();
                await _hubContext.Clients.Group(groupName).SendAsync("BudgetAlert", notification, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogWarning(
                    "[BudgetAlert] Sent notification - VirtualKey: {VirtualKeyId}, Threshold: {Threshold}%, " +
                    "CurrentSpend: ${CurrentSpend:F2}, Budget: ${Budget:F2}, AlertType: {AlertType}, " +
                    "Severity: {Severity}",
                    virtualKeyId, threshold, totalSpend, budget, alertType, severity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending budget alert for VirtualKey {VirtualKeyId}", virtualKeyId);
            }
        }

        private static (string alertType, string severity, string message) GetAlertDetails(
            int threshold, decimal totalSpend, decimal budget, decimal percentageUsed)
        {
            string alertType = threshold switch
            {
                50 => "Budget Warning",
                75 => "Budget Alert",
                80 => "High Spend Alert",
                90 => "Critical Spend Alert",
                95 => "Budget Nearly Exhausted",
                100 => "Budget Exceeded",
                _ => "Budget Update"
            };

            string severity = threshold switch
            {
                <= 50 => "info",
                <= 75 => "warning",
                <= 90 => "high",
                <= 95 => "critical",
                _ => "exceeded"
            };

            string message = threshold switch
            {
                100 => $"Budget limit of ${budget:F2} has been exceeded. Current spend: ${totalSpend:F2}",
                95 => $"Only 5% of budget remaining. Current spend: ${totalSpend:F2} of ${budget:F2}",
                90 => $"90% of budget consumed. Immediate action recommended.",
                _ => $"You have used {percentageUsed:F1}% of your ${budget:F2} budget"
            };

            return (alertType, severity, message);
        }

        private static List<string> GetBudgetRecommendations(int threshold)
        {
            return threshold switch
            {
                >= 100 => new List<string>
                {
                    "Increase budget limit or pause API usage",
                    "Review and optimize API calls",
                    "Consider implementing caching strategies"
                },
                >= 90 => new List<string>
                {
                    "Monitor usage closely",
                    "Consider rate limiting",
                    "Review upcoming workloads"
                },
                >= 75 => new List<string>
                {
                    "Review current usage patterns",
                    "Plan for budget adjustment if needed"
                },
                _ => new List<string>
                {
                    "Continue monitoring spend",
                    "Set up alerts for higher thresholds"
                }
            };
        }
    }
}
