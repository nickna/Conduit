using ConduitLLM.Core.Services;

namespace ConduitLLM.Tests.Core.Services;

public partial class StreamingMetricsCollectorTests
{
    [Fact]
    public void RecordingLongStream_DoesNotAllocatePerChunkStorage()
    {
        var collector = new StreamingMetricsCollector("bounded-recording", "model", "provider");
        collector.RecordFirstToken();
        collector.RecordToken();
        _ = collector.GetMetrics();
        var thread = Environment.CurrentManagedThreadId;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100_000; index++)
            collector.RecordToken();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        Assert.True(allocated <= 1024, $"Recording 100,000 chunks allocated growing managed storage: {allocated} bytes.");
        Assert.Equal(100_002, collector.GetMetrics().TokensGenerated);
    }

    [Fact]
    public void LongStreamSnapshot_AllocatesOnlyItsResult()
    {
        var collector = new StreamingMetricsCollector("bounded-snapshot", "model", "provider");
        collector.RecordFirstToken();
        collector.RecordToken();
        _ = collector.GetMetrics();
        for (var index = 0; index < 100_000; index++)
            collector.RecordToken();
        var thread = Environment.CurrentManagedThreadId;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var metrics = collector.GetMetrics();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        Assert.True(allocated <= 1024, $"Snapshot allocated storage proportional to stream length: {allocated} bytes.");
        Assert.Equal(100_002, metrics.TokensGenerated);
        Assert.NotNull(metrics.AvgInterTokenLatencyMs);
    }

    [Fact]
    public void FirstTokenState_DistinguishesOrdinaryRecordsAndFrozenFirstMarker()
    {
        var collector = new StreamingMetricsCollector("first-state", "model", "provider");
        collector.GetFinalMetrics();
        Assert.False(collector.HasRecordedFirstToken);
        collector.RecordToken();
        Assert.False(collector.HasRecordedFirstToken);
        collector.RecordFirstToken();
        Assert.True(collector.HasRecordedFirstToken);
        var first = collector.GetMetrics();
        collector.RecordFirstToken();
        Assert.Equal(first.TimeToFirstTokenMs, collector.GetMetrics().TimeToFirstTokenMs);
        Assert.Equal(1, collector.GetMetrics().TokensGenerated);
    }

    [Fact]
    public void FirstTokenAfterOrdinaryTokens_PreservesRecordedIntervalsAndResetsReportedCount()
    {
        var collector = new StreamingMetricsCollector("out-of-order", "model", "provider");
        collector.GetFinalMetrics(); // Freeze the existing clock; all following timestamps are identical.
        collector.RecordToken();
        collector.RecordToken();
        Assert.Null(collector.GetMetrics().TimeToFirstTokenMs);
        Assert.Equal(2, collector.GetMetrics().TokensGenerated);

        collector.RecordFirstToken();
        var first = collector.GetMetrics();
        Assert.Equal(1, first.TokensGenerated);
        Assert.NotNull(first.TimeToFirstTokenMs);
        Assert.Equal(0d, first.AvgInterTokenLatencyMs);

        collector.RecordFirstToken();
        collector.RecordToken();
        var next = collector.GetMetrics();
        Assert.Equal(2, next.TokensGenerated);
        Assert.Equal(first.TimeToFirstTokenMs, next.TimeToFirstTokenMs);
        Assert.Equal(0d, next.AvgInterTokenLatencyMs);
    }

    [Fact]
    public void SnapshotAndFinalAverage_PreserveAllIntervalsWithoutStoppingSnapshotClock()
    {
        var collector = new StreamingMetricsCollector("latency", "model", "provider");
        collector.RecordFirstToken();
        var first = collector.GetMetrics().TimeToFirstTokenMs!.Value;
        Assert.True(SpinWait.SpinUntil(() => collector.GetMetrics().ElapsedMs > first, TimeSpan.FromSeconds(5)));
        collector.RecordToken();
        collector.GetFinalMetrics();
        var stopped = collector.GetMetrics().ElapsedMs;
        collector.RecordToken();
        collector.RecordToken();

        var snapshot = collector.GetMetrics();
        var final = collector.GetFinalMetrics();
        var expectedAverage = (stopped - first) / 3d;
        Assert.Equal(4, snapshot.TokensGenerated);
        Assert.Equal(expectedAverage, snapshot.AvgInterTokenLatencyMs);
        Assert.Equal(expectedAverage, final.AvgInterTokenLatencyMs);
        Assert.Equal(stopped, snapshot.ElapsedMs);

        snapshot.TokensGenerated = -1;
        snapshot.AvgInterTokenLatencyMs = -1;
        Assert.Equal(4, collector.GetMetrics().TokensGenerated);
        Assert.Equal(expectedAverage, collector.GetMetrics().AvgInterTokenLatencyMs);
    }
}
