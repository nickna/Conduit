using System.Text;

using Prometheus;

namespace ConduitLLM.Tests.Gateway.Metrics;

public sealed class NativeMetricSamplesTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1", 1L)]
    [InlineData("1.0", 1L)]
    [InlineData("1e0", 1L)]
    [InlineData("9007199254740991", 9_007_199_254_740_991L)]
    public void IntegerSample_UsesExactCounterDomain(string wireValue, long expected)
    {
        Assert.True(NativeMetricSamples.TryReadNonnegativeInteger($"metric {wireValue}\n", "metric", out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("1.5")]
    [InlineData("NaN")]
    [InlineData("+Inf")]
    [InlineData("Infinity")]
    [InlineData("1,0")]
    [InlineData("1 12345")]
    [InlineData("9007199254740992")]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    public void InvalidIntegerSample_CannotSatisfyGate(string wireValue) =>
        Assert.Throws<InvalidOperationException>(() => NativeMetricSamples.TryReadNonnegativeInteger(
            $"metric {wireValue}\n", "metric", out _));

    [Fact]
    public void IntegerSamples_RetainMissingAndDuplicateChecks()
    {
        Assert.False(NativeMetricSamples.TryReadNonnegativeInteger("metric_extra 1\n", "metric", out var missing));
        Assert.Equal(0L, missing);
        Assert.Throws<InvalidOperationException>(() => NativeMetricSamples.TryReadNonnegativeInteger(
            "metric 1\nmetric 1\n", "metric", out _));
    }

    [Theory]
    [InlineData("conduit_models_active_count{provider=\"-9001\"}")]
    [InlineData("conduit_model_response_time_seconds_count{model=\"owned_model\",provider=\"owned\"}")]
    [InlineData("conduit_tasks_queue_depth{task_type=\"native_signalr_probe\",status=\"pending\"}")]
    public void AggregateCounters_RejectFractionalSamples(string key)
    {
        var wire = "conduit_cost_rate_dollars_per_minute{provider=\"owned\"} 0.4\n" +
            "conduit_models_active_count{provider=\"-9001\"} 1\n" +
            "conduit_model_response_time_seconds_sum{model=\"owned_model\",provider=\"owned\"} 0.2\n" +
            "conduit_model_response_time_seconds_count{model=\"owned_model\",provider=\"owned\"} 1\n" +
            "conduit_tasks_queue_depth{task_type=\"native_signalr_probe\",status=\"pending\"} 2\n";
        wire = wire.Replace(key + (key.StartsWith("conduit_tasks", StringComparison.Ordinal) ? " 2" : " 1"), key + " 1.5");
        Assert.Throws<InvalidOperationException>(() =>
        {
            NativeMetricSamples.HasBusinessAggregates(wire, "owned_model", "owned");
            NativeMetricSamples.HasPendingNotificationTasks(wire);
        });
    }

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
