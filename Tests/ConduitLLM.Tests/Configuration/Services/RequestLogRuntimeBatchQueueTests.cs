using System.Text.Json;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class RequestLogRuntimeBatchQueueTests
{
    [Fact]
    public async Task ThresholdUsesOwnedSnapshotsAndRetainsTheExistingHundredRowBoundary()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var store = new RecordingStore();
        using var queue = Create(provider, store);
        using var value = JsonDocument.Parse("true");
        var request = Request();
        request.Metadata = new Dictionary<string, JsonElement> { ["original"] = value.RootElement.Clone() };
        await queue.LogRequestAsync(request);
        request.ModelName = "mutated";
        request.InputTokens = 999;
        request.Metadata.Clear();
        for (var i = 1; i < 99; i++) await queue.LogRequestAsync(Request());
        Assert.Empty(store.Batches);
        Assert.Equal(0, store.Allocations);
        await queue.LogRequestAsync(Request());
        var batch = Assert.Single(store.Batches);
        Assert.Equal(100, batch.Count);
        Assert.Equal(100, batch.Select(row => row.Id).Distinct().Count());
        Assert.Equal("original-model", batch[0].Record.ModelName);
        Assert.Equal(2, batch[0].Record.InputTokens);
        using var metadata = JsonDocument.Parse(batch[0].Record.MetadataJson!);
        Assert.True(metadata.RootElement.GetProperty("original").GetBoolean());
        await queue.StopAsync(CancellationToken.None);
        Assert.Single(store.Batches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InvalidCompleteAllocationDoesNotAssignAnyQueueIdentity(int invalid)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var store = new RecordingStore { InvalidAllocation = invalid };
        using var queue = Create(provider, store);
        await queue.LogRequestAsync(Request());
        await queue.LogRequestAsync(Request());
        await queue.FlushEventsAsync(); // Existing normal flush logs/requeues failures.
        Assert.Empty(store.Batches);
        store.InvalidAllocation = null;
        await queue.FlushEventsAsync();
        Assert.Equal(2, store.Allocations);
        Assert.Equal(2, Assert.Single(store.Batches).Count);
    }

    [Fact]
    public async Task CompletionFailureRequeuesTheSameImmutableIdsWithoutReallocation()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var store = new RecordingStore { CompletionFailures = 1 };
        using var queue = Create(provider, store);
        await queue.LogRequestAsync(Request());
        await queue.FlushEventsAsync();
        await queue.FlushEventsAsync();
        Assert.Equal(1, store.Allocations);
        Assert.Equal(2, store.Batches.Count);
        Assert.Equal(store.Batches[0].ToArray(), store.Batches[1].ToArray());
    }

    [Fact]
    public async Task ShutdownBacklogAndCancellationPreserveAllAssignedRowsForRetry()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var store = new RecordingStore { CompletionFailures = int.MaxValue };
        using var queue = Create(provider, store);
        for (var i = 0; i < 205; i++) await queue.LogRequestAsync(Request());
        store.CompletionFailures = 0;
        store.CancelWrite = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => queue.StopAsync(CancellationToken.None));
        var failed = store.Batches[^1];
        Assert.Equal(205, failed.Count);
        var allocations = store.Allocations;
        store.CancelWrite = false;
        await queue.StopAsync(CancellationToken.None);
        Assert.Equal(allocations, store.Allocations);
        Assert.Equal(failed.ToArray(), store.Batches[^1].ToArray());
    }

    [Fact]
    public async Task ThresholdManualFlushAndShutdownHaveOnePersistenceOwner()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var store = new RecordingStore { PauseFirst = true };
        using var queue = Create(provider, store);
        for (var i = 0; i < 99; i++) await queue.LogRequestAsync(Request());
        var threshold = queue.LogRequestAsync(Request());
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = queue.FlushEventsAsync();
        var stop = queue.StopAsync(CancellationToken.None);
        store.Release.TrySetResult();
        await Task.WhenAll(threshold, manual, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, store.MaximumConcurrentWrites);
        Assert.Equal(100, Assert.Single(store.Batches).Count);
    }

    private static RequestLogService Create(IServiceProvider provider, IRequestLogBatchRuntimeStore store) =>
        RequestLogService.CreateWithRuntimePersistence(provider, store, NullLogger<RequestLogService>.Instance);

    private static LogRequestDto Request() => new()
    {
        VirtualKeyId = 1, ModelName = "original-model", RequestType = "chat", InputTokens = 2,
        OutputTokens = 3, Cost = .25m, Timestamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private sealed class RecordingStore : IRequestLogBatchRuntimeStore
    {
        private int _nextId, _active;
        public int Allocations, CompletionFailures, MaximumConcurrentWrites;
        public int? InvalidAllocation;
        public bool CancelWrite, PauseFirst;
        public List<IReadOnlyList<RequestLogRuntimeBatchEntry>> Batches { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<int>> AllocateIdsAsync(int count, CancellationToken cancellationToken = default)
        {
            Allocations++;
            IReadOnlyList<int> values = InvalidAllocation switch
            {
                0 => [], 1 => Enumerable.Repeat(1, count).ToArray(),
                2 => Enumerable.Repeat(0, count).ToArray(),
                _ => Enumerable.Range(0, count).Select(_ => ++_nextId).ToArray()
            };
            return Task.FromResult(values);
        }

        public async Task WriteBatchAsync(IReadOnlyList<RequestLogRuntimeBatchEntry> entries,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites, active);
            try
            {
                Batches.Add(entries.ToArray());
                Entered.TrySetResult();
                if (PauseFirst) { PauseFirst = false; await Release.Task; }
                if (CancelWrite) throw new OperationCanceledException();
                if (CompletionFailures > 0) { CompletionFailures--; throw new IOException("Simulated completion failure."); }
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
