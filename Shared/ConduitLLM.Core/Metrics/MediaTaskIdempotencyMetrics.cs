using System.Diagnostics.Metrics;

namespace ConduitLLM.Core.Metrics;

/// <summary>Metrics for media outcomes that require replay-safety decisions.</summary>
public static class MediaTaskIdempotencyMetrics
{
    private static readonly Meter Meter = new("ConduitLLM.Media.Idempotency");
    private static readonly Counter<long> IndeterminateTasks =
        Meter.CreateCounter<long>("media_task_indeterminate");
    private static readonly Counter<long> OperatorRetries =
        Meter.CreateCounter<long>("media_task_operator_retries");
    private static readonly Counter<long> Reconciliations = Meter.CreateCounter<long>("media_task_reconciliations");
    private static readonly Counter<double> ProviderWriteOffs = Meter.CreateCounter<double>("media_task_provider_cost_written_off");

    public static void RecordReconciliation(string taskType, string outcome, decimal? cost = null)
    {
        Reconciliations.Add(1, new KeyValuePair<string, object?>("task_type", taskType), new("outcome", outcome));
        if (cost is > 0) ProviderWriteOffs.Add((double)cost.Value, new KeyValuePair<string, object?>("task_type", taskType));
    }

    public static void RecordIndeterminate(string source, long count = 1) =>
        IndeterminateTasks.Add(count, new KeyValuePair<string, object?>("source", source));

    public static void RecordOperatorRetry(string taskType) =>
        OperatorRetries.Add(1, new KeyValuePair<string, object?>("task_type", taskType));
}
