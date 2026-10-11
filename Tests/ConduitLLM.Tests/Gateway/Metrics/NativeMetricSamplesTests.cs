using System.Text;

using Prometheus;

namespace ConduitLLM.Tests.Gateway.Metrics;

public sealed class NativeMetricSamplesTests
{
    [Theory]
    [InlineData("0.4", 0.4)]
    [InlineData("4e-1", 0.4)]
    [InlineData("-2", -2)]
    public void ReadExactSample_UsesInvariantNumbers(string wireValue, double expected)
    {
        Assert.True(NativeMetricSamples.TryRead($"# HELP metric Help\nmetric{{provider=\"owned\"}} {wireValue}\n",
            "metric{provider=\"owned\"}", out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("+Inf")]
    [InlineData("Infinity")]
    [InlineData("0,4")]
    [InlineData("0.4 12345")]
    public void InvalidSample_CannotSatisfyGate(string wireValue) =>
        Assert.Throws<InvalidOperationException>(() => NativeMetricSamples.TryRead(
            $"metric {wireValue}\n", "metric", out _));

    [Fact]
    public void MissingAndDuplicateSamples_CannotSatisfyGate()
    {
        Assert.False(NativeMetricSamples.TryRead("metric_extra 0.4\nmetric{provider=\"other\"} 0.4\n",
            "metric", out _));
        Assert.Throws<InvalidOperationException>(() => NativeMetricSamples.TryRead(
            "metric 0.4\nmetric 0.4\n", "metric", out _));
    }

    [Fact]
    public async Task RealPrometheusExporter_ProvesExactAggregateKeysAndRepeatedCollection()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var cost = factory.CreateGauge("conduit_cost_rate_dollars_per_minute", "cost", "provider");
        cost.WithLabels("owned").Set(0.4);
        factory.CreateGauge("conduit_models_active_count", "active", "provider").WithLabels("-9001").Set(1);
        var latency = factory.CreateHistogram("conduit_model_response_time_seconds", "latency", "model", "provider");
        // Each collection observes the database mean, rather than every request.
        latency.WithLabels("owned_model", "owned").Observe(0.2);
        latency.WithLabels("owned_model", "owned").Observe(0.2);
        var queue = factory.CreateGauge("conduit_tasks_queue_depth", "queue", "task_type", "status");
        queue.WithLabels("native_signalr_probe", "pending").Set(2);

        async Task<string> ExportAsync()
        {
            using var stream = new MemoryStream();
            await registry.CollectAndExportAsTextAsync(stream);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        var wire = await ExportAsync();
        Assert.True(NativeMetricSamples.HasBusinessAggregates(wire, "owned_model", "owned"));
        Assert.True(NativeMetricSamples.HasPendingNotificationTasks(wire));
        Assert.False(NativeMetricSamples.HasBusinessAggregates(wire, "other_model", "owned"));
        cost.WithLabels("owned").Set(0);
        queue.WithLabels("native_signalr_probe", "pending").Set(1);
        wire = await ExportAsync();
        Assert.False(NativeMetricSamples.HasBusinessAggregates(wire, "owned_model", "owned"));
        Assert.False(NativeMetricSamples.HasPendingNotificationTasks(wire));
    }
}
