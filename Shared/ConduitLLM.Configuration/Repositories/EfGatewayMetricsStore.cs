#if !CONDUIT_NATIVE_AOT
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// EF reference implementation of the fixed-shape Gateway metrics contract.
/// Native Gateway replaces this adapter with typed Npgsql queries.
/// </summary>
public sealed class EfGatewayMetricsStore : IGatewayMetricsStore
{
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfGatewayMetricsStore(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlyList<GatewayModelUsageMetric>> GetModelUsageAsync(
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RequestLogs
            .AsNoTracking()
            .Where(request => request.Timestamp >= since)
            .GroupBy(request => new
            {
                Model = request.ModelName,
                Provider = request.ProviderType ?? "unknown"
            })
            .Select(group => new GatewayModelUsageMetric(
                group.Key.Model,
                group.Key.Provider,
                group.Average(request => request.ResponseTimeMs)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GatewayProviderCostMetric>> GetProviderCostsAsync(
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RequestLogs
            .AsNoTracking()
            .Where(request => (request.BilledAtUtc ?? request.Timestamp) >= since && request.Cost > 0)
            .GroupBy(request => request.ProviderType ?? "unknown")
            .Select(group => new GatewayProviderCostMetric(
                group.Key,
                group.Sum(request => request.Cost)))
            .ToListAsync(cancellationToken);
    }

    public async Task<GatewayActiveEntityMetrics> GetActiveEntitiesAsync(
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var activeVirtualKeyCount = await context.VirtualKeys
            .AsNoTracking()
            .CountAsync(
                key => key.IsEnabled && (key.ExpiresAt == null || key.ExpiresAt > now),
                cancellationToken);
        var mappingsByProvider = await context.ModelProviderMappings
            .AsNoTracking()
            .Where(mapping => mapping.IsEnabled && mapping.Provider.IsEnabled)
            .GroupBy(mapping => mapping.ProviderId)
            .Select(group => new GatewayProviderMappingMetric(group.Key, group.Count()))
            .ToListAsync(cancellationToken);
        return new GatewayActiveEntityMetrics(activeVirtualKeyCount, mappingsByProvider);
    }

    public async Task<GatewayTaskQueueMetrics> GetTaskQueueMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var queueDepths = await context.AsyncTasks
            .AsNoTracking()
            .Where(task => !task.IsArchived && (task.State == 0 || task.State == 1))
            .GroupBy(task => new { task.Type, task.State })
            .Select(group => new GatewayTaskQueueMetric(
                group.Key.Type,
                group.Key.State,
                group.Count()))
            .ToListAsync(cancellationToken);
        var oldestPendingTasks = await context.AsyncTasks
            .AsNoTracking()
            .Where(task => !task.IsArchived && task.State == 0)
            .GroupBy(task => task.Type)
            .Select(group => new GatewayPendingTaskMetric(
                group.Key,
                group.Min(task => task.CreatedAt)))
            .ToListAsync(cancellationToken);
        return new GatewayTaskQueueMetrics(queueDepths, oldestPendingTasks);
    }

    public async Task<IReadOnlyList<GatewayGenerationTaskMetric>> GetGenerationTaskMetricsAsync(
        string taskType,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AsyncTasks
            .AsNoTracking()
            .Where(task => task.Type == taskType && task.CreatedAt >= since)
            .GroupBy(task => task.State)
            .Select(group => new GatewayGenerationTaskMetric(
                group.Key,
                group.Count(),
                group.Where(task => task.CompletedAt.HasValue).Any()
                    ? group.Where(task => task.CompletedAt.HasValue)
                        .Average(task => (double)(task.CompletedAt!.Value - task.CreatedAt).TotalSeconds)
                    : null))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GatewayVirtualKeySpendMetric>> GetTopVirtualKeySpendAsync(
        DateTime from,
        DateTime before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spendByKey = await context.VirtualKeySpendHistory
            .AsNoTracking()
            .Where(spend => spend.Timestamp >= from && spend.Timestamp < before)
            .GroupBy(spend => spend.VirtualKeyId)
            .Select(group => new
            {
                VirtualKeyId = group.Key,
                TotalSpend = group.Sum(spend => spend.Amount)
            })
            .OrderByDescending(spend => spend.TotalSpend)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return spendByKey
            .Select(spend => new GatewayVirtualKeySpendMetric(
                spend.VirtualKeyId,
                spend.TotalSpend))
            .ToList();
    }
}
#endif
