using ConduitLLM.Core.Extensions;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Services.SpendNotification
{
    /// <summary>
    /// Distributed implementation of spend notification service using Redis for multi-instance consistency
    /// </summary>
    public class DistributedSpendNotificationService : ISpendNotificationService, IHostedService, IDisposable
    {
        private readonly IHubContext<SpendNotificationHub> _hubContext;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<DistributedSpendNotificationService> _logger;
        private readonly RedisConnectionFactory _redisConnectionFactory;

        private ISpendDataRepository? _repository;
        private IBudgetAlertManager? _budgetAlertManager;
        private ISpendPatternAnalyzer? _patternAnalyzer;

        private Timer? _patternAnalysisTimer;
        private readonly TimeSpan _analysisInterval = TimeSpan.FromMinutes(5);

        public string InstanceId { get; }

        public DistributedSpendNotificationService(
            IHubContext<SpendNotificationHub> hubContext,
            IServiceScopeFactory serviceScopeFactory,
            ILogger<DistributedSpendNotificationService> logger,
            RedisConnectionFactory redisConnectionFactory)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _redisConnectionFactory = redisConnectionFactory ?? throw new ArgumentNullException(nameof(redisConnectionFactory));

            InstanceId = GenerateInstanceId();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await InitializeServicesAsync();

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

            if (_repository != null)
            {
                await _repository.UnregisterInstanceAsync(InstanceId);
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
                // Record spend if repository is available
                if (_repository != null)
                {
                    await _repository.RecordSpendingPatternAsync(virtualKeyId, amount, totalSpend);
                }

                // Check budget thresholds if applicable
                if (budget.HasValue && budget.Value > 0 && _budgetAlertManager != null)
                {
                    var budgetPercentage = (totalSpend / budget.Value) * 100;
                    await _budgetAlertManager.CheckBudgetThresholdsAsync(
                        virtualKeyId, totalSpend, budget.Value, budgetPercentage);
                }

                // Send spend update notification
                await SendSpendUpdateNotificationAsync(
                    virtualKeyId, amount, totalSpend, budget, model, provider);

                // Check for unusual spending patterns
                if (_patternAnalyzer != null)
                {
                    await _patternAnalyzer.CheckUnusualSpendingAsync(virtualKeyId);
                }
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
            if (_repository != null)
            {
                _ = _repository.RecordSpendingPatternAsync(virtualKeyId, amount, amount);
            }
        }

        /// <summary>
        /// Public method to check for unusual spending patterns
        /// </summary>
        public async Task CheckUnusualSpendingAsync(int virtualKeyId)
        {
            if (_patternAnalyzer != null)
            {
                await _patternAnalyzer.CheckUnusualSpendingAsync(virtualKeyId);
            }
        }

        private async Task InitializeServicesAsync()
        {
            try
            {
                var connection = await _redisConnectionFactory.GetConnectionAsync();
                var database = connection.GetDatabase();

                // Create repositories and services
                using var scope = _serviceScopeFactory.CreateScope();
                var serviceProvider = scope.ServiceProvider;

                // Initialize repository
                _repository = new SpendDataRepository(
                    database,
                    serviceProvider.GetRequiredService<ILogger<SpendDataRepository>>());

                // Initialize budget alert manager
                var lockService = serviceProvider.GetRequiredService<IDistributedLockProvider>();
                _budgetAlertManager = new BudgetAlertManager(
                    _hubContext,
                    _repository,
                    lockService,
                    serviceProvider.GetRequiredService<ILogger<BudgetAlertManager>>(),
                    serviceProvider.GetService<IHostApplicationLifetime>());

                // Initialize pattern analyzer
                _patternAnalyzer = new SpendPatternAnalyzer(
                    _hubContext,
                    _repository,
                    serviceProvider.GetRequiredService<ILogger<SpendPatternAnalyzer>>());

                // Register instance
                await RegisterInstanceAsync();

                _logger.LogInformation("Distributed spend notification services initialized with Redis backend");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize Redis-based services, running in degraded mode");
                // Service will continue without Redis features
            }
        }

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
            if (_patternAnalyzer != null)
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
        }

        private async Task RegisterInstanceAsync()
        {
            if (_repository != null)
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
