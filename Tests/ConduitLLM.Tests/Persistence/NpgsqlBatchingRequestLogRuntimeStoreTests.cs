using System.Text.Json;

using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Npgsql;

using Xunit;

namespace ConduitLLM.Tests.Persistence;

public sealed class NpgsqlBatchingRequestLogRuntimeStoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static RequestLogRuntimeRecord Row(int id) => new()
    {
        VirtualKeyId = id, ModelName = "model", RequestType = "chat", Timestamp = DateTime.UtcNow
    };

    [Fact]
    public async Task WriteAsync_AdmittedWrite_WaitsForCommittedIdDespiteCallerCancellationAsync()
    {
        var entered = Signal(); var commit = Signal(); var input = Row(17);
        await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (records, token) =>
        {
            Assert.Single(records); Assert.NotSame(input, records[0]);
            entered.TrySetResult(); await commit.Task.WaitAsync(Timeout, token); return [71];
        });
        using var caller = new CancellationTokenSource();
        var write = store.WriteAsync(input, caller.Token);
        await entered.Task.WaitAsync(Timeout); caller.Cancel();
        Assert.False(write.IsCompleted);
        commit.TrySetResult(); Assert.Equal(71, await write.WaitAsync(Timeout));
    }

    [Fact]
    public async Task WriteAsync_FullChannel_BackpressuresAndCancelsBeforeAdmissionAsync()
    {
        var entered = Signal(); var release = Signal(); var seen = new List<int>();
        await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (records, token) =>
        {
            seen.AddRange(records.Select(r => r.VirtualKeyId));
            if (records[0].VirtualKeyId == 1) { entered.TrySetResult(); await release.Task.WaitAsync(Timeout, token); }
            return records.Select(r => r.VirtualKeyId).ToArray();
        }, capacity: 1);
        var first = store.WriteAsync(Row(1)); await entered.Task.WaitAsync(Timeout);
        var second = store.WriteAsync(Row(2));
        using var canceled = new CancellationTokenSource();
        var blocked = store.WriteAsync(Row(3), canceled.Token); Assert.False(blocked.IsCompleted);
        canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        release.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Timeout);
        Assert.Equal(new[] { 1, 2 }, seen);
    }

    [Fact]
    public async Task WriteAsync_AvailableBacklog_DrainsImmediatelyInBatchesOfAtMost100Async()
    {
        var entered = Signal(); var release = Signal(); var sizes = new List<int>();
        await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (records, token) =>
        {
            sizes.Add(records.Length);
            if (records[0].VirtualKeyId == 0) { entered.TrySetResult(); await release.Task.WaitAsync(Timeout, token); }
            return records.Select(r => r.VirtualKeyId).ToArray();
        });
        var first = store.WriteAsync(Row(0)); await entered.Task.WaitAsync(Timeout);
        var backlog = Enumerable.Range(1, 205).Select(i => store.WriteAsync(Row(i))).ToArray();
        release.TrySetResult(); await first.WaitAsync(Timeout);
        Assert.Equal(Enumerable.Range(1, 205), await Task.WhenAll(backlog).WaitAsync(Timeout));
        Assert.Equal(new[] { 1, 100, 100, 5 }, sizes);
    }

    [Fact]
    public async Task WriteAsync_ConfirmedSqlRollback_IsolatesPoisonWithoutRejectingGoodRowAsync()
    {
        var entered = Signal(); var release = Signal(); var attempts = new List<int[]>();
        await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (records, token) =>
        {
            attempts.Add(records.Select(r => r.VirtualKeyId).ToArray());
            if (records[0].VirtualKeyId == 0) { entered.TrySetResult(); await release.Task.WaitAsync(Timeout, token); }
            if (records.Any(r => r.VirtualKeyId == 2))
                throw new RequestLogBatchRolledBackException(new InvalidOperationException("confirmed SQL rejection"));
            return records.Select(r => r.VirtualKeyId + 100).ToArray();
        });
        var first = store.WriteAsync(Row(0)); await entered.Task.WaitAsync(Timeout);
        var good = store.WriteAsync(Row(1)); var poison = store.WriteAsync(Row(2)); release.TrySetResult();
        await first.WaitAsync(Timeout); Assert.Equal(101, await good.WaitAsync(Timeout));
        await Assert.ThrowsAsync<RequestLogBatchRolledBackException>(() => poison);
        Assert.Equal(new[] { 1, 2, 1, 1 }, attempts.Select(a => a.Length));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("cancellation")]
    [InlineData("cardinality")]
    public async Task WriteAsync_UncertainOrInvalidOutcome_DoesNotRetryAsync(string failure)
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (records, token) =>
        {
            calls++;
            if (records[0].VirtualKeyId == 0) { entered.TrySetResult(); await release.Task.WaitAsync(Timeout, token); return [0]; }
            if (failure == "network") throw new IOException("commit acknowledgment lost");
            if (failure == "cancellation") throw new OperationCanceledException();
            return [1]; // Committed IDs cannot be safely assigned to two callers.
        });
        var first = store.WriteAsync(Row(0)); await entered.Task.WaitAsync(Timeout);
        var writes = new[] { store.WriteAsync(Row(1)), store.WriteAsync(Row(2)) }; release.TrySetResult();
        await first.WaitAsync(Timeout);
        foreach (var write in writes) await Assert.ThrowsAnyAsync<Exception>(() => write);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StopAsync_GracefulShutdown_ClosesAdmissionDrainsAndJoinsAsync()
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (records, token) =>
        {
            calls++; if (records[0].VirtualKeyId == 1) { entered.TrySetResult(); await release.Task.WaitAsync(Timeout, token); }
            return records.Select(r => r.VirtualKeyId).ToArray();
        });
        var first = store.WriteAsync(Row(1)); await entered.Task.WaitAsync(Timeout); var second = store.WriteAsync(Row(2));
        var stop = store.StopAsync(); Assert.False(stop.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(Row(3)));
        release.TrySetResult(); await stop.WaitAsync(Timeout); Assert.Equal(new[] { 1, 2 }, await Task.WhenAll(first, second));
        await store.StopAsync(); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StopAsync_CanceledShutdown_FailsAllAcceptedAndBlockedWaitersAsync()
    {
        var entered = Signal();
        await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (records, token) =>
        {
            entered.TrySetResult(); await Task.Delay(System.Threading.Timeout.Infinite, token); return [1];
        }, capacity: 1);
        var first = store.WriteAsync(Row(1)); await entered.Task.WaitAsync(Timeout);
        var second = store.WriteAsync(Row(2)); var blocked = store.WriteAsync(Row(3));
        using var shutdown = new CancellationTokenSource(); shutdown.Cancel();
        await store.StopAsync(shutdown.Token).WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await Assert.ThrowsAsync<InvalidOperationException>(() => blocked);
    }

    [Fact]
    public async Task LogRequestAsync_MutableCallerDto_QueuedSnapshotOwnsAllPayloadAsync()
    {
        var entered = Signal(); var release = Signal(); RequestLogRuntimeRecord? saved = null;
        await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (records, token) =>
        {
            if (records[0].VirtualKeyId == 0) { entered.TrySetResult(); await release.Task.WaitAsync(Timeout, token); }
            else saved = Assert.Single(records);
            return records.Select(r => r.VirtualKeyId).ToArray();
        });
        var first = store.WriteAsync(Row(0)); await entered.Task.WaitAsync(Timeout);
        var writer = new StoreBackedRequestLogRuntimeWriter(store);
        using var original = JsonDocument.Parse("""{"nested":{"value":"original"}}""");
        using var changed = JsonDocument.Parse("""{"nested":{"value":"changed"}}""");
        var dto = new LogRequestDto
        {
            VirtualKeyId = 1, ModelName = "original", RequestType = "chat", Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, JsonElement> { ["details"] = original.RootElement }
        };
        var write = writer.LogRequestAsync(dto); dto.ModelName = "changed"; dto.Cost = 99;
        dto.Metadata["details"] = changed.RootElement;
        dto.Metadata["added"] = changed.RootElement;
        original.Dispose(); // Queued work must own JSON, not the caller's document.
        release.TrySetResult(); await Task.WhenAll(first, write).WaitAsync(Timeout);
        Assert.NotNull(saved); Assert.Equal("original", saved.ModelName); Assert.Equal(0m, saved.Cost);
        using var savedMetadata = JsonDocument.Parse(saved.MetadataJson!);
        Assert.Equal("original", savedMetadata.RootElement.GetProperty("details").GetProperty("nested").GetProperty("value").GetString());
        Assert.False(savedMetadata.RootElement.TryGetProperty("added", out _));
    }
}
