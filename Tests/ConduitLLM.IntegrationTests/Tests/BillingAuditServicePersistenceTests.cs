using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Services;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class BillingAuditServicePersistenceTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public async Task ThresholdDatabaseFailureIsSwallowedAndTheSameHundredQueuedEntitiesRecover()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var provider = new UnavailableProvider();
        using var service = Create(provider, new NpgsqlBillingAuditRuntimeWriter(database.Source));
        var rows = Enumerable.Range(0, 100).Select(index => Event(index.ToString())).ToArray();
        rows[^1].UsageJson = "invalid last row";
        foreach (var row in rows.Take(99)) await service.LogBillingEventAsync(row);
        Assert.Empty(await database.ReadRowsAsync());
        // The asynchronous producer deliberately swallows the threshold flush's
        // SQL failure, retaining the same entity references for a later flush.
        await service.LogBillingEventAsync(rows[^1]);
        Assert.Empty(await database.ReadRowsAsync());
        Assert.All(rows, row => { Assert.Equal(Guid.Empty, row.Id); Assert.Equal(default, row.Timestamp); });

        rows[^1].UsageJson = """{"recovered":true}""";
        await service.FlushEventsAsync();
        await AssertCommittedAsync(database, rows);
        await service.StopAsync(database.Token);
        Assert.Equal(100, (await database.ReadRowsAsync()).Count);
        Assert.Equal(0, provider.Accesses);
    }

    [Fact]
    public async Task ShutdownTailDatabaseFailureIsRethrownAndItsRequeuedEntitiesRetryOnTheSameInstance()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var provider = new UnavailableProvider();
        using var service = Create(provider, new NpgsqlBillingAuditRuntimeWriter(database.Source));
        var rows = Enumerable.Range(0, 7).Select(index => Event("tail-" + index)).ToArray();
        rows[^1].MetadataJson = "invalid tail metadata";
        foreach (var row in rows) await service.LogBillingEventAsync(row);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => service.StopAsync(database.Token));
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, failure.SqlState);
        Assert.Empty(await database.ReadRowsAsync());
        Assert.All(rows, row => { Assert.Equal(Guid.Empty, row.Id); Assert.Equal(default, row.Timestamp); });

        rows[^1].MetadataJson = "null";
        await service.StopAsync(database.Token);
        await AssertCommittedAsync(database, rows);
        await service.StopAsync(database.Token);
        Assert.Equal(7, (await database.ReadRowsAsync()).Count);
        Assert.Equal(0, provider.Accesses);
    }

    [Fact]
    public async Task ObservedSqlLockCancellationRequeuesTheShutdownTailAndReleasesItsConnection()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var provider = new UnavailableProvider();
        using var service = Create(provider, new NpgsqlBillingAuditRuntimeWriter(database.Source));
        var row = Event("cancelled shutdown");
        await service.LogBillingEventAsync(row);
        await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Pooling = false, ApplicationName = "billing_service_blocker" }.ConnectionString);
        await blocker.OpenAsync(database.Token);
        await using var transaction = await blocker.BeginTransactionAsync(database.Token);
        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = """LOCK TABLE "BillingAuditEvents" IN ACCESS EXCLUSIVE MODE""";
            await command.ExecuteNonQueryAsync(database.Token);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(database.Token);
        var pending = service.StopAsync(cancellation.Token);
        try
        {
            await database.WaitForLockWaitAsync(pending);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        await transaction.RollbackAsync(database.Token);
        Assert.Equal(Guid.Empty, row.Id);
        Assert.Equal(default, row.Timestamp);
        Assert.Empty(await database.ReadRowsAsync());
        await service.StopAsync(database.Token);
        await AssertCommittedAsync(database, [row]);
        Assert.Equal(0, provider.Accesses);
    }

    [Fact]
    public async Task TwoIndependentRuntimeServicesTimerFlushBothSubthresholdQueues()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        await using var secondSource = NpgsqlDataSource.Create(database.ConnectionString);
        var firstProvider = new UnavailableProvider();
        var secondProvider = new UnavailableProvider();
        var firstWriter = new ObservingWriter(new NpgsqlBillingAuditRuntimeWriter(database.Source));
        var secondWriter = new ObservingWriter(new NpgsqlBillingAuditRuntimeWriter(secondSource));
        using var first = Create(firstProvider, firstWriter);
        using var second = Create(secondProvider, secondWriter);
        var firstRow = Event("first host timer");
        var secondRow = Event("second host timer");
        await first.LogBillingEventAsync(firstRow);
        await second.LogBillingEventAsync(secondRow);
        await first.StartAsync(database.Token);
        await second.StartAsync(database.Token);
        try
        {
            await Task.WhenAll(firstWriter.Committed.Task, secondWriter.Committed.Task)
                .WaitAsync(TimeSpan.FromSeconds(20), database.Token);
            // A completed writer precedes the service's validated copyback.
            // Stop joins that timer flush before these final entity assertions.
            await first.StopAsync(database.Token);
            await second.StopAsync(database.Token);
            await AssertCommittedAsync(database, [firstRow, secondRow]);
            Assert.False(firstWriter.Token.CanBeCanceled);
            Assert.False(secondWriter.Token.CanBeCanceled);
            Assert.Equal(1, firstWriter.Calls);
            Assert.Equal(1, secondWriter.Calls);
            Assert.Equal(0, firstProvider.Accesses);
            Assert.Equal(0, secondProvider.Accesses);
        }
        finally
        {
            // Attempt both drains independently if an assertion or timer fails.
            var failures = new List<Exception>();
            try { await first.StopAsync(database.Token); }
            catch (Exception exception) { failures.Add(exception); }
            try { await second.StopAsync(database.Token); }
            catch (Exception exception) { failures.Add(exception); }
            if (failures.Count > 0) throw new AggregateException("Owned billing timer services failed to drain.", failures);
        }
    }

    private static BillingAuditService Create(IServiceProvider provider, IBillingAuditRuntimeWriter writer) =>
        BillingAuditService.CreateWithRuntimePersistence(provider, writer, NullLogger<BillingAuditService>.Instance);

    private static BillingAuditEvent Event(string requestId) => new()
    {
        Id = Guid.Empty, Timestamp = default, EventType = BillingAuditEventType.UsageTracked,
        VirtualKeyId = 41, VirtualKeyGroupId = 7, RequestId = requestId, Model = "service lifecycle 雪",
        UsageJson = """{"unicode":"café 雪 🔎","count":1}""", MetadataJson = "null",
        CalculatedCost = 0.12500001m, ProviderType = "OpenAI", HttpStatusCode = 200,
        RequestPath = "/v1/chat/completions", ToolUsageJson = "[]", ToolUsageCost = 0m
    };

    private static async Task AssertCommittedAsync(OwnedDatabase database, IReadOnlyList<BillingAuditEvent> expected)
    {
        var rows = await database.ReadRowsAsync();
        Assert.Equal(expected.Count, rows.Count);
        Assert.Equal(expected.Count, expected.Select(row => row.Id).Distinct().Count());
        foreach (var entity in expected)
        {
            Assert.NotEqual(Guid.Empty, entity.Id);
            Assert.Equal(DateTimeKind.Utc, entity.Timestamp.Kind);
            Assert.NotEqual(default, entity.Timestamp);
            var row = Assert.Single(rows, row => row.RequestId == entity.RequestId);
            Assert.Equal(entity.Id, row.Id);
            Assert.Equal(entity.Timestamp, row.Timestamp);
            using var actualJson = JsonDocument.Parse(row.UsageJson);
            using var expectedJson = JsonDocument.Parse(entity.UsageJson!);
            Assert.True(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement));
        }
    }

    private sealed class UnavailableProvider : IServiceProvider
    {
        private int _accesses;
        public int Accesses => Volatile.Read(ref _accesses);
        public object? GetService(Type serviceType)
        {
            Interlocked.Increment(ref _accesses);
            throw new InvalidOperationException("Native billing service attempted an EF scope.");
        }
    }

    private sealed class ObservingWriter(IBillingAuditRuntimeWriter inner) : IBillingAuditRuntimeWriter
    {
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public CancellationToken Token { get; private set; }
        public async Task<IReadOnlyList<BillingAuditRuntimeRecord>> WriteBatchAsync(
            IReadOnlyList<BillingAuditRuntimeRecord> records, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Token = cancellationToken;
            var committed = await inner.WriteBatchAsync(records, cancellationToken);
            Committed.TrySetResult();
            return committed;
        }
    }

    private sealed record StoredRow(Guid Id, string RequestId, DateTime Timestamp, string UsageJson);
    private sealed class OwnedDatabase : IAsyncDisposable
    {
        private const string Prefix = "billing_service_lifecycle_";
        private readonly string _management;
        private readonly string _schema = Prefix + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
        public string ConnectionString { get; }
        public NpgsqlDataSource Source { get; }
        public NpgsqlDataSource EfSource { get; }
        public CancellationToken Token => _deadline.Token;

        private OwnedDatabase(string management)
        {
            _management = management;
            ConnectionString = new NpgsqlConnectionStringBuilder(management)
                { SearchPath = _schema, ApplicationName = _schema, MaxPoolSize = 1 }.ConnectionString;
            Source = NpgsqlDataSource.Create(ConnectionString);
            try { EfSource = NpgsqlDataSource.Create(ConnectionString); }
            catch (Exception primary)
            {
                try { Source.Dispose(); }
                catch (Exception cleanup)
                {
                    throw new AggregateException("Owned billing service data source initialization and cleanup failed.", primary, cleanup);
                }
                finally { _deadline.Dispose(); }
                throw;
            }
        }

        public static async Task<OwnedDatabase> CreateAsync(string management)
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
                await using var context = new ConduitDbContext(new DbContextOptionsBuilder<ConduitDbContext>()
                    .UseNpgsql(database.EfSource).Options);
                await context.Database.OpenConnectionAsync(database.Token);
                await using (var command = context.Database.GetDbConnection().CreateCommand())
                {
                    command.CommandText = context.Database.GenerateCreateScript();
                    await command.ExecuteNonQueryAsync(database.Token);
                }
                context.AddRange(new VirtualKeyGroup { Id = 7, GroupName = "billing lifecycle group" },
                    new VirtualKey { Id = 41, KeyName = "billing lifecycle key", KeyHash = "billing-lifecycle-key-hash", VirtualKeyGroupId = 7 });
                await context.SaveChangesAsync(database.Token);
                return database;
            }
            catch (Exception primary)
            {
                try { await database.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException("Billing service setup and cleanup failed.", primary, cleanup); }
                throw;
            }
        }

        public async Task<IReadOnlyList<StoredRow>> ReadRowsAsync()
        {
            await using var connection = await Source.OpenConnectionAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "Id", "RequestId", "Timestamp", "UsageJson" FROM "BillingAuditEvents" ORDER BY "RequestId"
                """;
            await using var reader = await command.ExecuteReaderAsync(Token);
            var result = new List<StoredRow>();
            while (await reader.ReadAsync(Token))
                result.Add(new StoredRow(reader.GetGuid(0), reader.GetString(1), reader.GetDateTime(2), reader.GetString(3)));
            return result;
        }

        public async Task WaitForLockWaitAsync(Task pending)
        {
            await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString)
                { Pooling = false, ApplicationName = "billing_service_observer" }.ConnectionString);
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
                            throw new InvalidOperationException("Cleanup requires an owned billing service schema.");
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
                throw new AggregateException("Owned billing service fixture cleanup failed.", failures);
        }
    }
}
