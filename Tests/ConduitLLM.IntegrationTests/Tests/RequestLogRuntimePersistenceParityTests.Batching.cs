using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class RequestLogRuntimePersistenceParityTests
{
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource BatchSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RequestLogRuntimeRecord BatchRow(int key, string model = "model") => new()
    {
        VirtualKeyId = key, ModelName = model, ProviderId = 12, ProviderType = "OpenAI",
        ModelProviderMappingId = 77, PromptCachingEligible = true, PromptCachingPolicyApplied = true,
        CachedReadSavings = 0.12345678m, CacheWritePremium = 0.01234567m,
        RoutingAffinityUsed = true, RoutingDecisionReason = "cache-affinity", RoutingFailoverCount = 2,
        RequestType = "chat", InputTokens = 101, OutputTokens = 37, CachedInputTokens = 80, CachedWriteTokens = 11,
        Cost = 1.23456789m, BillingMethod = 1, ProviderReportedCostUsd = 1.11111111m,
        ProviderCostMarkupMultiplier = 1.125m, BilledAtUtc = new DateTime(2026, 10, 19, 1, 2, 4, DateTimeKind.Utc),
        ResponseTimeMs = 42.75, Timestamp = new DateTime(2026, 10, 19, 1, 2, 3, DateTimeKind.Utc),
        UserId = "user", ClientIp = "192.0.2.10", RequestPath = "/v1/chat/completions", StatusCode = 200,
        MetadataJson = """{"stream":true,"nested":{"count":3}}"""
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_CommitThenCompletionBoundary_AcknowledgesDurabilityOrFailsWithoutRetryAsync(bool loseAcknowledgment)
    {
        var schema = $"request_ack_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var backend = new NpgsqlRequestLogBatchWriter(source);
            var seedEntered = BatchSignal(); var releaseSeed = BatchSignal(); var committed = BatchSignal(); var releaseAck = BatchSignal();
            var calls = new List<int>();
            await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (rows, token) =>
            {
                calls.Add(rows.Length);
                if (rows[0].VirtualKeyId == 0) { seedEntered.TrySetResult(); await releaseSeed.Task.WaitAsync(BatchTimeout, token); }
                var ids = await backend.WriteAsync(rows, token);
                if (rows[0].VirtualKeyId != 0)
                {
                    committed.TrySetResult(); await releaseAck.Task.WaitAsync(BatchTimeout, token);
                    if (loseAcknowledgment) throw new IOException("Simulated lost completion after a real successful COMMIT.");
                }
                return ids;
            });
            var seed = store.WriteAsync(BatchRow(0)); await seedEntered.Task.WaitAsync(BatchTimeout);
            var first = store.WriteAsync(BatchRow(1)); var second = store.WriteAsync(BatchRow(2)); releaseSeed.TrySetResult();
            await seed.WaitAsync(BatchTimeout); await committed.Task.WaitAsync(BatchTimeout);
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
            // An independent physical connection sees both rows before callers
            // receive success. Every scalar matches the existing single-row path.
            Assert.Equal(3, await RequestRowCountAsync(schema));
            var control = new NpgsqlRequestLogRuntimeStore(source);
            var controlId = await control.WriteAsync(BatchRow(3));
            var controlJson = await RequestRowJsonByIdAsync(schema, controlId);
            releaseAck.TrySetResult();
            if (loseAcknowledgment)
            {
                await Assert.ThrowsAsync<IOException>(() => first); await Assert.ThrowsAsync<IOException>(() => second);
            }
            else
            {
                foreach (var task in new[] { first, second })
                {
                    var id = await task.WaitAsync(BatchTimeout);
                    Assert.Equal(controlJson, await RequestRowJsonByIdAsync(schema, id));
                }
            }
            await store.StopAsync(); Assert.Equal(new[] { 1, 2 }, calls);
            Assert.Equal(4, await RequestRowCountAsync(schema));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task WriteAsync_PoisonBatch_ConfirmsRollbackBeforeIsolatingAndCommitsGoodRowOnceAsync()
    {
        var schema = $"request_poison_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var backend = new NpgsqlRequestLogBatchWriter(source); var entered = BatchSignal(); var release = BatchSignal();
            var sizes = new List<int>(); var rollbackObserved = false;
            await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (rows, token) =>
            {
                sizes.Add(rows.Length);
                if (rows[0].VirtualKeyId == 0) { entered.TrySetResult(); await release.Task.WaitAsync(BatchTimeout, token); }
                try { return await backend.WriteAsync(rows, token); }
                catch (RequestLogBatchRolledBackException)
                {
                    rollbackObserved = true;
                    Assert.Equal(1, await RequestRowCountAsync(schema));
                    throw;
                }
            });
            var seed = store.WriteAsync(BatchRow(0)); await entered.Task.WaitAsync(BatchTimeout);
            var good = store.WriteAsync(BatchRow(1)); var poison = store.WriteAsync(BatchRow(2, new string('x', 101)));
            release.TrySetResult(); await seed.WaitAsync(BatchTimeout); Assert.True(await good.WaitAsync(BatchTimeout) > 0);
            var error = await Assert.ThrowsAsync<PostgresException>(() => poison);
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, error.SqlState);
            Assert.True(rollbackObserved); Assert.Equal(new[] { 1, 2, 1, 1 }, sizes);
            Assert.Equal(2, await RequestRowCountAsync(schema));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task WriteAsync_MissingReturningRow_RollsBackWholeBatchWithoutIsolationAsync()
    {
        var schema = $"request_cardinality_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await ExecuteRequestSqlAsync(schema, """
                CREATE FUNCTION suppress_request() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW."ModelName" = 'skip' THEN RETURN NULL; END IF; RETURN NEW; END $$;
                CREATE TRIGGER suppress_request BEFORE INSERT ON "RequestLogs"
                    FOR EACH ROW EXECUTE FUNCTION suppress_request();
                """);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var backend = new NpgsqlRequestLogBatchWriter(source); var entered = BatchSignal(); var release = BatchSignal(); var calls = 0;
            await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (rows, token) =>
            {
                calls++;
                if (rows[0].VirtualKeyId == 0) { entered.TrySetResult(); await release.Task.WaitAsync(BatchTimeout, token); }
                return await backend.WriteAsync(rows, token);
            });
            var seed = store.WriteAsync(BatchRow(0)); await entered.Task.WaitAsync(BatchTimeout);
            var first = store.WriteAsync(BatchRow(1)); var skipped = store.WriteAsync(BatchRow(2, "skip")); release.TrySetResult();
            await seed.WaitAsync(BatchTimeout);
            await Assert.ThrowsAsync<InvalidOperationException>(() => first); await Assert.ThrowsAsync<InvalidOperationException>(() => skipped);
            Assert.Equal(2, calls); Assert.Equal(1, await RequestRowCountAsync(schema));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task StopAsync_CanceledDatabaseBatch_JoinsAndRollsBackAllUnacknowledgedRowsAsync()
    {
        var schema = $"request_stop_{Guid.NewGuid():N}";
        var lockId = Random.Shared.NextInt64(1, long.MaxValue);
        try
        {
            await CreateSchemaAsync(schema);
            await ExecuteRequestSqlAsync(schema, $"""
                CREATE FUNCTION hold_request() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW."ModelName" = 'hold' THEN PERFORM pg_advisory_xact_lock({lockId}); END IF; RETURN NEW; END $$;
                CREATE TRIGGER hold_request BEFORE INSERT ON "RequestLogs"
                    FOR EACH ROW EXECUTE FUNCTION hold_request();
                """);
            await using var gate = new NpgsqlConnection(_fixture.ConnectionString); await gate.OpenAsync();
            await using var takeLock = new NpgsqlCommand($"SELECT pg_advisory_lock({lockId})", gate); await takeLock.ExecuteNonQueryAsync();
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var backend = new NpgsqlRequestLogBatchWriter(source); var entered = BatchSignal(); var release = BatchSignal();
            await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (rows, token) =>
            {
                if (rows[0].VirtualKeyId == 0) { entered.TrySetResult(); await release.Task.WaitAsync(BatchTimeout, token); }
                return await backend.WriteAsync(rows, token);
            });
            var seed = store.WriteAsync(BatchRow(0)); await entered.Task.WaitAsync(BatchTimeout);
            var first = store.WriteAsync(BatchRow(1)); var held = store.WriteAsync(BatchRow(2, "hold")); release.TrySetResult();
            await seed.WaitAsync(BatchTimeout);
            // Prove the real SQL batch reached its second trigger, rather than
            // canceling only an unstarted queue item.
            var deadline = DateTime.UtcNow + BatchTimeout;
            while (true)
            {
                await using var probe = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND classid = @high AND objid = @low", gate);
                probe.Parameters.AddWithValue("high", NpgsqlTypes.NpgsqlDbType.Oid, (uint)(lockId >> 32));
                probe.Parameters.AddWithValue("low", NpgsqlTypes.NpgsqlDbType.Oid, (uint)lockId);
                if ((long)(await probe.ExecuteScalarAsync())! > 0) break;
                Assert.True(DateTime.UtcNow < deadline, "The database batch never reached the held trigger.");
                await Task.Delay(10);
            }
            using var shutdown = new CancellationTokenSource(); shutdown.Cancel(); await store.StopAsync(shutdown.Token).WaitAsync(BatchTimeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
            Assert.Equal(1, await RequestRowCountAsync(schema));
        }
        finally { await DropSchemaAsync(schema); }
    }

    private async Task<long> RequestRowCountAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(WithSearchPath(schema)); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"RequestLogs\"", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> RequestRowJsonByIdAsync(string schema, int id)
    {
        await using var connection = new NpgsqlConnection(WithSearchPath(schema)); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT (to_jsonb(row) - 'Id' - 'VirtualKeyId')::text FROM \"RequestLogs\" row WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", id); return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteRequestSqlAsync(string schema, string sql)
    {
        await using var connection = new NpgsqlConnection(WithSearchPath(schema)); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }
}
