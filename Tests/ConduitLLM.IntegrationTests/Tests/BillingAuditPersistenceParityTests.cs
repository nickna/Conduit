using System.Data.Common;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class BillingAuditPersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    private static readonly DateTime Instant = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedBatchesRoundTripSeventeenScalarsWithoutChangingInputs(bool native)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var records = new[]
        {
            Populated(),
            new BillingAuditRuntimeRecord { Id = Guid.NewGuid(), Timestamp = Instant, EventType = -197 },
            Populated() with
            {
                UsageJson = "null", MetadataJson = "[]", ToolUsageJson = "null", ProviderType = "",
                Model = "", RequestId = "", FailureReason = "", RequestPath = "", IsEstimated = false,
                CalculatedCost = -0.000000005m, ToolUsageCost = 9999.99999999m
            }
        };
        var originals = records.ToArray();
        var result = await database.Writer(native).WriteBatchAsync(records, database.Token);
        Assert.Equal(records.Length, result.Count);
        Assert.Equal(originals, records);
        for (var index = 0; index < records.Length; index++)
        {
            Assert.Equal(records[index], result[index]);
            await AssertRowAsync(database, result[index]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseDefaultsMatchLegacyEfAndOnlySentinelsChange(bool native)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var legacy = new BillingAuditEvent { Id = Guid.Empty, Timestamp = default, EventType = (BillingAuditEventType)197 };
        var before = DateTime.UtcNow.AddSeconds(-1);
        await using (var context = database.CreateContext())
        {
            context.BillingAuditEvents.Add(legacy);
            await context.SaveChangesAsync(database.Token);
        }
        Assert.NotEqual(Guid.Empty, legacy.Id);
        Assert.InRange(legacy.Timestamp, before, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(DateTimeKind.Utc, legacy.Timestamp.Kind);

        var sentinel = Populated() with { Id = Guid.Empty, Timestamp = default };
        var supplied = Populated();
        var records = new[]
        {
            sentinel, supplied,
            Populated() with { Id = Guid.Empty },
            Populated() with { Timestamp = default }
        };
        var result = await database.Writer(native).WriteBatchAsync(records, database.Token);
        Assert.Equal(sentinel, records[0]);
        Assert.Equal(supplied, records[1]);
        Assert.NotEqual(Guid.Empty, result[0].Id);
        Assert.NotEqual(legacy.Id, result[0].Id);
        Assert.InRange(result[0].Timestamp, before, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(DateTimeKind.Utc, result[0].Timestamp.Kind);
        Assert.Equal(sentinel with { Id = result[0].Id, Timestamp = result[0].Timestamp }, result[0]);
        Assert.Equal(supplied, result[1]);
        Assert.Equal(Instant.Ticks, result[1].Timestamp.Ticks);
        Assert.NotEqual(Guid.Empty, result[2].Id);
        Assert.Equal(records[2].Timestamp, result[2].Timestamp);
        Assert.Equal(Instant.Ticks, result[2].Timestamp.Ticks);
        Assert.Equal(records[3].Id, result[3].Id);
        Assert.InRange(result[3].Timestamp, before, DateTime.UtcNow.AddSeconds(1));
        for (var index = 0; index < records.Length; index++)
            Assert.Equal(records[index] with { Id = result[index].Id, Timestamp = result[index].Timestamp }, result[index]);
        Assert.Equal(Guid.Empty, records[2].Id);
        Assert.Equal(default, records[3].Timestamp);
        foreach (var row in result) await AssertRowAsync(database, row);
        var legacyStored = await database.ReadAsync(legacy.Id);
        Assert.NotNull(legacyStored);
        Assert.Equal(legacy.Id, legacyStored.Id);
        Assert.Equal(legacy.Timestamp, legacyStored.Timestamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateFailureRollsBackGeneratedDefaultsAndAllowsOriginalBatchRetry(bool native)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var records = new[]
        {
            Populated() with { Id = Guid.Empty, Timestamp = default },
            Populated() with { MetadataJson = "not json" }
        };
        var original = records.ToArray();
        var writer = database.Writer(native);
        await AssertSqlStateAsync(() => writer.WriteBatchAsync(records, database.Token), PostgresErrorCodes.InvalidTextRepresentation);
        Assert.Equal(original, records);
        Assert.Equal(0L, await database.CountAsync());
        records[1] = records[1] with { MetadataJson = original[0].MetadataJson };
        var committed = await writer.WriteBatchAsync(records, database.Token);
        Assert.Equal(records[0], original[0]);
        Assert.NotEqual(Guid.Empty, committed[0].Id);
        Assert.Equal(records[1], committed[1]);
        Assert.Equal(2L, await database.CountAsync());
        foreach (var row in committed) await AssertRowAsync(database, row);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownSizedBatchRollsBackAcrossCommandBatchesAndRetriesAtomically(bool native)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        // Force the EF reference through several command batches, each within
        // the same SaveChanges transaction. A shutdown drain can exceed 100.
        var writer = database.Writer(native, maxBatchSize: 32);
        var rows = Enumerable.Range(0, 129).Select(_ =>
            Populated() with { Id = Guid.Empty, Timestamp = default }).ToArray();
        rows[^1] = rows[^1] with { ToolUsageJson = "invalid final row" };
        var originals = rows.ToArray();
        await AssertSqlStateAsync(() => writer.WriteBatchAsync(rows, database.Token), PostgresErrorCodes.InvalidTextRepresentation);
        Assert.Equal(0L, await database.CountAsync());
        Assert.Equal(originals, rows);
        rows[^1] = rows[^1] with { ToolUsageJson = "null" };
        var committed = await writer.WriteBatchAsync(rows, database.Token);
        Assert.Equal(129, committed.Count);
        Assert.Equal(129, committed.Select(row => row.Id).Distinct().Count());
        Assert.Equal(129L, await database.CountAsync());
        for (var index = 0; index < rows.Length; index++)
        {
            Assert.Equal(Guid.Empty, rows[index].Id);
            Assert.Equal(default, rows[index].Timestamp);
            Assert.Equal(rows[index] with { Id = committed[index].Id, Timestamp = committed[index].Timestamp }, committed[index]);
            await AssertRowAsync(database, committed[index]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseConstraintsRejectWholeBatchesAndPreserveTheCommittedRow(bool native)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var writer = database.Writer(native);
        var seed = Populated();
        await writer.WriteBatchAsync([seed], database.Token);
        var failures = new (BillingAuditRuntimeRecord Row, string State)[]
        {
            (Populated() with { UsageJson = "{" }, PostgresErrorCodes.InvalidTextRepresentation),
            (Populated() with { MetadataJson = "{" }, PostgresErrorCodes.InvalidTextRepresentation),
            (Populated() with { ToolUsageJson = "{" }, PostgresErrorCodes.InvalidTextRepresentation),
            (Populated() with { CalculatedCost = 10000m }, PostgresErrorCodes.NumericValueOutOfRange),
            (Populated() with { ToolUsageCost = -10000m }, PostgresErrorCodes.NumericValueOutOfRange),
            (Populated() with { VirtualKeyId = int.MinValue }, PostgresErrorCodes.ForeignKeyViolation),
            (Populated() with { Model = new string('m', 101) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { RequestId = new string('r', 101) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { FailureReason = new string('f', 501) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { ProviderType = new string('p', 51) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { RequestPath = new string('x', 257) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { Id = seed.Id }, PostgresErrorCodes.UniqueViolation)
        };
        foreach (var (row, state) in failures)
        {
            var first = Populated() with { Id = Guid.Empty, Timestamp = default };
            var batch = new[] { first, row };
            await AssertSqlStateAsync(() => writer.WriteBatchAsync(batch, database.Token), state);
            Assert.Equal(first, batch[0]);
            Assert.Equal(row, batch[1]);
            Assert.Equal(1L, await database.CountAsync());
            await AssertRowAsync(database, seed);
        }
        // VirtualKeyGroupId is a nullable correlation scalar, without a foreign key.
        var unreferencedGroup = Populated() with { VirtualKeyGroupId = int.MinValue };
        await writer.WriteBatchAsync([unreferencedGroup], database.Token);
        await AssertRowAsync(database, unreferencedGroup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationRequestIdUniquenessAppliesOnlyToEventFifteen(bool native)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var writer = database.Writer(native);
        var seed = Populated() with { EventType = 15, RequestId = "reconcile 雪" };
        await writer.WriteBatchAsync([seed], database.Token);
        var duplicate = seed with { Id = Guid.NewGuid() };
        await AssertSqlStateAsync(() => writer.WriteBatchAsync(
            [Populated() with { Id = Guid.Empty }, duplicate], database.Token), PostgresErrorCodes.UniqueViolation);
        Assert.Equal(1L, await database.CountAsync());
        var permitted = new[]
        {
            seed with { Id = Guid.NewGuid(), EventType = 1 },
            seed with { Id = Guid.NewGuid(), EventType = 1 },
            seed with { Id = Guid.NewGuid(), RequestId = null },
            seed with { Id = Guid.NewGuid(), RequestId = null }
        };
        var committed = await writer.WriteBatchAsync(permitted, database.Token);
        Assert.Equal(5L, await database.CountAsync());
        foreach (var row in committed) await AssertRowAsync(database, row);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeListValidationAndCancellationPrecedeConnectionAcquisition(bool native)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var writer = database.Writer(native);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var nullBatch = await Assert.ThrowsAsync<ArgumentNullException>(() => writer.WriteBatchAsync(null!, cancelled.Token));
        Assert.Equal("records", nullBatch.ParamName);
        var row = Populated() with { Id = Guid.Empty, Timestamp = default };
        var nullItem = await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteBatchAsync([row, null!], cancelled.Token));
        Assert.Equal("records", nullItem.ParamName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteBatchAsync([], cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteBatchAsync([row], cancelled.Token));
        Assert.Equal(Guid.Empty, row.Id);
        Assert.Equal(default, row.Timestamp);
        Assert.Empty(await writer.WriteBatchAsync([], database.Token));
        Assert.Equal(0L, await database.CountAsync());

        // An unavailable factory/source independently proves empty/invalid calls need no connection.
        await using var unavailable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=unused;Timeout=1;Pooling=false");
        IBillingAuditRuntimeWriter noDatabase = native
            ? new NpgsqlBillingAuditRuntimeWriter(unavailable)
            : new EfBillingAuditRuntimeWriter(new UnavailableContextFactory());
        Assert.Empty(await noDatabase.WriteBatchAsync([], database.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => noDatabase.WriteBatchAsync([row, null!], database.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => noDatabase.WriteBatchAsync([row], cancelled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringObservedSqlLockWaitRollsBackAndReusesTheWriter(bool native)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var writer = database.Writer(native);
        await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Pooling = false, ApplicationName = "billing_audit_blocker" }.ConnectionString);
        await blocker.OpenAsync(database.Token);
        await using var transaction = await blocker.BeginTransactionAsync(database.Token);
        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = """LOCK TABLE "BillingAuditEvents" IN ACCESS EXCLUSIVE MODE""";
            await command.ExecuteNonQueryAsync(database.Token);
        }
        var row = Populated() with { Id = Guid.Empty, Timestamp = default };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(database.Token);
        var pending = writer.WriteBatchAsync([row], cancellation.Token);
        try
        {
            await database.WaitForLockWaitAsync(pending);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Guid.Empty, row.Id);
            Assert.Equal(default, row.Timestamp);
        }
        finally { await DrainAsync(pending, cancellation); }
        await transaction.RollbackAsync(database.Token);
        Assert.Equal(0L, await database.CountAsync());
        var committed = await writer.WriteBatchAsync([row, Populated()], database.Token);
        Assert.Equal(2L, await database.CountAsync());
        foreach (var snapshot in committed) await AssertRowAsync(database, snapshot);
    }

    [Fact]
    public async Task SingleSlotPoolCancellationLeavesSentinelsUntouchedAndPermitsReuse()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var writer = database.Writer(true);
        var row = Populated() with { Id = Guid.Empty, Timestamp = default };
        await using (await database.Source.OpenConnectionAsync(database.Token))
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(database.Token);
            var pending = writer.WriteBatchAsync([row], cancellation.Token);
            try
            {
                Assert.False(pending.IsCompleted);
                Assert.Empty(await writer.WriteBatchAsync([], database.Token));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { await DrainAsync(pending, cancellation); }
        }
        Assert.Equal(Guid.Empty, row.Id);
        Assert.Equal(default, row.Timestamp);
        Assert.Equal(0L, await database.CountAsync());
        var committed = Assert.Single(await writer.WriteBatchAsync([row], database.Token));
        await AssertRowAsync(database, committed);
    }

    [Fact]
    public Task OwnedEfAndTypedBackendsCloseAfterSuccessfulFixtureDisposal() =>
        AssertFixtureBackendReleaseAsync(failQuery: false);

    [Fact]
    public Task OwnedEfAndTypedBackendsCloseAfterAnEfQueryFailure() =>
        AssertFixtureBackendReleaseAsync(failQuery: true);

    [Fact]
    public async Task SetupFailureClosesBothOwnedBackendsAndPreservesThePrimaryException()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var observer = new NpgsqlConnection(RegressionConnectionString("observer", pooling: false));
        await observer.OpenAsync(deadline.Token);
        await using var sentinelSource = NpgsqlDataSource.Create(RegressionConnectionString("sentinel", pooling: true));
        await using var sentinel = await sentinelSource.OpenConnectionAsync(deadline.Token);
        var sentinelPid = await BackendPidAsync(sentinel, deadline.Token);
        var primary = new InvalidOperationException("Injected owned EF setup failure.");
        string? schema = null;
        var efPid = 0;
        var typedPid = 0;

        var actual = await Record.ExceptionAsync(async () =>
        {
            await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString, async (owned, context) =>
            {
                schema = new NpgsqlConnectionStringBuilder(owned.ConnectionString).SearchPath;
                efPid = await BackendPidAsync(context.Database.GetDbConnection(), deadline.Token);
                await using (var typed = await owned.Source.OpenConnectionAsync(deadline.Token))
                    typedPid = await BackendPidAsync(typed, deadline.Token);
                throw primary;
            });
        });

        Assert.Same(primary, actual);
        Assert.NotNull(schema);
        Assert.True(efPid > 0);
        Assert.True(typedPid > 0);
        Assert.NotEqual(efPid, typedPid);
        Assert.NotEqual(sentinelPid, efPid);
        Assert.NotEqual(sentinelPid, typedPid);
        await WaitForOwnedBackendsAndSchemaToDisappearAsync(observer, schema!, efPid, typedPid, deadline.Token);
        Assert.Equal(sentinelPid, await BackendPidAsync(sentinel, deadline.Token));
        await AssertSentinelUsableAsync(sentinel, deadline.Token);
    }

    private async Task AssertFixtureBackendReleaseAsync(bool failQuery)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var observer = new NpgsqlConnection(RegressionConnectionString("observer", pooling: false));
        await observer.OpenAsync(deadline.Token);
        await using var sentinelSource = NpgsqlDataSource.Create(RegressionConnectionString("sentinel", pooling: true));
        await using var sentinel = await sentinelSource.OpenConnectionAsync(deadline.Token);
        var sentinelPid = await BackendPidAsync(sentinel, deadline.Token);
        var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var schema = new NpgsqlConnectionStringBuilder(database.ConnectionString).SearchPath!;
        var efPid = 0;
        var typedPid = 0;
        try
        {
            Assert.NotSame(database.Source, database.EfSource);
            await using var context = database.CreateContext();
            await context.Database.OpenConnectionAsync(deadline.Token);
            efPid = await BackendPidAsync(context.Database.GetDbConnection(), deadline.Token);
            await using (var typed = await database.Source.OpenConnectionAsync(deadline.Token))
                typedPid = await BackendPidAsync(typed, deadline.Token);
            Assert.True(efPid > 0);
            Assert.True(typedPid > 0);
            Assert.NotEqual(efPid, typedPid);
            Assert.NotEqual(sentinelPid, efPid);
            Assert.NotEqual(sentinelPid, typedPid);
            if (failQuery)
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT 1 / 0";
                var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    await command.ExecuteScalarAsync(deadline.Token);
                });
                Assert.Equal(PostgresErrorCodes.DivisionByZero, failure.SqlState);
            }
        }
        finally { await database.DisposeAsync(); }

        await WaitForOwnedBackendsAndSchemaToDisappearAsync(observer, schema, efPid, typedPid, deadline.Token);
        Assert.Equal(sentinelPid, await BackendPidAsync(sentinel, deadline.Token));
        await AssertSentinelUsableAsync(sentinel, deadline.Token);
    }

    private string RegressionConnectionString(string role, bool pooling) =>
        new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = $"ba_{role}_{Guid.NewGuid():N}",
            Pooling = pooling,
            MaxPoolSize = 1
        }.ConnectionString;

    private static async Task<int> BackendPidAsync(DbConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_backend_pid()";
        return (int)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task AssertSentinelUsableAsync(NpgsqlConnection sentinel, CancellationToken token)
    {
        await using var command = sentinel.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(1, (int)(await command.ExecuteScalarAsync(token))!);
    }

    private static async Task WaitForOwnedBackendsAndSchemaToDisappearAsync(
        NpgsqlConnection observer, string schema, int efPid, int typedPid, CancellationToken token)
    {
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT NOT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = ANY(@pids))
                AND to_regnamespace(@schema) IS NULL
            """;
        command.Parameters.AddWithValue("pids", new[] { efPid, typedPid });
        command.Parameters.AddWithValue("schema", schema);
        while (!(bool)(await command.ExecuteScalarAsync(token))!)
            await Task.Delay(25, token);
    }

    private static BillingAuditRuntimeRecord Populated() => new()
    {
        Id = Guid.NewGuid(), Timestamp = Instant, EventType = 197, VirtualKeyId = 41, VirtualKeyGroupId = 7,
        Model = "O'Hare 雪", RequestId = Guid.NewGuid().ToString("D"),
        UsageJson = """{"quoted":"café 雪 🔎","nested":[1,null,false]}""", CalculatedCost = 0.125000005m,
        FailureReason = "diagnostic O'Hare 雪", ProviderType = "future-provider 雪", HttpStatusCode = -197,
        MetadataJson = """{"missing":null,"value":true}""", RequestPath = "/v1/雪'O", IsEstimated = true,
        ToolUsageJson = """[{"name":"雪","count":2}]""", ToolUsageCost = -0.000000005m
    };

    private static async Task AssertRowAsync(OwnedDatabase database, BillingAuditRuntimeRecord expected)
    {
        var row = await database.ReadAsync(expected.Id);
        Assert.NotNull(row);
        Assert.Equal(expected.Id, row.Id);
        Assert.Equal(expected.Timestamp.Ticks / 10 * 10, row.Timestamp.Ticks);
        Assert.Equal(DateTimeKind.Utc, row.Timestamp.Kind);
        Assert.Equal(expected.EventType, row.EventType);
        Assert.Equal(expected.VirtualKeyId, row.VirtualKeyId);
        Assert.Equal(expected.VirtualKeyGroupId, row.VirtualKeyGroupId);
        Assert.Equal(expected.Model, row.Model);
        Assert.Equal(expected.RequestId, row.RequestId);
        AssertJson(expected.UsageJson, row.UsageJson);
        Assert.Equal(Rounded(expected.CalculatedCost), row.CalculatedCost);
        Assert.Equal(expected.FailureReason, row.FailureReason);
        Assert.Equal(expected.ProviderType, row.ProviderType);
        Assert.Equal(expected.HttpStatusCode, row.HttpStatusCode);
        AssertJson(expected.MetadataJson, row.MetadataJson);
        Assert.Equal(expected.RequestPath, row.RequestPath);
        Assert.Equal(expected.IsEstimated, row.IsEstimated);
        AssertJson(expected.ToolUsageJson, row.ToolUsageJson);
        Assert.Equal(Rounded(expected.ToolUsageCost), row.ToolUsageCost);
    }

    private static decimal? Rounded(decimal? value) =>
        value is null ? null : decimal.Round(value.Value, 8, MidpointRounding.AwayFromZero);

    private static void AssertJson(string? expected, string? actual)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is null) return;
        using var left = JsonDocument.Parse(expected);
        using var right = JsonDocument.Parse(actual!);
        Assert.True(JsonElement.DeepEquals(left.RootElement, right.RootElement));
    }

    private static async Task AssertSqlStateAsync(Func<Task> operation, string state)
    {
        var exception = await Record.ExceptionAsync(operation);
        Assert.NotNull(exception);
        Assert.Equal(state, Assert.IsType<PostgresException>(exception.GetBaseException()).SqlState);
    }

    private static async Task DrainAsync(Task operation, CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private sealed class ContextFactory(NpgsqlDataSource dataSource, int? maxBatchSize = null) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql(dataSource, options => { if (maxBatchSize is { } size) options.MaxBatchSize(size); }).Options);
    }

    private sealed class UnavailableContextFactory : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => throw new InvalidOperationException("Empty batch acquired a context.");
    }

    private sealed class OwnedDatabase : IAsyncDisposable
    {
        private const string Prefix = "billing_audit_parity_";
        private readonly string _management;
        private readonly string _schema;
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
        public string ConnectionString { get; }
        public NpgsqlDataSource Source { get; }
        public NpgsqlDataSource EfSource { get; }
        public CancellationToken Token => _deadline.Token;

        private OwnedDatabase(string management)
        {
            _management = management;
            _schema = Prefix + Guid.NewGuid().ToString("N");
            ConnectionString = new NpgsqlConnectionStringBuilder(management)
                { SearchPath = _schema, ApplicationName = _schema, MaxPoolSize = 1 }.ConnectionString;
            Source = NpgsqlDataSource.Create(ConnectionString);
            try { EfSource = NpgsqlDataSource.Create(ConnectionString); }
            catch (Exception primary)
            {
                try { Source.Dispose(); }
                catch (Exception cleanup)
                {
                    throw new AggregateException("Owned billing audit data source initialization and cleanup failed.", primary, cleanup);
                }
                finally { _deadline.Dispose(); }
                throw;
            }
        }

        public static async Task<OwnedDatabase> CreateAsync(string management, Func<OwnedDatabase, ConduitDbContext, Task>? setupProbe = null)
        {
            var database = new OwnedDatabase(management);
            try
            {
                await using (var connection = new NpgsqlConnection(management))
                {
                    await connection.OpenAsync(database.Token);
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"CREATE SCHEMA \"{database._schema}\"";
                    await command.ExecuteNonQueryAsync(database.Token);
                }
                await using var context = database.CreateContext();
                await context.Database.OpenConnectionAsync(database.Token);
                if (setupProbe is not null) await setupProbe(database, context);
                await using (var command = context.Database.GetDbConnection().CreateCommand())
                {
                    command.CommandText = context.Database.GenerateCreateScript();
                    await command.ExecuteNonQueryAsync(database.Token);
                }
                context.AddRange(new VirtualKeyGroup { Id = 7, GroupName = "billing audit group" },
                    new VirtualKey { Id = 41, KeyName = "billing audit key", KeyHash = "billing-audit-key-hash", VirtualKeyGroupId = 7 });
                await context.SaveChangesAsync(database.Token);
                return database;
            }
            catch (Exception primary)
            {
                try { await database.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException("Billing audit setup and cleanup failed.", primary, cleanup); }
                throw;
            }
        }

        public ConduitDbContext CreateContext() => new ContextFactory(EfSource).CreateDbContext();
        public IBillingAuditRuntimeWriter Writer(bool native, int? maxBatchSize = null) => native
            ? new NpgsqlBillingAuditRuntimeWriter(Source) : new EfBillingAuditRuntimeWriter(new ContextFactory(EfSource, maxBatchSize));

        public async Task<long> CountAsync()
        {
            await using var connection = await Source.OpenConnectionAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT count(*) FROM "BillingAuditEvents" """;
            return (long)(await command.ExecuteScalarAsync(Token))!;
        }

        public async Task<BillingAuditRuntimeRecord?> ReadAsync(Guid id)
        {
            await using var connection = await Source.OpenConnectionAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "Id", "Timestamp", "EventType", "VirtualKeyId", "VirtualKeyGroupId", "Model", "RequestId",
                    "UsageJson", "CalculatedCost", "FailureReason", "ProviderType", "HttpStatusCode",
                    "MetadataJson", "RequestPath", "IsEstimated", "ToolUsageJson", "ToolUsageCost"
                FROM "BillingAuditEvents" WHERE "Id" = @id
                """;
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(Token);
            if (!await reader.ReadAsync(Token)) return null;
            var row = new BillingAuditRuntimeRecord
            {
                Id = reader.GetGuid(0), Timestamp = reader.GetDateTime(1), EventType = reader.GetInt32(2),
                VirtualKeyId = Number(reader, 3), VirtualKeyGroupId = Number(reader, 4), Model = Text(reader, 5),
                RequestId = Text(reader, 6), UsageJson = Text(reader, 7), CalculatedCost = Money(reader, 8),
                FailureReason = Text(reader, 9), ProviderType = Text(reader, 10), HttpStatusCode = Number(reader, 11),
                MetadataJson = Text(reader, 12), RequestPath = Text(reader, 13), IsEstimated = reader.GetBoolean(14),
                ToolUsageJson = Text(reader, 15), ToolUsageCost = Money(reader, 16)
            };
            Assert.False(await reader.ReadAsync(Token));
            return row;
        }

        public async Task WaitForLockWaitAsync(Task pending)
        {
            await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString)
                { Pooling = false, ApplicationName = "billing_audit_observer" }.ConnectionString);
            await observer.OpenAsync(Token);
            await using var command = observer.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @name
                    AND wait_event_type = 'Lock' AND cardinality(pg_blocking_pids(pid)) > 0)
                """;
            command.Parameters.AddWithValue("name", _schema);
            while (!(bool)(await command.ExecuteScalarAsync(Token))!)
            {
                Assert.False(pending.IsCompleted);
                await Task.Delay(20, Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            try { await EfSource.DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); }
            finally
            {
                try { await Source.DisposeAsync(); }
                catch (Exception exception) { failures.Add(exception); }
                finally
                {
                    try
                    {
                        _deadline.Dispose();
                        if (!_schema.StartsWith(Prefix, StringComparison.Ordinal) ||
                            !Guid.TryParseExact(_schema[Prefix.Length..], "N", out _))
                            throw new InvalidOperationException("Cleanup requires an owned billing audit schema.");
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        await using var connection = new NpgsqlConnection(_management);
                        await connection.OpenAsync(cleanup.Token);
                        await using var command = connection.CreateCommand();
                        command.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE";
                        await command.ExecuteNonQueryAsync(cleanup.Token);
                    }
                    catch (Exception exception) { failures.Add(exception); }
                }
            }
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1)
                throw new AggregateException("Owned billing audit fixture cleanup failed.", failures);
        }

        private static int? Number(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        private static decimal? Money(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
        private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
