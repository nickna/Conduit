using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Serialization;
using StackExchange.Redis;

namespace ConduitLLM.Gateway.Services.SpendNotification
{
    /// <summary>
    /// Distributed implementation of spend notification service using Redis for multi-instance consistency
    /// </summary>
    public class DistributedSpendNotificationService : ISpendNotificationService, IHostedService, IDisposable
    {
        private readonly IHubContext<SpendNotificationHub> _hubContext;
        private readonly ILogger<DistributedSpendNotificationService> _logger;

        private readonly ISpendDataRepository _repository;
        private readonly IBudgetAlertManager _budgetAlertManager;
        private readonly ISpendPatternAnalyzer _patternAnalyzer;

        private Timer? _patternAnalysisTimer;
        private readonly TimeSpan _analysisInterval = TimeSpan.FromMinutes(5);

        public string InstanceId { get; }

        public DistributedSpendNotificationService(
            IHubContext<SpendNotificationHub> hubContext,
            ILogger<DistributedSpendNotificationService> logger,
            ISpendDataRepository repository,
            IBudgetAlertManager budgetAlertManager,
            ISpendPatternAnalyzer patternAnalyzer)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _budgetAlertManager = budgetAlertManager ?? throw new ArgumentNullException(nameof(budgetAlertManager));
            _patternAnalyzer = patternAnalyzer ?? throw new ArgumentNullException(nameof(patternAnalyzer));

            InstanceId = GenerateInstanceId();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await RegisterInstanceAsync();

            _patternAnalysisTimer = new Timer(
                async _ => await AnalyzeSpendingPatternsAsync(),
                null,
                _analysisInterval,
                _analysisInterval);

            _logger.LogInformation("DistributedSpendNotificationService started with instance ID: {InstanceId}",
                InstanceId);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("DistributedSpendNotificationService stopping...");

            _patternAnalysisTimer?.Change(Timeout.Infinite, 0);
            _patternAnalysisTimer?.Dispose();

            try
            {
                await _repository.UnregisterInstanceAsync(InstanceId);
            }
            catch (RedisException ex)
            {
                _logger.LogWarning(ex, "Failed to unregister spend notification instance; shutdown will continue");
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Timed out unregistering spend notification instance; shutdown will continue");
            }
        }

        public async Task NotifySpendUpdateAsync(
            int virtualKeyId,
            decimal amount,
            decimal totalSpend,
            decimal? budget,
            string? model,
            string? provider)
        {
            try
            {
                await _repository.RecordSpendingPatternAsync(virtualKeyId, amount, totalSpend);

                // Check budget thresholds if applicable
                if (budget.HasValue && budget.Value > 0)
                {
                    var budgetPercentage = (totalSpend / budget.Value) * 100;
                    await _budgetAlertManager.CheckBudgetThresholdsAsync(
                        virtualKeyId, totalSpend, budget.Value, budgetPercentage);
                }

                // Send spend update notification
                await SendSpendUpdateNotificationAsync(
                    virtualKeyId, amount, totalSpend, budget, model, provider);

                // Check for unusual spending patterns
                await _patternAnalyzer.CheckUnusualSpendingAsync(virtualKeyId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing spend update for VirtualKey {VirtualKeyId}", virtualKeyId);
            }
        }

        /// <summary>
        /// Legacy method for backward compatibility
        /// </summary>
        public async Task NotifySpendUpdatedAsync(int virtualKeyId, decimal spendAmount, string? model, string? provider)
        {
            await NotifySpendUpdateAsync(virtualKeyId, spendAmount, spendAmount, null, model, provider);
        }

        /// <summary>
        /// Sends a spend summary for a period
        /// </summary>
        public async Task SendSpendSummaryAsync(int virtualKeyId, SpendSummaryNotification summary)
        {
            try
            {
                var groupName = $"vkey-{virtualKeyId}";
                await _hubContext.Clients.Group(groupName).SendAsync("SpendSummary", summary);

                _logger.LogInformation(
                    "Sent spend summary for VirtualKey {VirtualKeyId}: Period {Period}, Total: ${TotalSpend:F2}",
                    virtualKeyId, summary.Period, summary.TotalSpend);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending spend summary for VirtualKey {VirtualKeyId}", virtualKeyId);
            }
        }

        /// <summary>
        /// Records spend data for pattern analysis
        /// </summary>
        public void RecordSpend(int virtualKeyId, decimal amount)
        {
            // Fire and forget to avoid blocking
            _ = _repository.RecordSpendingPatternAsync(virtualKeyId, amount, amount);
        }

        /// <summary>
        /// Public method to check for unusual spending patterns
        /// </summary>
        public Task CheckUnusualSpendingAsync(int virtualKeyId) =>
            _patternAnalyzer.CheckUnusualSpendingAsync(virtualKeyId);

        private async Task SendSpendUpdateNotificationAsync(
            int virtualKeyId, decimal amount, decimal totalSpend, decimal? budget, string? model, string? provider)
        {
            var notification = new SpendUpdateNotification
            {
                NewSpend = amount,
                TotalSpend = totalSpend,
                Budget = budget,
                BudgetPercentage = budget.HasValue && budget.Value > 0 ? (totalSpend / budget.Value) * 100 : null,
                Model = model,
                Provider = provider
                // Metadata is intentionally not set: this notification has no per-request
                // context, so fabricating a request id / endpoint here would be misleading
            };

            var groupName = $"vkey-{virtualKeyId}";
            await _hubContext.Clients.Group(groupName).SendAsync("SpendUpdate", notification);

            _logger.LogInformation(
                "Sent spend update for VirtualKey {VirtualKeyId}: ${Amount:F2} (Total: ${TotalSpend:F2})",
                virtualKeyId, amount, totalSpend);
        }

        private async Task AnalyzeSpendingPatternsAsync()
        {
            try
            {
                await _patternAnalyzer.AnalyzeAllPatternsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic pattern analysis");
            }
        }

        private async Task RegisterInstanceAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                var instanceData = new SpendNotificationInstanceData(
                    InstanceId,
                    Environment.MachineName,
                    Environment.ProcessId,
                    now,
                    now);

                await _repository.RegisterInstanceAsync(InstanceId, instanceData);
                _logger.LogInformation("Registered spend notification instance: {InstanceId}", InstanceId);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected during shutdown/startup race conditions.
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Timed out registering spend notification instance; notification processing will continue");
            }
            catch (RedisException ex)
            {
                _logger.LogWarning(ex, "Failed to register spend notification instance; notification processing will continue");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to register spend notification instance due to invalid operation; notification processing will continue");
            }
        }

        private static string GenerateInstanceId()
        {
            return $"{Environment.MachineName}_{Environment.ProcessId}_{Guid.NewGuid():N}".Substring(0,
                Math.Min(50, $"{Environment.MachineName}_{Environment.ProcessId}_{Guid.NewGuid():N}".Length));
        }

        public void Dispose()
        {
            _patternAnalysisTimer?.Dispose();
        }
    }
}
