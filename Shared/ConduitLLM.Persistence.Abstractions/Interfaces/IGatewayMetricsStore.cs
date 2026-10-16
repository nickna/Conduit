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

    /// <summary>
    /// Reads metrics for a nonblank task type, preserving its case and whitespace.
    /// Validates the task type before cancellation or I/O.
    /// </summary>
    Task<IReadOnlyList<GatewayGenerationTaskMetric>> GetGenerationTaskMetricsAsync(
        string taskType,
        DateTime since,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the highest spend totals within the requested time range.
    /// Requires a positive limit and validates it before cancellation or I/O.
    /// </summary>
    Task<IReadOnlyList<GatewayVirtualKeySpendMetric>> GetTopVirtualKeySpendAsync(
        DateTime from,
        DateTime before,
        int limit,
        CancellationToken cancellationToken = default);
}
