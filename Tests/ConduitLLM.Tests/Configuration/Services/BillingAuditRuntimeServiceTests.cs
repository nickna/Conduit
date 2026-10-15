using System.Reflection;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class BillingAuditRuntimeServiceTests
{
    private static readonly DateTime Instant = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
    private static readonly DateTime Generated = new(2026, 10, 11, 13, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RuntimePersistenceMapsAllScalarsAndCopiesBackOnlyGeneratedDefaults()
    {
        var provider = new UnavailableProvider();
        var writer = new RecordingWriter();
        using var service = Create(provider, writer);
        var generated = Event("generated", defaults: true);
        var supplied = Event("supplied", defaults: false);
        var originalGenerated = Snapshot(generated);
        var originalSupplied = Snapshot(supplied);
        await service.LogBillingEventAsync(generated);
        service.LogBillingEvent(supplied);
        Assert.Empty(writer.Calls);

        await service.StopAsync(CancellationToken.None);

        var call = Assert.Single(writer.Calls);
        Assert.Equal(new[] { originalGenerated, originalSupplied }, call.Records);
        Assert.False(call.Token.CanBeCanceled);
        Assert.NotEqual(Guid.Empty, generated.Id);
        Assert.Equal(Generated, generated.Timestamp);
        Assert.Equal(originalGenerated with { Id = generated.Id, Timestamp = generated.Timestamp }, Snapshot(generated));
        Assert.Equal(originalSupplied, Snapshot(supplied));
        Assert.Equal(0, provider.Accesses);
    }

    [Theory]
    [InlineData("null-result")]
    [InlineData("wrong-count")]
    [InlineData("null-item")]
    [InlineData("reordered")]
    [InlineData("changed-scalar")]
    [InlineData("changed-supplied-id")]
    [InlineData("changed-supplied-timestamp")]
    [InlineData("changed-supplied-kind")]
    [InlineData("empty-generated-id")]
    [InlineData("duplicate-generated-id")]
    [InlineData("default-generated-timestamp")]
    [InlineData("nonutc-generated-timestamp")]
    public async Task WholeCommittedResultIsValidatedBeforeAnyEntityMutationAndCanBeRetried(string defect)
    {
        var provider = new UnavailableProvider();
        var writer = new RecordingWriter();
        using var service = Create(provider, writer);
        var first = Event("first", defaults: true);
        var second = Event("second", defaults: false);
        var originals = new[] { Snapshot(first), Snapshot(second) };
        writer.Behavior = (records, _) =>
        {
            var result = Generate(records).ToArray();
            switch (defect)
            {
                case "null-result": return Task.FromResult<IReadOnlyList<BillingAuditRuntimeRecord>>(null!);
                case "wrong-count": return Task.FromResult<IReadOnlyList<BillingAuditRuntimeRecord>>([result[0]]);
                case "null-item": result[1] = null!; break;
                case "reordered": Array.Reverse(result); break;
                case "changed-scalar": result[1] = result[1] with { ProviderType = "changed" }; break;
                case "changed-supplied-id": result[1] = result[1] with { Id = Guid.NewGuid() }; break;
                case "changed-supplied-timestamp": result[1] = result[1] with { Timestamp = Instant.AddTicks(1) }; break;
                case "changed-supplied-kind": result[1] = result[1] with { Timestamp = DateTime.SpecifyKind(Instant, DateTimeKind.Unspecified) }; break;
                case "empty-generated-id": result[0] = result[0] with { Id = Guid.Empty }; break;
                case "duplicate-generated-id": result[0] = result[0] with { Id = result[1].Id }; break;
                case "default-generated-timestamp": result[0] = result[0] with { Timestamp = default }; break;
                case "nonutc-generated-timestamp": result[0] = result[0] with { Timestamp = DateTime.SpecifyKind(Generated, DateTimeKind.Unspecified) }; break;
            }
            return Task.FromResult<IReadOnlyList<BillingAuditRuntimeRecord>>(result);
        };
        await service.LogBillingEventAsync(first);
        await service.LogBillingEventAsync(second);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopAsync(CancellationToken.None));
        Assert.Equal(originals, new[] { Snapshot(first), Snapshot(second) });
        Assert.Single(writer.Calls);
        writer.Behavior = (records, _) => Task.FromResult(Generate(records));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(2, writer.Calls.Count);
        Assert.Equal(originals, writer.Calls[1].Records);
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.Equal(Generated, first.Timestamp);
        Assert.Equal(originals[1], Snapshot(second));
        Assert.Equal(0, provider.Accesses);
    }

    [Fact]
    public async Task ThresholdFailureIsSwallowedAndAllHundredEventsRecoverBeforeTheTail()
    {
        var provider = new UnavailableProvider();
        var writer = new RecordingWriter
        {
            Behavior = (_, _) => Task.FromException<IReadOnlyList<BillingAuditRuntimeRecord>>(new IOException("fixture write failed"))
        };
        using var service = Create(provider, writer);
        var rows = Enumerable.Range(0, 101).Select(index => Event(index.ToString(), defaults: true)).ToArray();
        foreach (var row in rows.Take(99)) await service.LogBillingEventAsync(row);
        Assert.Empty(writer.Calls);
        await service.LogBillingEventAsync(rows[99]);
        Assert.Equal(100, Assert.Single(writer.Calls).Records.Count);
        Assert.All(rows, row => { Assert.Equal(Guid.Empty, row.Id); Assert.Equal(default, row.Timestamp); });

        writer.Behavior = (records, _) => Task.FromResult(Generate(records));
        await service.LogBillingEventAsync(rows[100]);
        Assert.Equal(2, writer.Calls.Count);
        Assert.Equal(100, writer.Calls[1].Records.Count);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, writer.Calls[2].Records.Count);
        var successful = writer.Calls.Skip(1).SelectMany(call => call.Records).ToArray();
        Assert.Equal(rows.Select(row => row.RequestId), successful.Select(row => row.RequestId));
        Assert.All(rows, row => { Assert.NotEqual(Guid.Empty, row.Id); Assert.Equal(Generated, row.Timestamp); });
        Assert.Equal(0, provider.Accesses);
    }

    [Fact]
    public async Task ShutdownFailureIsRethrownAndTheSameInstanceCanDrainItsRequeuedTail()
    {
        var provider = new UnavailableProvider();
        var failure = new IOException("fixture shutdown failure");
        var writer = new RecordingWriter
        {
            Behavior = (_, _) => Task.FromException<IReadOnlyList<BillingAuditRuntimeRecord>>(failure)
        };
        using var service = Create(provider, writer);
        var rows = Enumerable.Range(0, 7).Select(index => Event(index.ToString(), defaults: true)).ToArray();
        foreach (var row in rows) await service.LogBillingEventAsync(row);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.StopAsync(CancellationToken.None)));
        Assert.All(rows, row => Assert.Equal(Guid.Empty, row.Id));
        writer.Behavior = (records, _) => Task.FromResult(Generate(records));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(2, writer.Calls.Count);
        Assert.Equal(writer.Calls[0].Records, writer.Calls[1].Records);
        Assert.All(rows, row => Assert.NotEqual(Guid.Empty, row.Id));
        Assert.Equal(0, provider.Accesses);
    }

    [Fact]
    public async Task ShutdownForwardsCancellationRequeuesAndAllowsSameInstanceRecovery()
    {
        var provider = new UnavailableProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingWriter
        {
            Behavior = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable fixture continuation.");
            }
        };
        using var service = Create(provider, writer);
        var row = Event("cancelled", defaults: true);
        await service.LogBillingEventAsync(row);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stop = service.StopAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(cancellation.Token, Assert.Single(writer.Calls).Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            cancellation.Cancel();
            try { await stop.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { /* Expected owned cancellation; every other failure propagates. */ }
        }
        Assert.Equal(Guid.Empty, row.Id);
        Assert.Equal(default, row.Timestamp);
        writer.Behavior = (records, _) => Task.FromResult(Generate(records));
        await service.StopAsync(CancellationToken.None);
        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal(2, writer.Calls.Count);
        Assert.Equal(0, provider.Accesses);
    }

    [Fact]
    public async Task NavigationGraphsAreRejectedBeforeWritingOrMutatingAnyQueuedEntity()
    {
        var provider = new UnavailableProvider();
        var writer = new RecordingWriter();
        using var service = Create(provider, writer);
        var first = Event("first", defaults: true);
        var second = Event("second", defaults: true);
        second.VirtualKey = new VirtualKey { Id = 41, KeyName = "unexpected navigation" };
        await service.LogBillingEventAsync(first);
        await service.LogBillingEventAsync(second);
        await Assert.ThrowsAsync<ArgumentException>(() => service.StopAsync(CancellationToken.None));
        Assert.Empty(writer.Calls);
        Assert.Equal(Guid.Empty, first.Id);
        Assert.Equal(default, first.Timestamp);
        second.VirtualKey = null;
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(2, Assert.Single(writer.Calls).Records.Count);
        Assert.Equal(0, provider.Accesses);
    }

    [Fact]
    public async Task RuntimeRetentionSchedulerCompletesWithoutAccessingAnEfScope()
    {
        var provider = new UnavailableProvider();
        using var service = Create(provider, new RecordingWriter());
        var scheduler = typeof(BillingAuditService).GetMethod("ScheduleDataRetentionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(scheduler);
        Assert.Equal(typeof(BillingAuditService), scheduler.DeclaringType);
        var scheduled = Assert.IsAssignableFrom<Task>(scheduler.Invoke(service, [CancellationToken.None]));
        Assert.True(scheduled.IsCompletedSuccessfully);
        await scheduled;
        Assert.Equal(0, provider.Accesses);
    }

    [Fact]
    public async Task NativeTimerFlushesSubthresholdEventsAndStopJoinsTheActiveWriteBeforeDrainingTheTail()
    {
        var provider = new UnavailableProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var callNumber = 0;
        var writer = new RecordingWriter
        {
            Behavior = async (records, _) =>
            {
                if (Interlocked.Increment(ref callNumber) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(deadline.Token);
                }
                return Generate(records);
            }
        };
        using var service = Create(provider, writer);
        var first = Event("timer", defaults: true);
        var tail = Event("shutdown-tail", defaults: true);
        await service.LogBillingEventAsync(first);
        await service.StartAsync(deadline.Token);
        Assert.Empty(writer.Calls);
        Task? stop = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
            await service.LogBillingEventAsync(tail);
            stop = service.StopAsync(deadline.Token);
            Assert.False(stop.IsCompleted);
            release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            Assert.Equal(2, writer.Calls.Count);
            Assert.Equal("timer", Assert.Single(writer.Calls[0].Records).RequestId);
            Assert.Equal("shutdown-tail", Assert.Single(writer.Calls[1].Records).RequestId);
            Assert.False(writer.Calls[0].Token.CanBeCanceled);
            Assert.Equal(deadline.Token, writer.Calls[1].Token);
            Assert.NotEqual(Guid.Empty, first.Id);
            Assert.NotEqual(Guid.Empty, tail.Id);
            Assert.Equal(0, provider.Accesses);
        }
        finally
        {
            release.TrySetResult();
            if (stop is null) await service.StopAsync(CancellationToken.None);
            else await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void LegacyConstructorAndFactoryKeepOriginalGuardsAndSinglePublicConstructor()
    {
        var constructor = Assert.Single(typeof(BillingAuditService).GetConstructors());
        Assert.Equal(new[] { typeof(IServiceProvider), typeof(Microsoft.Extensions.Logging.ILogger<BillingAuditService>) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        var provider = new UnavailableProvider();
        var logger = NullLogger<BillingAuditService>.Instance;
        using var legacy = new BillingAuditService(provider, logger);
        Assert.Null(typeof(BillingAuditService).GetField("_runtimeWriter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(legacy));
        Assert.Equal("serviceProvider", Assert.Throws<ArgumentNullException>(() => new BillingAuditService(null!, logger)).ParamName);
        Assert.Equal("logger", Assert.Throws<ArgumentNullException>(() => new BillingAuditService(provider, null!)).ParamName);
        Assert.Equal("runtimeWriter", Assert.Throws<ArgumentNullException>(() =>
            BillingAuditService.CreateWithRuntimePersistence(provider, null!, logger)).ParamName);
        Assert.Equal("runtimeWriter", Assert.Throws<ArgumentNullException>(() =>
            BillingAuditService.CreateWithRuntimePersistence(null!, null!, null!)).ParamName);
        Assert.Equal(0, provider.Accesses);
    }

    private static BillingAuditService Create(IServiceProvider provider, IBillingAuditRuntimeWriter writer) =>
        BillingAuditService.CreateWithRuntimePersistence(provider, writer, NullLogger<BillingAuditService>.Instance);

    private static BillingAuditEvent Event(string requestId, bool defaults) => new()
    {
        Id = defaults ? Guid.Empty : Guid.NewGuid(), Timestamp = defaults ? default : Instant,
        EventType = (BillingAuditEventType)197, VirtualKeyId = 41, VirtualKeyGroupId = -77,
        Model = "O'Hare 雪", RequestId = requestId, UsageJson = """{"unicode":"café 雪 🔎","null":null}""",
        CalculatedCost = 0.1250005m, FailureReason = "diagnostic 雪", ProviderType = "future-provider",
        HttpStatusCode = -197, MetadataJson = "null", RequestPath = "/v1/雪", IsEstimated = true,
        ToolUsageJson = """[{"name":"雪"}]""", ToolUsageCost = -0.0000005m
    };

    private static BillingAuditRuntimeRecord Snapshot(BillingAuditEvent row) => new()
    {
        Id = row.Id, Timestamp = row.Timestamp, EventType = (int)row.EventType,
        VirtualKeyId = row.VirtualKeyId, VirtualKeyGroupId = row.VirtualKeyGroupId, Model = row.Model,
        RequestId = row.RequestId, UsageJson = row.UsageJson, CalculatedCost = row.CalculatedCost,
        FailureReason = row.FailureReason, ProviderType = row.ProviderType, HttpStatusCode = row.HttpStatusCode,
        MetadataJson = row.MetadataJson, RequestPath = row.RequestPath, IsEstimated = row.IsEstimated,
        ToolUsageJson = row.ToolUsageJson, ToolUsageCost = row.ToolUsageCost
    };

    private static IReadOnlyList<BillingAuditRuntimeRecord> Generate(IReadOnlyList<BillingAuditRuntimeRecord> records) =>
        records.Select(row => row with
        {
            Id = row.Id == Guid.Empty ? Guid.NewGuid() : row.Id,
            Timestamp = row.Timestamp == default ? Generated : row.Timestamp
        }).ToArray();

    private sealed class UnavailableProvider : IServiceProvider
    {
        private int _accesses;
        public int Accesses => Volatile.Read(ref _accesses);
        public object? GetService(Type serviceType)
        {
            Interlocked.Increment(ref _accesses);
            throw new InvalidOperationException("Runtime audit path attempted to access scoped services.");
        }
    }

    private sealed record WriteCall(IReadOnlyList<BillingAuditRuntimeRecord> Records, CancellationToken Token);
    private sealed class RecordingWriter : IBillingAuditRuntimeWriter
    {
        private readonly object _gate = new();
        private readonly List<WriteCall> _calls = [];
        public Func<IReadOnlyList<BillingAuditRuntimeRecord>, CancellationToken, Task<IReadOnlyList<BillingAuditRuntimeRecord>>> Behavior { get; set; } =
            (records, _) => Task.FromResult(Generate(records));
        public IReadOnlyList<WriteCall> Calls { get { lock (_gate) return _calls.ToArray(); } }
        public Task<IReadOnlyList<BillingAuditRuntimeRecord>> WriteBatchAsync(
            IReadOnlyList<BillingAuditRuntimeRecord> records, CancellationToken cancellationToken = default)
        {
            lock (_gate) _calls.Add(new WriteCall(records.ToArray(), cancellationToken));
            return Behavior(records, cancellationToken);
        }
    }
}
