using System.Diagnostics;
using System.Text.Json;

using ConduitLLM.Benchmarks.Messaging;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;

using Moq;

using Xunit;

namespace ConduitLLM.Tests.Messaging;

public class TransportThroughputHarnessTests
{
    [Fact]
    public void RuntimeCompilationSmoke_CompilesAndExecutesWithoutDatabaseConfiguration()
    {
        Assert.Equal(0, TransportThroughputHarness.VerifyRuntimeCompilation());
    }

    private static SpendUpdateRequested Publish(DeliveryCollector collector, int sequence = 0, decimal amount = .01m)
    {
        var message = new SpendUpdateRequested { KeyId = 1, RequestId = Guid.NewGuid().ToString("N"),
            CorrelationId = collector.PhaseId, Amount = amount };
        collector.RegisterPublished(message, sequence, Stopwatch.GetTimestamp());
        return message;
    }

    private static void Settle(DeliveryCollector collector, params Guid[] ids) =>
        collector.ObserveStore(Stopwatch.GetTimestamp(), ids, 0, 0, 0, 0, 0);

    [Fact]
    public async Task HandlerEntryAndReturn_DoNotCompleteBeforeIndependentDurableSettlement()
    {
        var collector = new DeliveryCollector(80);
        collector.BeginMeasurement(1, requireOrdering: true);
        var message = Publish(collector);
        var context = new Mock<IEventContext>();
        var id = Guid.NewGuid();
        context.SetupGet(c => c.MessageId).Returns(id);
        var handling = new BenchHandler<SpendUpdateRequested>(collector).HandleAsync(message, context.Object);
        Assert.Equal(1, collector.Snapshot().Started);
        Assert.Equal(0, collector.Snapshot().Completed);
        Assert.False(await collector.WaitForCompletionAsync(TimeSpan.FromMilliseconds(5)));
        await handling;
        Assert.Equal(1, collector.Snapshot().Completed);
        Assert.False(await collector.WaitForCompletionAsync(TimeSpan.FromMilliseconds(5)));
        Settle(collector, id);
        Assert.True(await collector.WaitForCompletionAsync(TimeSpan.FromSeconds(1)));
        var record = Assert.Single(collector.Snapshot().Deliveries);
        Assert.True(record.PublishedTicks <= record.StartedTicks);
        Assert.True(record.StartedTicks < record.FinishedTicks);
        Assert.True(record.FinishedTicks <= record.SettlementObservedTicks);
    }

    [Fact]
    public void WarmupCannotResetUntilFinishedAndSettled_ThenGetsIndependentIdentity()
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(1);
        var phase = collector.PhaseId;
        var ticket = collector.Start(Publish(collector), Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => collector.BeginMeasurement(2));
        collector.Finish(ticket);
        Assert.Throws<InvalidOperationException>(() => collector.BeginMeasurement(2));
        Settle(collector, ticket.MessageId);
        collector.BeginMeasurement(2);
        Assert.NotEqual(phase, collector.PhaseId);
        Assert.Empty(collector.Snapshot().Deliveries);
        Assert.False(collector.Snapshot().Complete);
    }

    [Fact]
    public void LogicalDuplicateWithNewEnvelopeIdIsRejected_AndCannotBeWaivedByEmptyStore()
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(1);
        var message = Publish(collector);
        var ticket = collector.Start(message, Guid.NewGuid());
        collector.Finish(ticket);
        Assert.Throws<InvalidOperationException>(() => collector.Start(message, Guid.NewGuid()));
        Settle(collector, ticket.MessageId);
        Assert.Equal(1, collector.Snapshot().DuplicateAttempts);
        Assert.False(collector.Snapshot().Complete);
    }

    [Fact]
    public void ReusedEnvelopeIdForDifferentLogicalEventIsRejected()
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(2);
        var first = Publish(collector, 0);
        var second = Publish(collector, 1);
        var id = Guid.NewGuid();
        collector.Start(first, id);
        Assert.Throws<InvalidOperationException>(() => collector.Start(second, id));
        Assert.False(collector.Snapshot().Complete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrEmptyDurableMessageIdIsRejected(bool empty)
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(1);
        Assert.Throws<InvalidOperationException>(() => collector.Start(Publish(collector), empty ? Guid.Empty : null));
        Assert.False(collector.Snapshot().Complete);
    }

    [Fact]
    public void StrictOrderingRejectsConcurrentAndOutOfOrderHandlers()
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(2, requireOrdering: true);
        var zero = Publish(collector, 0);
        var one = Publish(collector, 1);
        var lastFirst = collector.Start(one, Guid.NewGuid());
        var firstLast = collector.Start(zero, Guid.NewGuid());
        collector.Finish(lastFirst);
        collector.Finish(firstLast);
        Settle(collector, lastFirst.MessageId, firstLast.MessageId);
        Assert.Equal(2, collector.Snapshot().MaxConcurrency);
        Assert.True(collector.Snapshot().OrderViolations > 0);
        Assert.False(collector.Snapshot().Complete);
    }

    [Fact]
    public void SettlementBeforeHandlerReturnAndUnknownSettlementAreRejected()
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(1);
        var ticket = collector.Start(Publish(collector), Guid.NewGuid());
        Settle(collector, ticket.MessageId, Guid.NewGuid());
        collector.Finish(ticket);
        Settle(collector, ticket.MessageId);
        Assert.False(collector.Snapshot().Complete);
        Assert.Equal(2, collector.Snapshot().Errors.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DurableBacklogOrDeadLetterPreventsCompletion(int column)
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(1);
        var ticket = collector.Start(Publish(collector), Guid.NewGuid());
        collector.Finish(ticket);
        var counts = new long[5]; counts[column] = 1;
        collector.ObserveStore(Stopwatch.GetTimestamp(), [ticket.MessageId], counts[0], counts[1], counts[2], counts[3], counts[4]);
        Assert.False(collector.Snapshot().Complete);
    }

    [Fact]
    public void SnapshotIsDetachedAndSyntheticAmountsRemainExact()
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(2, requireOrdering: true);
        var zero = collector.Start(Publish(collector, 0, .01234567m), Guid.NewGuid());
        collector.Finish(zero);
        var before = collector.Snapshot();
        var one = collector.Start(Publish(collector, 1, .00000001m), Guid.NewGuid());
        collector.Finish(one);
        Settle(collector, zero.MessageId, one.MessageId);
        var after = collector.Snapshot();
        Assert.Equal(1, before.Completed);
        Assert.Equal(0, before.Settled);
        Assert.Equal(.01234568m, after.RegisteredSyntheticAmount);
        Assert.Equal(after.RegisteredSyntheticAmount, after.SettledSyntheticAmount);
        Assert.True(after.Complete);
        var report = new TransportBenchmarkReport();
        var json = JsonSerializer.Serialize(report, TransportBenchmarkJsonContext.Default.TransportBenchmarkReport);
        Assert.Contains("synthetic", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledSyntheticHandlerCannotBeSettledAsSuccess()
    {
        var collector = new DeliveryCollector(100);
        collector.BeginMeasurement(1);
        var context = new Mock<IEventContext>();
        var id = Guid.NewGuid();
        context.SetupGet(c => c.MessageId).Returns(id);
        context.SetupGet(c => c.CancellationToken).Returns(new CancellationToken(canceled: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BenchHandler<SpendUpdateRequested>(collector).HandleAsync(Publish(collector), context.Object));
        Settle(collector, id);
        Assert.Equal(0, collector.Snapshot().Completed);
        Assert.Equal(0, collector.Snapshot().InFlight);
        Assert.False(collector.Snapshot().Complete);
    }

    [Theory]
    [InlineData("--messages", "0")]
    [InlineData("--handler-cost-ms", "-1")]
    [InlineData("--rate", "NaN")]
    [InlineData("--rate", "Infinity")]
    [InlineData("--receive-batch", "0")]
    [InlineData("--warmup", "0")]
    [InlineData("--timeout-seconds", "0")]
    [InlineData("--database", "production")]
    [InlineData("--database", "conduit_msgbench_x;drop database x")]
    public void InvalidInputsAreRejectedBeforeAnyDatastoreAccess(string key, string value)
    {
        Assert.Null(HarnessOptions.Parse(["--admin-connection", "Host=127.0.0.1;Port=15479;Database=postgres", key, value]));
    }

    [Fact]
    public void DatastoreConnectionMustBeExplicit_AndSafeDefaultsPreserveOrderedPublication()
    {
        Assert.Null(HarnessOptions.Parse([]));
        var options = HarnessOptions.Parse(["--admin-connection", "Host=127.0.0.1;Port=15479;Database=postgres"]);
        Assert.NotNull(options);
        Assert.StartsWith("conduit_msgbench_", options.Database);
        Assert.Equal(1, options.PublishParallelism);
    }

    [Fact]
    public async Task ExistingReceiptIsNotOverwritten_AndCommandFailsBeforeDatastoreAccess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"queue-contract-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "preserved");
        try
        {
            var code = await TransportThroughputHarness.RunAsync(["--admin-connection", "Host=127.0.0.1;Port=1;Database=postgres", "--output", path]);
            Assert.Equal(1, code);
            Assert.Equal("preserved", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmptyResultsCannotQualifyAsSuccess() => Assert.False(RunResult.AllComplete([]));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SuccessRequiresCompleteWarmupAndMeasurementAndSerializesExactRecords(bool complete, bool warmupComplete)
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(1, requireOrdering: true);
        var ticket = collector.Start(Publish(collector), Guid.NewGuid());
        collector.Finish(ticket);
        Settle(collector, ticket.MessageId);
        var snapshot = collector.Snapshot();
        var result = new RunResult("spend", 0, 0, 10, complete, warmupComplete, 100, 90, 80,
            1, 10, 20, 30, true, Enumerable.Repeat(.123, 20).ToArray(), snapshot, snapshot, null,
            new StoreSetup(["test_relation"], ["test_relation"], 1, 0, true, null));
        Assert.Equal(complete && warmupComplete, RunResult.AllComplete([result]));
        var report = new TransportBenchmarkReport();
        report.Results.Add(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report, TransportBenchmarkJsonContext.Default.TransportBenchmarkReport));
        var root = document.RootElement;
        Assert.Equal(Environment.Version.ToString(), root.GetProperty("runtimeVersion").GetString());
        Assert.True(root.GetProperty("gcConfiguration").EnumerateObject().Any());
        var delivery = root.GetProperty("results")[0].GetProperty("measurement").GetProperty("deliveries")[0];
        Assert.Equal(ticket.MessageId, delivery.GetProperty("messageId").GetGuid());
        Assert.Equal(.01m, delivery.GetProperty("syntheticAmount").GetDecimal());
    }

    [Fact]
    public void AlteredSyntheticAmountOnTheWireIsRejected()
    {
        var collector = new DeliveryCollector(0);
        collector.BeginMeasurement(1);
        var message = Publish(collector);
        Assert.Throws<InvalidOperationException>(() => collector.Start(message with { Amount = .02m }, Guid.NewGuid()));
        Assert.False(collector.Snapshot().Complete);
    }

    [Fact]
    public async Task SchemaReadinessWaitsOnlyForMissingRelationsAndRetainsSetupEvidence()
    {
        string[] required = ["schema.inbox", "schema.queue"];
        var polls = 0;
        StoreSetup observed = null;
        await StoreSchemaReadiness.WaitAsync(required, _ => Task.FromResult(++polls == 1 ? [required[0]] : required),
            state => observed = state, CancellationToken.None);
        Assert.Equal(2, polls);
        Assert.True(observed.Ready);
        Assert.Equal(required, observed.RequiredRelations);
        Assert.Equal(required, observed.PresentRelations);
        Assert.Equal(2, observed.Polls);
    }

    [Fact]
    public async Task SchemaQueryFailureIsNotRetriedOrClassifiedAsMissingSchema()
    {
        var polls = 0;
        StoreSetup observed = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreSchemaReadiness.WaitAsync(["schema.inbox"],
            _ => { polls++; throw new InvalidOperationException("query/auth failure"); }, state => observed = state, CancellationToken.None));
        Assert.Equal(1, polls);
        Assert.False(observed.Ready);
        Assert.Contains("query/auth failure", observed.Error);
    }

    [Fact]
    public async Task SchemaReadinessHonorsExistingDeadlineWithoutRunningAProbeAfterCancellation()
    {
        StoreSetup observed = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StoreSchemaReadiness.WaitAsync(["schema.inbox"],
            _ => throw new Xunit.Sdk.XunitException("Cancelled gate must not query"), state => observed = state,
            new CancellationToken(canceled: true)));
        Assert.False(observed.Ready);
        Assert.Equal(0, observed.Polls);
    }
}
