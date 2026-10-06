using ConduitLLM.Core.Extensions;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Scheduled OpenRouter metadata drift detection. Leader-locked so it runs once cluster-wide, and
    /// gated by <see cref="OpenRouterSyncOptions.Enabled"/> (drift can also be triggered on demand via
    /// the Admin API regardless of this flag).
    /// </summary>
    public class OpenRouterMetadataSyncService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IDistributedLockProvider _lockService;
        private readonly OpenRouterSyncOptions _options;
        private readonly ILogger<OpenRouterMetadataSyncService> _logger;

        private const string LeaderLockKey = "openrouter:metadata-sync:leader";

        public OpenRouterMetadataSyncService(
            IServiceScopeFactory scopeFactory,
            IDistributedLockProvider lockService,
            IOptions<OpenRouterSyncOptions> options,
            ILogger<OpenRouterMetadataSyncService> logger)
        {
            _scopeFactory = scopeFactory;
            _lockService = lockService;
            _options = options.Value;
            _logger = logger;
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("OpenRouter metadata sync scheduler is disabled (OpenRouterSync:Enabled=false).");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.InitialDelaySeconds), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var run = await RunScheduledSyncAsync(stoppingToken);
                    if (run != null)
                    {
                        _logger.LogInformation(
                            "OpenRouter metadata sync {Status}: {Created} created, {Updated} updated, {AutoResolved} auto-resolved across {Mappings} mappings.",
                            run.Status, run.ItemsCreated, run.ItemsUpdated, run.ItemsAutoResolved, run.MappingsChecked);
                    }

                    await Task.Delay(TimeSpan.FromHours(_options.ScheduleIntervalHours), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OpenRouter metadata sync loop encountered an error; retrying after backoff.");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }
        }

        public async Task<ConduitLLM.Admin.Models.ProviderSync.ProviderSyncRunDto?> RunScheduledSyncAsync(CancellationToken stoppingToken)
        {
            await using var ownership = await _lockService.TryAcquireAsync(LeaderLockKey, TimeSpan.Zero, stoppingToken);
            if (ownership is null) { return null; }
            using var work = ownership.CreateOperationCancellation(stoppingToken, TimeSpan.FromMinutes(15));
            work.Token.ThrowIfCancellationRequested();
            using var scope = _scopeFactory.CreateScope();
            var detection = scope.ServiceProvider.GetRequiredService<IOpenRouterDriftDetectionService>();
            var run = await detection.RunSyncAsync("Schedule", work.Token);
            work.Token.ThrowIfCancellationRequested();
            return run;
        }
    }
}
