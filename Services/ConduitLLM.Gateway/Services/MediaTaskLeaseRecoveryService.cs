using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;

namespace ConduitLLM.Gateway.Services;

/// <summary>Durably redispatches safe work and classifies unknown provider outcomes.</summary>
public sealed class MediaTaskLeaseRecoveryService : BackgroundService
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("ConduitLLM.Media.Idempotency");
    private static readonly System.Diagnostics.Metrics.Counter<long> SafeRecoveries =
        Meter.CreateCounter<long>("media_task_safe_recoveries");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MediaTaskLeaseRecoveryService> _logger;

    public MediaTaskLeaseRecoveryService(
        IServiceScopeFactory scopeFactory,
        ILogger<MediaTaskLeaseRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var recovery = scope.ServiceProvider.GetRequiredService<IMediaTaskRecovery>();
                var result = await recovery.RecoverAsync(stoppingToken);
                if (result.ResetToPending > 0) SafeRecoveries.Add(result.ResetToPending);
                if (result.Redispatched > 0 || result.Blocked > 0)
                    _logger.LogInformation("Media recovery durably redispatched {Redispatched} tasks; {Blocked} requests require repair",
                        result.Redispatched, result.Blocked);
                if (result.MarkedIndeterminate > 0)
                {
                    MediaTaskIdempotencyMetrics.RecordIndeterminate(
                        "lease_recovery", result.MarkedIndeterminate);
                    _logger.LogCritical(
                        "Marked {Count} expired media tasks indeterminate; provider reconciliation is required",
                        result.MarkedIndeterminate);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover expired media task leases");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
