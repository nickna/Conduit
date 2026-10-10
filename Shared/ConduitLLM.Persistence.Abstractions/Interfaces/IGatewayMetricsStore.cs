using ConduitLLM.Persistence;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>
/// Fixed-shape persistence boundary for Gateway operational metrics.
/// </summary>
public interface IGatewayMetricsStore
{
    Task<IReadOnlyList<GatewayModelUsageMetric>> GetModelUsageAsync(
        DateTime since,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GatewayProviderCostMetric>> GetProviderCostsAsync(
        DateTime since,
        CancellationToken cancellationToken = default);

    Task<GatewayActiveEntityMetrics> GetActiveEntitiesAsync(
        DateTime now,
        CancellationToken cancellationToken = default);

    Task<GatewayTaskQueueMetrics> GetTaskQueueMetricsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GatewayGenerationTaskMetric>> GetGenerationTaskMetricsAsync(
        string taskType,
        DateTime since,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GatewayVirtualKeySpendMetric>> GetTopVirtualKeySpendAsync(
        DateTime from,
        DateTime before,
        int limit,
        CancellationToken cancellationToken = default);
}
