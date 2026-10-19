using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class RequestLogRuntimePersistenceParityTests
{
    [Fact]
    public Task CommittedBatchWithLostCompletionReplaysExactlyOnceThroughTheQueue() => InBatchSchema(async (_, source) =>
    {
        var store = new LoseCommittedCompletion(new NpgsqlRequestLogBatchRuntimeStore(source));
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var queue = RequestLogService.CreateWithRuntimePersistence(provider, store, NullLogger<RequestLogService>.Instance);
        await queue.LogRequestAsync(new LogRequestDto
        {
            VirtualKeyId = 41, ModelName = "committed-before-error", RequestType = "chat", Cost = .25m,
            Timestamp = BatchRecord().Timestamp
        });
        await queue.FlushEventsAsync(); // Real transaction committed, decorator then threw.
        Assert.Equal(1, await CountRows(source));
        await queue.FlushEventsAsync();
        Assert.Equal(1, await CountRows(source));
        Assert.Equal(1, store.Allocations);
        Assert.Equal(store.Attempts[0].ToArray(), store.Attempts[1].ToArray());
        await queue.StopAsync(CancellationToken.None);
    });

    [Fact]
    public Task ReplayUsesDatabaseDecimalTimestampAndJsonbStorageSemantics() => InBatchSchema(async (_, source) =>
    {
        var store = new NpgsqlRequestLogBatchRuntimeStore(source);
        var id = (await store.AllocateIdsAsync(1))[0];
        var original = BatchRecord() with
        {
            Cost = 1.234567895m, CachedReadSavings = .123456785m, CacheWritePremium = .987654325m,
            ProviderReportedCostUsd = 2.234567895m, ProviderCostMarkupMultiplier = 1.123456785m,
            Timestamp = BatchRecord().Timestamp.AddTicks(9), BilledAtUtc = BatchRecord().Timestamp.AddTicks(8),
            MetadataJson = """{"x":1,"y":[true]}"""
        };
        await store.WriteBatchAsync([new(id, original)]);
        await store.WriteBatchAsync([new(id, original with
        {
            Cost = 1.234567899m, CachedReadSavings = .123456789m, CacheWritePremium = .987654329m,
            ProviderReportedCostUsd = 2.234567899m, ProviderCostMarkupMultiplier = 1.123456789m,
            Timestamp = original.Timestamp.AddTicks(-1), BilledAtUtc = original.BilledAtUtc!.Value.AddTicks(-1),
            MetadataJson = """{ "y": [true], "x": 1 }"""
        })]);
        Assert.Equal(1, await CountRows(source));
    });

    [Fact]
    public Task EveryChangedScalarRejectsReplayAndRollsBackNewRows() => InBatchSchema(async (schema, source) =>
    {
        var store = new NpgsqlRequestLogBatchRuntimeStore(source);
        var ids = await store.AllocateIdsAsync(2);
        var record = BatchRecord();
        await store.WriteBatchAsync([new(ids[0], record)]);
        var before = await ReadRowAsJsonAsync(schema);
        Func<RequestLogRuntimeRecord, RequestLogRuntimeRecord>[] changes =
        [
            r => r with { VirtualKeyId = 42 }, r => r with { ModelName = "other" },
            r => r with { ProviderId = null }, r => r with { ProviderType = null },
            r => r with { ModelProviderMappingId = null }, r => r with { PromptCachingEligible = false },
            r => r with { PromptCachingPolicyApplied = false }, r => r with { CachedReadSavings = .9m },
            r => r with { CacheWritePremium = .9m }, r => r with { RoutingAffinityUsed = false },
            r => r with { RoutingDecisionReason = null }, r => r with { RoutingFailoverCount = 9 },
            r => r with { RequestType = "other" }, r => r with { InputTokens = 9 },
            r => r with { OutputTokens = 9 }, r => r with { CachedInputTokens = null },
            r => r with { CachedWriteTokens = null }, r => r with { Cost = .9m },
            r => r with { BillingMethod = null }, r => r with { ProviderReportedCostUsd = null },
            r => r with { ProviderCostMarkupMultiplier = null }, r => r with { BilledAtUtc = null },
            r => r with { ResponseTimeMs = 9 }, r => r with { Timestamp = r.Timestamp.AddMilliseconds(1) },
            r => r with { UserId = null }, r => r with { ClientIp = null },
            r => r with { RequestPath = null }, r => r with { StatusCode = null },
            r => r with { MetadataJson = null }, r => r with { MetadataJson = "null" }
        ];
        foreach (var change in changes)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteBatchAsync(
                [new(ids[0], change(record)), new(ids[1], record)]));
            Assert.Equal(1, await CountRows(source));
            Assert.Equal(before, await ReadRowAsJsonAsync(schema));
        }
    });

    [Fact]
    public Task SqlNullAndJsonNullAreDistinctReplayPayloads() => InBatchSchema(async (_, source) =>
    {
        var store = new NpgsqlRequestLogBatchRuntimeStore(source);
        var ids = await store.AllocateIdsAsync(2);
        await store.WriteBatchAsync([new(ids[0], BatchRecord() with { MetadataJson = null }),
            new(ids[1], BatchRecord() with { MetadataJson = "null" })]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteBatchAsync(
            [new(ids[0], BatchRecord() with { MetadataJson = "null" })]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteBatchAsync(
            [new(ids[1], BatchRecord() with { MetadataJson = null })]));
        Assert.Equal(2, await CountRows(source));
    });

    [Fact]
    public Task BacklogPartialCommitThenFailureReplaysCommittedSubBatches() => InBatchSchema(async (_, source) =>
    {
        var store = new NpgsqlRequestLogBatchRuntimeStore(source);
        var ids = await store.AllocateIdsAsync(205);
        var record = BatchRecord();
        var batch = ids.Select(id => new RequestLogRuntimeBatchEntry(id, record)).ToArray();
        batch[150] = batch[150] with { Record = record with { MetadataJson = "invalid json" } };
        await Assert.ThrowsAsync<PostgresException>(() => store.WriteBatchAsync(batch));
        Assert.Equal(100, await CountRows(source));
        batch[150] = batch[150] with { Record = record };
        await store.WriteBatchAsync(batch);
        Assert.Equal(205, await CountRows(source));
        await store.WriteBatchAsync(batch);
        Assert.Equal(205, await CountRows(source));
    });

    [Fact]
    public Task ConcurrentAllocationAndGeneratedIdsAreDisjointAndMatchingReplaysConverge() => InBatchSchema(async (_, source) =>
    {
        var first = new NpgsqlRequestLogBatchRuntimeStore(source);
        var second = new NpgsqlRequestLogBatchRuntimeStore(source);
        var allocated = await Task.WhenAll(first.AllocateIdsAsync(20), second.AllocateIdsAsync(20));
        var ordinary = await new NpgsqlRequestLogRuntimeStore(source).WriteAsync(BatchRecord());
        var ids = allocated.SelectMany(values => values).Append(ordinary).ToArray();
        Assert.Equal(41, ids.Distinct().Count());
        var batch = allocated[0].Select(id => new RequestLogRuntimeBatchEntry(id, BatchRecord())).ToArray();
        await Task.WhenAll(first.WriteBatchAsync(batch), second.WriteBatchAsync(batch.Reverse().ToArray()));
        Assert.Equal(21, await CountRows(source));
    });

    [Fact]
    public Task CancelledInsertRollsBackAndCanRetryWithTheSameIds() => InBatchSchema(async (_, source) =>
    {
        var store = new NpgsqlRequestLogBatchRuntimeStore(source);
        var id = (await store.AllocateIdsAsync(1))[0];
        await using (var blocker = await source.OpenConnectionAsync())
        await using (var transaction = await blocker.BeginTransactionAsync())
        {
            await using var command = new NpgsqlCommand("LOCK TABLE \"RequestLogs\" IN ACCESS EXCLUSIVE MODE", blocker, transaction);
            await command.ExecuteNonQueryAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteBatchAsync([new(id, BatchRecord())], cancellation.Token));
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await CountRows(source));
        await store.WriteBatchAsync([new(id, BatchRecord())]);
        Assert.Equal(1, await CountRows(source));
    });

    [Fact]
    public Task CancellationObservedDuringActualServerCommitRollsBackAndCanRetry() => InBatchSchema(async (schema, source) =>
    {
        await using (var command = source.CreateCommand($"""
            CREATE FUNCTION "{schema}".delay_commit() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN PERFORM pg_sleep(10); RETURN NEW; END $body$;
            CREATE CONSTRAINT TRIGGER delay_request_log_commit AFTER INSERT ON "RequestLogs"
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION "{schema}".delay_commit();
            """))
            await command.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(WithSearchPath(schema)) { ApplicationName = schema }.ConnectionString;
        await using var committingSource = NpgsqlDataSource.Create(connectionString);
        var store = new NpgsqlRequestLogBatchRuntimeStore(committingSource);
        var id = (await store.AllocateIdsAsync(1))[0];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = store.WriteBatchAsync([new(id, BatchRecord())], cancellation.Token);
        var sawCommit = false;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && !pending.IsCompleted)
            {
                await using var observed = source.CreateCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @application AND query = 'COMMIT' AND wait_event = 'PgSleep')
                    """);
                observed.Parameters.AddWithValue("application", schema);
                if ((bool)(await observed.ExecuteScalarAsync())!) { sawCommit = true; break; }
                await Task.Delay(20);
            }
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(sawCommit, "Cancellation must follow observation of COMMIT executing its deferred trigger.");
        Assert.Equal(0, await CountRows(source));
        await using (var command = source.CreateCommand("DROP TRIGGER delay_request_log_commit ON \"RequestLogs\""))
            await command.ExecuteNonQueryAsync();
        await store.WriteBatchAsync([new(id, BatchRecord())]);
        Assert.Equal(1, await CountRows(source));
    });

    private async Task InBatchSchema(Func<string, NpgsqlDataSource, Task> action)
    {
        var schema = $"request_log_batch_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            await action(schema, source);
        }
        finally { await DropSchemaAsync(schema); }
    }

    private static async Task<long> CountRows(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT count(*) FROM \"RequestLogs\"");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static RequestLogRuntimeRecord BatchRecord() => new()
    {
        VirtualKeyId = 41, ModelName = "batch-model", ProviderId = 1, ProviderType = "OpenAI",
        ModelProviderMappingId = 2, PromptCachingEligible = true, PromptCachingPolicyApplied = true,
        CachedReadSavings = .1m, CacheWritePremium = .2m, RoutingAffinityUsed = true,
        RoutingDecisionReason = "affinity", RoutingFailoverCount = 1, RequestType = "chat",
        InputTokens = 2, OutputTokens = 3, CachedInputTokens = 1, CachedWriteTokens = 1,
        Cost = .25m, BillingMethod = 1, ProviderReportedCostUsd = .2m, ProviderCostMarkupMultiplier = 1.25m,
        Timestamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        BilledAtUtc = new DateTime(2026, 10, 1, 0, 0, 1, DateTimeKind.Utc), ResponseTimeMs = 12.5,
        UserId = "user", ClientIp = "192.0.2.1", RequestPath = "/v1/chat/completions", StatusCode = 200,
        MetadataJson = """{"stream":true}"""
    };

    private sealed class LoseCommittedCompletion(IRequestLogBatchRuntimeStore inner) : IRequestLogBatchRuntimeStore
    {
        private bool _fail = true;
        public int Allocations;
        public List<IReadOnlyList<RequestLogRuntimeBatchEntry>> Attempts { get; } = [];
        public Task<IReadOnlyList<int>> AllocateIdsAsync(int count, CancellationToken cancellationToken = default)
        { Allocations++; return inner.AllocateIdsAsync(count, cancellationToken); }
        public async Task WriteBatchAsync(IReadOnlyList<RequestLogRuntimeBatchEntry> entries, CancellationToken cancellationToken = default)
        {
            Attempts.Add(entries.ToArray());
            await inner.WriteBatchAsync(entries, cancellationToken); // Actual PostgreSQL commit completed.
            if (_fail) { _fail = false; throw new IOException("Lost completion after a real successful commit."); }
        }
    }
}
