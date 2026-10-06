using Prometheus;

namespace ConduitLLM.Core.Metrics;

public static class DistributedLockMetrics
{
    public static readonly Counter Acquisitions = Prometheus.Metrics.CreateCounter(
        "conduit_distributed_lock_acquisitions_total", "Lock acquisition results",
        new CounterConfiguration { LabelNames = ["operation", "outcome"] });
    public static readonly Histogram Wait = Prometheus.Metrics.CreateHistogram(
        "conduit_distributed_lock_wait_seconds", "Lock acquisition duration",
        new HistogramConfiguration { LabelNames = ["operation"] });
    public static readonly Histogram Hold = Prometheus.Metrics.CreateHistogram(
        "conduit_distributed_lock_hold_seconds", "Lock ownership duration",
        new HistogramConfiguration { LabelNames = ["operation"] });
    public static readonly Counter Losses = Prometheus.Metrics.CreateCounter(
        "conduit_distributed_lock_losses_total", "Detected lock ownership loss",
        new CounterConfiguration { LabelNames = ["operation"] });
    public static readonly Counter ReleaseFailures = Prometheus.Metrics.CreateCounter(
        "conduit_distributed_lock_release_failures_total", "Lock release failures",
        new CounterConfiguration { LabelNames = ["operation"] });

    internal static string Operation(string key) => key switch
    {
        "media:cleanup:leader" => "media",
        "openrouter:metadata-sync:leader" => "metadata",
        "discovery:cache:warming" => "discovery",
        _ when key.Contains("alert", StringComparison.Ordinal) => "alert",
        _ when key.Contains("warming", StringComparison.Ordinal) => "warming",
        _ => "other",
    };
}
