using ConduitLLM.Core.Interfaces;
using Prometheus;

namespace ConduitLLM.Gateway.Services;

public static class WebhookDeliveryTelemetry
{
    private static readonly Counter Scheduled = Prometheus.Metrics.CreateCounter("conduit_webhook_scheduled_total", "Committed future delivery by reason",
        new CounterConfiguration { LabelNames = ["reason"] });
    private static readonly Gauge Records = Prometheus.Metrics.CreateGauge("conduit_webhook_records", "Retained authoritative delivery records",
        new GaugeConfiguration { LabelNames = ["state"] });
    private static readonly Gauge Oldest = Prometheus.Metrics.CreateGauge("conduit_webhook_oldest_pending_seconds", "Age of oldest pending delivery");
    private static readonly Gauge Replays = Prometheus.Metrics.CreateGauge("conduit_webhook_replay_cycles", "Retained audited replay cycles");
    private static readonly Gauge Reserved = Prometheus.Metrics.CreateGauge("conduit_webhook_reserved_attempts", "Reserved attempts in retained current cycles; may include crash-before-send slots");
    public static void Schedule(bool deferral) => Observe(() => Scheduled.WithLabels(deferral ? "admission" : "receiver_retry").Inc());
    public static void Snapshot(WebhookBacklog value) => Observe(() =>
    {
        Records.WithLabels("Pending").Set(value.Pending); Records.WithLabels("Delivered").Set(value.Delivered);
        Records.WithLabels("Exhausted").Set(value.Exhausted); Oldest.Set(value.OldestPendingSeconds);
        Replays.Set(value.ReplayCycles); Reserved.Set(value.ReservedAttempts);
    });
    private static void Observe(Action action) { try { action(); } catch (Exception) { } }
}
