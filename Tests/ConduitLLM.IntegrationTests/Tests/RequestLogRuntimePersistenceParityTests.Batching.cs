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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WriteAsync_MissingReturningRow_RollsBackWholeBatchWithoutIsolationAsync(int skippedIndex)
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
            var writes = Enumerable.Range(0, 3)
                .Select(index => store.WriteAsync(BatchRow(index + 1, index == skippedIndex ? "skip" : "model")))
                .ToArray();
            release.TrySetResult();
            await seed.WaitAsync(BatchTimeout);
            foreach (var write in writes) await Assert.ThrowsAsync<InvalidOperationException>(() => write);
            Assert.Equal(2, calls); Assert.Equal(1, await RequestRowCountAsync(schema));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task WriteAsync_UserTriggerDivisionByZero_ConfirmsRollbackBeforeIsolatingAndCommitsGoodRowsOnceAsync()
    {
        var schema = $"request_user_division_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await ExecuteRequestSqlAsync(schema, """
                CREATE FUNCTION reject_request_by_division() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."ModelName" = 'divide' THEN
                        PERFORM 1 / (NEW."InputTokens" - 101);
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER reject_request_by_division BEFORE INSERT ON "RequestLogs"
                    FOR EACH ROW EXECUTE FUNCTION reject_request_by_division();
                """);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var backend = new NpgsqlRequestLogBatchWriter(source);
            var entered = BatchSignal(); var release = BatchSignal();
            var sizes = new List<int>(); var rollbackConfirmed = false;
            await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (rows, token) =>
            {
                sizes.Add(rows.Length);
                if (rows[0].VirtualKeyId == 0) { entered.TrySetResult(); await release.Task.WaitAsync(BatchTimeout, token); }
                try { return await backend.WriteAsync(rows, token); }
                catch (RequestLogBatchRolledBackException error)
                {
                    Assert.Equal(PostgresErrorCodes.DivisionByZero, Assert.IsType<PostgresException>(error.InnerException).SqlState);
                    Assert.Equal(1, await RequestRowCountAsync(schema));
                    rollbackConfirmed = true;
                    throw;
                }
            });
            var seed = store.WriteAsync(BatchRow(0)); await entered.Task.WaitAsync(BatchTimeout);
            var writes = new[]
            {
                store.WriteAsync(BatchRow(1)), store.WriteAsync(BatchRow(2, "divide")), store.WriteAsync(BatchRow(3))
            };
            release.TrySetResult(); await seed.WaitAsync(BatchTimeout);
            var firstId = await writes[0].WaitAsync(BatchTimeout);
            var error = await Assert.ThrowsAsync<PostgresException>(() => writes[1]);
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
            var lastId = await writes[2].WaitAsync(BatchTimeout);
            Assert.True(firstId > 0); Assert.True(lastId > 0); Assert.NotEqual(firstId, lastId);
            // A genuine server SQL rejection before COMMIT is eligible for
            // isolated retries only after the original batch's rollback is confirmed.
            Assert.True(rollbackConfirmed);
            Assert.Equal(new[] { 1, 3, 1, 1, 1 }, sizes);
            Assert.Equal(3, await RequestRowCountAsync(schema));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task WriteAsync_DeferredConstraintAtCommit_FailsAllCallersWithoutIsolationOrEarlyIdsAsync()
    {
        var schema = $"request_deferred_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await ExecuteRequestSqlAsync(schema, """
                CREATE FUNCTION reject_deferred_request() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."ModelName" = 'deferred-poison' THEN
                        RAISE EXCEPTION 'deferred request rejected' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER reject_deferred_request
                    AFTER INSERT ON "RequestLogs" DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION reject_deferred_request();
                """);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var backend = new NpgsqlRequestLogBatchWriter(source);
            var entered = BatchSignal(); var release = BatchSignal();
            var commitFailed = BatchSignal(); var releaseFailure = BatchSignal();
            var attempts = new List<int[]>();
            await using var store = new NpgsqlBatchingRequestLogRuntimeStore(async (rows, token) =>
            {
                attempts.Add(rows.Select(row => row.VirtualKeyId).ToArray());
                if (rows[0].VirtualKeyId == 0) { entered.TrySetResult(); await release.Task.WaitAsync(BatchTimeout, token); }
                try { return await backend.WriteAsync(rows, token); }
                catch (PostgresException error)
                {
                    Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
                    // The earlier INSERTs already returned IDs, but the final
                    // explicit COMMIT rejected the deferred constraint. Once
                    // COMMIT starts the conservative policy never isolates.
                    Assert.Equal(1, await RequestRowCountAsync(schema));
                    commitFailed.TrySetResult();
                    await releaseFailure.Task.WaitAsync(BatchTimeout, token);
                    throw;
                }
            });
            var seed = store.WriteAsync(BatchRow(0)); await entered.Task.WaitAsync(BatchTimeout);
            var first = store.WriteAsync(BatchRow(1));
            var poison = store.WriteAsync(BatchRow(2, "deferred-poison"));
            var last = store.WriteAsync(BatchRow(3));
            release.TrySetResult(); await seed.WaitAsync(BatchTimeout);
            await commitFailed.Task.WaitAsync(BatchTimeout);
            Assert.False(first.IsCompleted); Assert.False(poison.IsCompleted); Assert.False(last.IsCompleted);
            releaseFailure.TrySetResult();
            foreach (var write in new[] { first, poison, last })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => write);
                Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            }
            Assert.Equal(new[] { 1, 3 }, attempts.Select(rows => rows.Length));
            Assert.Equal(new[] { 1, 2, 3 }, attempts[1]);
            Assert.Equal(1, await RequestRowCountAsync(schema));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task WriteAsync_MixedNullableAndPopulatedBatch_PreservesEveryColumnAndIdAssociationAsync()
    {
        var schema = $"request_batch_parity_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var nullable = BatchRow(42, "nullable") with
            {
                ProviderId = null, ProviderType = null, ModelProviderMappingId = null,
                PromptCachingEligible = false, PromptCachingPolicyApplied = false,
                CachedReadSavings = 0, CacheWritePremium = 0, RoutingAffinityUsed = false,
                RoutingDecisionReason = null, RoutingFailoverCount = 0, InputTokens = 0, OutputTokens = 0,
                CachedInputTokens = null, CachedWriteTokens = null, Cost = 0, BillingMethod = null,
                ProviderReportedCostUsd = null, ProviderCostMarkupMultiplier = null, BilledAtUtc = null,
                ResponseTimeMs = 0, UserId = null, ClientIp = null, RequestPath = null,
                StatusCode = null, MetadataJson = null
            };
            RequestLogRuntimeRecord[] rows =
            [
                BatchRow(41), nullable,
                nullable with { VirtualKeyId = 43, ModelName = "json-null", MetadataJson = "null" }
            ];
            var ids = await new NpgsqlRequestLogBatchWriter(source).WriteAsync(rows, CancellationToken.None);
            Assert.Equal(rows.Length, ids.Length); Assert.Equal(rows.Length, ids.Distinct().Count());
            var single = new NpgsqlRequestLogRuntimeStore(source);
            for (var index = 0; index < rows.Length; index++)
            {
                Assert.True(ids[index] > 0);
                var controlId = await single.WriteAsync(rows[index]);
                Assert.Equal(await RequestRowJsonByIdAsync(schema, controlId),
                    await RequestRowJsonByIdAsync(schema, ids[index]));
                await using var command = source.CreateCommand("""
                    SELECT "VirtualKeyId", "Metadata" IS NULL FROM "RequestLogs" WHERE "Id" = @id
                    """);
                command.Parameters.AddWithValue("id", ids[index]);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(rows[index].VirtualKeyId, reader.GetInt32(0));
                Assert.Equal(index == 1, reader.GetBoolean(1));
                Assert.False(await reader.ReadAsync());
            }
            Assert.Equal(6, await RequestRowCountAsync(schema));
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
