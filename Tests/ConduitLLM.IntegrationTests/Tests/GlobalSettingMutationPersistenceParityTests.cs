#if !CONDUIT_NATIVE_AOT
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;
using Xunit.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed partial class GlobalSettingMutationPersistenceParityTests(
    PostgresLockTestContainerFixture fixture, ITestOutputHelper output)
{
    private static readonly DateTime OriginalCreated = new(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly DateTime OriginalUpdated = new(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(5);
    private static readonly string[] MutationKinds = ["create-default", "create-supplied", "update-existing"];

    private void WriteIdentityObservation(object value) => output.WriteLine(
        "GLOBAL_SETTING_IDENTITY_CONFORMANCE=" + JsonSerializer.Serialize(value));

    [Theory]
    [InlineData("ef-default")]
    [InlineData("ef-checking")]
    [InlineData("typed")]
    public Task PreCancelledMutationsPreserveEveryCallerFieldBeforeSql(string providerKind) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var expectedDatabase = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
            var initialPid = await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
            foreach (var operation in MutationKinds)
            {
                var entity = MutationEntity(operation);
                var before = CallerSnapshot.From(entity);
                using var caller = new CancellationTokenSource();
                caller.Cancel();
                var pending = InvokeMutationAsync(boundary.Repository, operation, entity, caller.Token);
                boundary.Track(pending);
                await AssertCallerCancellationAsync(pending, caller.Token);
                boundary.Observed(pending);
                Assert.Equal(before, CallerSnapshot.From(entity));
                Assert.Equal(0, boundary.Counter.Started);
                Assert.Equal(expectedDatabase, await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token));
                Assert.Equal(initialPid, await BoundaryPoolUsableAsync(boundary.Source, boundary.Token));
            }
            output.WriteLine("GLOBAL_SETTING_MUTATION_PRE_CANCEL=" + JsonSerializer.Serialize(new
                { providerKind, operations = MutationKinds.Length, settingSqlStarted = 0,
                    fullCallerUnchanged = true, databaseUnchanged = true, samePhysicalBackend = true }));
        });

    [Theory]
    [InlineData("ef-default")]
    [InlineData("typed")]
    public Task OccupiedSingleSlotCancellationRetainsTheAlreadyStampedCaller(string providerKind) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var expectedDatabase = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
            foreach (var operation in MutationKinds)
            {
                NpgsqlConnection? held = null;
                Task<object?>? pending = null;
                var initialPid = 0;
                using var caller = new CancellationTokenSource();
                await IntegrationProofCleanup.RunAsync(async () =>
                {
                    held = await boundary.Source.OpenConnectionAsync(boundary.Token);
                    initialPid = held.ProcessID;
                    var entity = MutationEntity(operation);
                    var before = CallerSnapshot.From(entity);
                    pending = InvokeMutationAsync(boundary.Repository, operation, entity, caller.Token);
                    boundary.Track(pending);
                    Assert.False(pending.IsCompleted);
                    Assert.Equal(0, boundary.Counter.Started);
                    var stamped = AssertCallerWasStamped(operation, entity, before);
                    caller.Cancel();
                    await AssertCallerCancellationAsync(pending, caller.Token);
                    boundary.Observed(pending);
                    Assert.Equal(stamped, CallerSnapshot.From(entity));
                    Assert.Equal(0, boundary.Counter.Started);
                    Assert.Equal(expectedDatabase, await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token));
                    // The holder still owns the exact sole slot while cancellation is proved.
                    Assert.Equal(initialPid, held.ProcessID);
                }, async () => await IntegrationProofCleanup.DrainIndependentlyAsync(
                [
                    async token => { caller.Cancel(); if (pending is not null) await boundary.DrainAsync(pending, token); },
                    async token => { if (held is not null) await held.DisposeAsync().AsTask().WaitAsync(token); },
                    async token => { if (pending is not null) await boundary.DrainAsync(pending, token); }
                ], CleanupLimit, "The occupied setting slot and caller were not independently joined."),
                    "The occupied setting cancellation failed and its cleanup also failed.");
                Assert.Equal(initialPid, await BoundaryPoolUsableAsync(boundary.Source, boundary.Token));
            }
            output.WriteLine("GLOBAL_SETTING_MUTATION_POOL_CANCEL=" + JsonSerializer.Serialize(new
                { providerKind, operations = MutationKinds.Length, maxPoolSize = 1,
                    timestampsObservedBeforeCancel = true, retainedExactly = true, settingSqlStarted = 0 }));
        });

    [Theory]
    [InlineData("ef-default")]
    [InlineData("typed")]
    public Task ObservedWriteLockCancellationRetainsCallerAndReusesTheSameBackendBeforeUnlock(string providerKind) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var expectedDatabase = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
            foreach (var operation in MutationKinds)
            {
                var readerPid = await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
                NpgsqlConnection? blocker = null;
                NpgsqlTransaction? transaction = null;
                Task<object?>? pending = null;
                using var caller = new CancellationTokenSource();
                await IntegrationProofCleanup.RunAsync(async () =>
                {
                    blocker = new NpgsqlConnection(BoundaryManagementConnection());
                    await blocker.OpenAsync(boundary.Token);
                    transaction = await blocker.BeginTransactionAsync(boundary.Token);
                    await using (var command = blocker.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = $"LOCK TABLE \"{boundary.Schema}\".\"GlobalSettings\" IN ACCESS EXCLUSIVE MODE";
                        await command.ExecuteNonQueryAsync(boundary.Token);
                    }
                    var entity = MutationEntity(operation);
                    var before = CallerSnapshot.From(entity);
                    pending = InvokeMutationAsync(boundary.Repository, operation, entity, caller.Token);
                    boundary.Track(pending);
                    var stamped = AssertCallerWasStamped(operation, entity, before);
                    await WaitForBoundaryWriteLockAsync(boundary, readerPid, blocker.ProcessID, pending);
                    Assert.Equal(1, boundary.Counter.Started);
                    caller.Cancel();
                    await AssertCallerCancellationAsync(pending, caller.Token);
                    boundary.Observed(pending);
                    Assert.Equal(stamped, CallerSnapshot.From(entity));
                    Assert.Equal(readerPid, await BoundaryPoolUsableAsync(boundary.Source, boundary.Token));
                    // The blocker transaction is deliberately still active during SELECT 1.
                    Assert.NotNull(transaction.Connection);
                }, async () => await IntegrationProofCleanup.DrainIndependentlyAsync(
                [
                    async token => { caller.Cancel(); if (pending is not null) await boundary.DrainAsync(pending, token); },
                    async token => { if (transaction is not null) await transaction.RollbackAsync(token); },
                    async token => { if (blocker is not null) await blocker.DisposeAsync().AsTask().WaitAsync(token); },
                    async token => { if (pending is not null) await boundary.DrainAsync(pending, token); },
                    async token => { if (transaction is not null) await transaction.DisposeAsync().AsTask().WaitAsync(token); }
                ], CleanupLimit, "The setting write, blocker and transaction were not independently joined."),
                    "The setting SQL cancellation failed and its cleanup also failed.");
                Assert.Equal(expectedDatabase, await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token));
                await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
                boundary.Counter.Reset();
            }
            output.WriteLine("GLOBAL_SETTING_MUTATION_SQL_CANCEL=" + JsonSerializer.Serialize(new
                { providerKind, operations = MutationKinds.Length, exactWaiterBlockerAndRelation = true,
                    callerTokenMatched = true, retainedExactly = true, selectOneBeforeUnlockSamePid = true }));
        });

    [Theory]
    [InlineData("ef-default")]
    [InlineData("typed")]
    public Task RealWriteFailuresRetainCallerAndSuccessPersistsAllSixFields(string providerKind) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var expectedDatabase = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
            foreach (var (operation, entity, expectedState) in new[]
            {
                ("create-default", new GlobalSetting { Key = "seed", Value = "duplicate",
                    CreatedAt = default, UpdatedAt = OriginalUpdated }, "23505"),
                ("create-supplied", new GlobalSetting { Key = "oversized-create", Value = new string('v', 2001),
                    CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated }, "22001"),
                ("update-existing", new GlobalSetting { Id = 100, Key = "collision", Value = "duplicate",
                    Description = "changed", CreatedAt = OriginalCreated.AddDays(-1), UpdatedAt = OriginalUpdated }, "23505"),
                ("update-existing", new GlobalSetting { Id = 100, Key = "seed", Value = new string('v', 2001),
                    Description = null, CreatedAt = OriginalCreated.AddDays(-1), UpdatedAt = OriginalUpdated }, "22001")
            })
            {
                var before = CallerSnapshot.From(entity);
                var pending = InvokeMutationAsync(boundary.Repository, operation, entity, boundary.Token);
                boundary.Track(pending);
                var stamped = AssertCallerWasStamped(operation, entity, before);
                var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                boundary.Observed(pending);
                Assert.NotNull(error);
                Assert.IsNotType<TimeoutException>(error);
                Assert.Equal(expectedState, FindBoundaryPostgresException(error!)?.SqlState);
                Assert.Equal(stamped, CallerSnapshot.From(entity));
                Assert.Equal(expectedDatabase, await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token));
                Assert.True(boundary.Counter.Started > 0);
                await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
                boundary.Counter.Reset();
            }

            foreach (var operation in MutationKinds)
            {
                var entity = MutationEntity(operation);
                var before = CallerSnapshot.From(entity);
                var pending = InvokeMutationAsync(boundary.Repository, operation, entity, boundary.Token);
                boundary.Track(pending);
                var stamped = AssertCallerWasStamped(operation, entity, before, generatedIdMayBeSet: true);
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                boundary.Observed(pending);
                var after = CallerSnapshot.From(entity);
                Assert.Equal(stamped with { Id = after.Id }, after);
                if (operation.StartsWith("create", StringComparison.Ordinal))
                {
                    Assert.True(after.Id > 0);
                    Assert.Equal(after.Id, Assert.IsType<int>(result));
                }
                else Assert.True(Assert.IsType<bool>(result));
                var expectedRow = after.Wire();
                var actualRows = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
                Assert.Equal(expectedRow, Assert.Single(actualRows, row => row.Id == after.Id));
                var unaffected = expectedDatabase.Where(row => row.Id != after.Id).ToArray();
                Assert.Equal(unaffected, actualRows.Where(row => row.Id != after.Id
                    && expectedDatabase.Any(expected => expected.Id == row.Id)).ToArray());
                expectedDatabase = actualRows;
                Assert.True(boundary.Counter.Started > 0);
                await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
                boundary.Counter.Reset();
            }
            output.WriteLine("GLOBAL_SETTING_MUTATION_WRITE_OUTCOMES=" + JsonSerializer.Serialize(new
                { providerKind, failures = 4, successfulWrites = 3, sqlStates = new[] { "23505", "22001" },
                    exactCallerRetained = true, persistedAllSixFieldsAtWirePrecision = true }));
        });

    private async Task WithBoundaryAsync(string providerKind, Func<MutationBoundary, Task> work)
    {
        var schema = $"gs_mutation_{Guid.NewGuid():N}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var counter = new BoundarySqlCounter();
        NpgsqlDataSource? source = null;
        ServiceProvider? provider = null;
        MutationBoundary? boundary = null;
        var schemaAttempted = false;
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            schemaAttempted = true;
            await CreateBoundarySchemaAsync(schema, deadline.Token);
            var rawConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                SearchPath = schema, ApplicationName = schema, Pooling = true,
                MaxPoolSize = 1, MinPoolSize = 0, Multiplexing = false, Timeout = 5, CommandTimeout = 15
            }.ConnectionString;
            source = new NpgsqlDataSourceBuilder(rawConnection).UseLoggerFactory(counter)
                .EnableParameterLogging(false).Build();
            IGlobalSettingRepository repository;
            if (providerKind == "typed") repository = new NpgsqlGlobalSettingRepository(source);
            else if (providerKind == "ef-checking") repository = new GlobalSettingRepository(
                new CheckingBoundaryFactory(source), NullLogger<GlobalSettingRepository>.Instance);
            else
            {
                Assert.Equal("ef-default", providerKind);
                var services = new ServiceCollection();
                services.AddDbContextFactory<ConduitDbContext>(options => options.UseNpgsql(source));
                provider = services.BuildServiceProvider(new ServiceProviderOptions
                    { ValidateOnBuild = true, ValidateScopes = true });
                var actualFactory = provider.GetRequiredService<IDbContextFactory<ConduitDbContext>>();
                Assert.StartsWith("Microsoft.EntityFrameworkCore.Internal.DbContextFactory`1",
                    actualFactory.GetType().FullName!, StringComparison.Ordinal);
                repository = new GlobalSettingRepository(actualFactory, NullLogger<GlobalSettingRepository>.Instance);
            }
            boundary = new MutationBoundary(schema, source, repository, counter, deadline.Token);
            await work(boundary);
        }, async () => await IntegrationProofCleanup.DrainIndependentlyAsync(
        [
            async token => { deadline.Cancel(); if (boundary is not null) await boundary.DrainAllAsync(token); },
            async token => { if (provider is not null) await provider.DisposeAsync().AsTask().WaitAsync(token); },
            async token => { if (source is not null) await source.DisposeAsync().AsTask().WaitAsync(token); },
            async token => { if (schemaAttempted) await DropBoundarySchemaAsync(schema, token); }
        ], CleanupLimit, "The setting caller fixture was not independently disposed and removed."),
            "The setting caller proof failed and its owned cleanup also failed.");
    }

    private static GlobalSetting MutationEntity(string operation) => operation switch
    {
        "create-default" => new GlobalSetting { Key = "new-default", Value = "new-value",
            Description = "new-description", CreatedAt = default, UpdatedAt = OriginalUpdated },
        "create-supplied" => new GlobalSetting { Key = "new-supplied", Value = "new-value",
            Description = null, CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated },
        "update-existing" => new GlobalSetting { Id = 100, Key = "seed", Value = "after",
            Description = "changed-description", CreatedAt = OriginalCreated.AddDays(-1), UpdatedAt = OriginalUpdated },
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static async Task<object?> InvokeMutationAsync(IGlobalSettingRepository repository, string operation,
        GlobalSetting entity, CancellationToken token) => operation.StartsWith("create", StringComparison.Ordinal)
        ? await repository.CreateAsync(entity, token) : await repository.UpdateAsync(entity, token);

    private static CallerSnapshot AssertCallerWasStamped(string operation, GlobalSetting entity, CallerSnapshot before,
        bool generatedIdMayBeSet = false)
    {
        var stamped = CallerSnapshot.From(entity);
        if (!generatedIdMayBeSet || !operation.StartsWith("create", StringComparison.Ordinal))
            Assert.Equal(before.Id, stamped.Id);
        Assert.Equal(before.Key, stamped.Key);
        Assert.Equal(before.Value, stamped.Value);
        Assert.Equal(before.Description, stamped.Description);
        Assert.Equal(DateTimeKind.Utc, stamped.UpdatedAt.Kind);
        Assert.NotEqual(before.UpdatedAt, stamped.UpdatedAt);
        if (operation == "create-default") Assert.Equal(stamped.UpdatedAt, stamped.CreatedAt);
        else Assert.Equal(before.CreatedAt, stamped.CreatedAt);
        return stamped;
    }

    private static async Task AssertCallerCancellationAsync(Task pending, CancellationToken token)
    {
        var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsNotType<TimeoutException>(error);
        var cancellation = Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(token, cancellation.CancellationToken);
        Assert.True(pending.IsCanceled);
    }

    private static async Task CalibrateBoundaryCounterAsync(MutationBoundary boundary)
    {
        Assert.Equal(0, boundary.Counter.Started);
        var row = await boundary.Repository.GetByKeyAsync("seed", boundary.Token);
        Assert.NotNull(row);
        Assert.Equal(100, row.Id);
        Assert.Equal("before", row.Value);
        Assert.Equal(1, boundary.Counter.Started);
        boundary.Counter.Reset();
        Assert.Equal(0, boundary.Counter.Started);
    }

    private async Task WaitForBoundaryWriteLockAsync(MutationBoundary boundary, int readerPid, int blockerPid, Task pending)
    {
        using var observe = CancellationTokenSource.CreateLinkedTokenSource(boundary.Token);
        observe.CancelAfter(TimeSpan.FromSeconds(10));
        await using var observer = new NpgsqlConnection(BoundaryManagementConnection());
        await observer.OpenAsync(observe.Token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
              SELECT 1 FROM pg_catalog.pg_stat_activity AS a
              WHERE a.pid=@reader AND a.application_name=@application AND a.datname=current_database() AND a.state='active'
                AND a.wait_event_type='Lock' AND @blocker=ANY(pg_catalog.pg_blocking_pids(a.pid))
                AND EXISTS (SELECT 1 FROM pg_catalog.pg_locks AS waiting
                  JOIN pg_catalog.pg_class AS c ON c.oid=waiting.relation
                  JOIN pg_catalog.pg_namespace AS n ON n.oid=c.relnamespace
                  WHERE waiting.pid=a.pid AND NOT waiting.granted AND waiting.mode='RowExclusiveLock'
                    AND n.nspname=@schema AND c.relname='GlobalSettings')
                AND EXISTS (SELECT 1 FROM pg_catalog.pg_locks AS held
                  JOIN pg_catalog.pg_class AS c ON c.oid=held.relation
                  JOIN pg_catalog.pg_namespace AS n ON n.oid=c.relnamespace
                  WHERE held.pid=@blocker AND held.granted AND held.mode='AccessExclusiveLock'
                    AND n.nspname=@schema AND c.relname='GlobalSettings'))
            """;
        command.Parameters.AddWithValue("reader", readerPid);
        command.Parameters.AddWithValue("blocker", blockerPid);
        command.Parameters.AddWithValue("application", boundary.Schema);
        command.Parameters.AddWithValue("schema", boundary.Schema);
        while (true)
        {
            Assert.False(pending.IsCompleted, "The write completed before its exact server lock was observed.");
            if (Assert.IsType<bool>(await command.ExecuteScalarAsync(observe.Token))) return;
            await Task.Delay(TimeSpan.FromMilliseconds(20), observe.Token);
        }
    }

    private string BoundaryManagementConnection() => new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
    {
        Pooling = false, Timeout = 5, CommandTimeout = 5, ApplicationName = "gs_mutation_management"
    }.ConnectionString;

    private async Task CreateBoundarySchemaAsync(string schema, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(BoundaryManagementConnection());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."GlobalSettings" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "Key" varchar(100) NOT NULL UNIQUE,
                "Value" varchar(2000) NOT NULL,
                "Description" varchar(500),
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL);
            INSERT INTO "{schema}"."GlobalSettings"
                ("Id", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
            VALUES (100, 'seed', 'before', 'before-description', @created, @updated),
                   (101, 'collision', 'unrelated', NULL, @created, @updated);
            """;
        command.Parameters.AddWithValue("created", OriginalCreated);
        command.Parameters.AddWithValue("updated", OriginalUpdated);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<CallerSnapshot[]> ReadBoundaryRowsAsync(string schema, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(BoundaryManagementConnection());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT "Id", "Key", "Value", "Description", "CreatedAt", "UpdatedAt"
            FROM "{schema}"."GlobalSettings" ORDER BY "Id"
            """;
        var rows = new List<CallerSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetInt32(0), reader.GetString(1),
            reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDateTime(4), reader.GetDateTime(5)));
        return rows.ToArray();
    }

    private async Task DropBoundarySchemaAsync(string schema, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(BoundaryManagementConnection());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT COUNT(*) FROM pg_catalog.pg_namespace WHERE nspname=@schema";
        command.Parameters.AddWithValue("schema", schema);
        Assert.Equal(0L, await command.ExecuteScalarAsync(token));
        output.WriteLine("GLOBAL_SETTING_MUTATION_SCHEMA_ABSENT=" + schema);
    }

    private static async Task<int> BoundaryPoolUsableAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync(token));
        return connection.ProcessID;
    }

    private static PostgresException? FindBoundaryPostgresException(Exception error) => error switch
    {
        PostgresException postgres => postgres,
        AggregateException aggregate => aggregate.InnerExceptions.Select(FindBoundaryPostgresException).FirstOrDefault(item => item is not null),
        _ => error.InnerException is null ? null : FindBoundaryPostgresException(error.InnerException)
    };

    private sealed record CallerSnapshot(int Id, string Key, string Value, string? Description,
        DateTime CreatedAt, DateTime UpdatedAt)
    {
        public static CallerSnapshot From(GlobalSetting entity) => new(entity.Id, entity.Key, entity.Value,
            entity.Description, entity.CreatedAt, entity.UpdatedAt);
        public CallerSnapshot Wire() => this with
        {
            CreatedAt = new DateTime(CreatedAt.Ticks - CreatedAt.Ticks % 10, DateTimeKind.Utc),
            UpdatedAt = new DateTime(UpdatedAt.Ticks - UpdatedAt.Ticks % 10, DateTimeKind.Utc)
        };
    }

    private sealed class MutationBoundary(string schema, NpgsqlDataSource source, IGlobalSettingRepository repository,
        BoundarySqlCounter counter, CancellationToken token)
    {
        private readonly List<Task> _pending = [];
        private readonly HashSet<Task> _observed = [];
        public string Schema { get; } = schema;
        public NpgsqlDataSource Source { get; } = source;
        public IGlobalSettingRepository Repository { get; } = repository;
        public BoundarySqlCounter Counter { get; } = counter;
        public CancellationToken Token { get; } = token;
        public void Track(Task pending) => _pending.Add(pending);
        public void Observed(Task pending) => _observed.Add(pending);
        public async Task DrainAsync(Task pending, CancellationToken cleanup)
        {
            if (_observed.Contains(pending) && pending.IsCompleted) return;
            try { await pending.WaitAsync(cleanup); }
            catch (OperationCanceledException) when (pending.IsCanceled) { }
        }
        public async Task DrainAllAsync(CancellationToken cleanup)
        {
            foreach (var pending in _pending) await DrainAsync(pending, cleanup);
        }
    }

    private sealed class CheckingBoundaryFactory(NpgsqlDataSource source) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(source).Options);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class BoundarySqlCounter : ILoggerFactory, ILogger
    {
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public void Reset() => Interlocked.Exchange(ref _started, 0);
        public ILogger CreateLogger(string categoryName) => categoryName == "Npgsql.Command" ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != NpgsqlEventId.ExecutingCommand || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToArray();
            if (values.Any(item => item.Key == "Parameters")) throw new InvalidOperationException("Mutation proof must not log SQL parameter values.");
            if (values.FirstOrDefault(item => item.Key == "CommandText").Value is string sql
                && sql.Contains("\"GlobalSettings\"", StringComparison.Ordinal)) Interlocked.Increment(ref _started);
        }
        public void Dispose() { }
    }
}
#endif
