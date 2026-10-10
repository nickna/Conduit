namespace ConduitLLM.Persistence;

/// <summary>
/// Fixed-shape model usage aggregate consumed by Gateway metrics collection.
/// </summary>
public sealed record GatewayModelUsageMetric(
    string Model,
    string Provider,
    double AverageResponseTimeMs);

/// <summary>
/// Fixed-shape provider cost aggregate consumed by Gateway metrics collection.
/// </summary>
public sealed record GatewayProviderCostMetric(string Provider, decimal TotalCost);

/// <summary>
/// Fixed-shape active model-mapping aggregate consumed by Gateway metrics collection.
/// </summary>
public sealed record GatewayProviderMappingMetric(int ProviderId, int Count);

/// <summary>
/// Active Gateway entities at a point in time.
/// </summary>
public sealed record GatewayActiveEntityMetrics(
    int ActiveVirtualKeyCount,
    IReadOnlyList<GatewayProviderMappingMetric> MappingsByProvider);

/// <summary>
/// Fixed-shape async-task queue aggregate consumed by Gateway metrics collection.
/// </summary>
public sealed record GatewayTaskQueueMetric(string TaskType, int State, int Count);

/// <summary>
/// Oldest pending task timestamp for a task type.
/// </summary>
public sealed record GatewayPendingTaskMetric(string TaskType, DateTime OldestCreatedAt);

/// <summary>
/// Queue depth and wait-time inputs captured together for Gateway metrics collection.
/// </summary>
public sealed record GatewayTaskQueueMetrics(
    IReadOnlyList<GatewayTaskQueueMetric> QueueDepths,
    IReadOnlyList<GatewayPendingTaskMetric> OldestPendingTasks);

/// <summary>
/// Fixed-shape generation-task aggregate consumed by Gateway metrics collection.
/// </summary>
public sealed record GatewayGenerationTaskMetric(
    int State,
    int Count,
    double? AverageDurationSeconds);

/// <summary>
/// Fixed-shape virtual-key spend aggregate consumed by Gateway metrics collection.
/// </summary>
public sealed record GatewayVirtualKeySpendMetric(int VirtualKeyId, decimal TotalSpend);
