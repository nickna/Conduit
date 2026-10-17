#if !CONDUIT_NATIVE_AOT
using System.Data;
using System.Diagnostics;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;
using NpgsqlTypes;

using Xunit;
using Xunit.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

// Real PostgreSQL contract for completed read -> client UTC -> original-identity
// write. The rare case where every field, including the newly sampled UTC, is
// identical to the read snapshot is source-qualified; no clock injection is used.
[Collection("Postgres advisory locks")]
public sealed class GlobalSettingUpsertPersistenceParityTests(
    PostgresLockTestContainerFixture fixture, ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    private static readonly DateTime Created = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Updated = new(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly DateTime ConcurrentCreated = new(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly DateTime ConcurrentUpdated = new(2023, 4, 5, 6, 7, 8, DateTimeKind.Utc);
    private static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentWinnerRaceUsesPlainInsertAndPreservesTheCommittedWinner(bool typed)
    {
        await InUpsertCaseAsync(typed, "absent-winner", async owned =>
        {
            Assert.Empty(await owned.ReadRowsAsync(owned.Token));
            var pending = owned.Start("requested", "requested-description");
            var write = await owned.Gate.WriteEntered.WaitAsync(owned.Token);
            AssertSharedWriteShape(write, "INSERT");
            Assert.Empty(await owned.ReadRowsAsync(owned.Token));
            var winner = new Row(200, "target", "winner", "winner-description", ConcurrentCreated, ConcurrentUpdated);
            await owned.InsertAsync(winner, owned.Token);
            Assert.Equal(winner, Assert.Single(await owned.ReadRowsAsync(owned.Token)));
            var beforeSend = await owned.ReadBackendAsync(write.Pid, owned.Token);
            Assert.True(beforeSend.State is "idle" or "idle in transaction");
            owned.Gate.Release();
            var failure = await ObserveAsync(pending, owned.Token);
            var actual = Assert.Single(await owned.ReadRowsAsync(owned.Token));
            if (typed) Assert.IsType<PostgresException>(failure);
            else Assert.IsType<DbUpdateException>(failure);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, FindPostgres(failure!)?.SqlState);
            Assert.Equal(winner, actual);
            Assert.Equal(2, owned.Gate.CommandsSent);
            await owned.AssertRecoveryAsync(write.Pid);
            owned.Report(new { scenario = "absent-winner", typed, write, beforeSend,
                exceptionType = failure?.GetType().FullName, sqlState = FindPostgres(failure)?.SqlState,
                returned = failure is null ? await pending : (bool?)null, actual });
        });
    }

    [Theory]
    [InlineData(false, "same", "same")]
    [InlineData(false, "changed", "same")]
    [InlineData(false, "changed", "null")]
    [InlineData(false, "same", "changed")]
    [InlineData(false, "same", "empty")]
    [InlineData(true, "same", "same")]
    [InlineData(true, "changed", "same")]
    [InlineData(true, "changed", "null")]
    [InlineData(true, "same", "changed")]
    [InlineData(true, "same", "empty")]
    public async Task ConcurrentFieldWriterPreservesColumnsUnchangedFromTheReadSnapshot(
        bool typed, string valueMode, string descriptionMode)
    {
        await InUpsertCaseAsync(typed, "concurrent-fields", async owned =>
        {
            var original = new Row(100, "target", "original", "original-description", Created, Updated);
            await owned.InsertAsync(original, owned.Token);
            var requestedValue = valueMode == "same" ? original.Value : "requested";
            var requestedDescription = descriptionMode switch
            {
                "same" => original.Description,
                "changed" => "requested-description",
                "empty" => string.Empty,
                "null" => null,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptionMode))
            };
            var pending = owned.Start(requestedValue, requestedDescription);
            var write = await owned.Gate.WriteEntered.WaitAsync(owned.Token);
            AssertSharedWriteShape(write, "UPDATE");
            Assert.Equal(100, Assert.Single(write.IntegerParameters));
            var concurrent = original with { Value = "concurrent", Description = "concurrent-description",
                CreatedAt = ConcurrentCreated, UpdatedAt = ConcurrentUpdated };
            await owned.ReplaceFieldsAsync(concurrent, owned.Token);
            Assert.Equal(concurrent, Assert.Single(await owned.ReadRowsAsync(owned.Token)));
            owned.Gate.Release();
            Assert.True(await pending.WaitAsync(owned.Token));
            var actual = Assert.Single(await owned.ReadRowsAsync(owned.Token));
            var expectedValue = valueMode != "same" ? requestedValue : concurrent.Value;
            var expectedDescription = requestedDescription is null || descriptionMode == "same"
                ? concurrent.Description : requestedDescription;
            Assert.Equal(concurrent with { Value = expectedValue, Description = expectedDescription,
                UpdatedAt = actual.UpdatedAt }, actual);
            Assert.Equal(WireUtc(Assert.Single(write.UtcParameters.Distinct())), actual.UpdatedAt);
            Assert.NotEqual(concurrent.UpdatedAt, actual.UpdatedAt);
            Assert.Equal(2, owned.Gate.CommandsSent);
            await owned.AssertRecoveryAsync(write.Pid);
            owned.Report(new { scenario = "concurrent-fields", typed, valueMode, descriptionMode, write, actual });
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisappearedOriginalNeverRetargetsAReplacementWithTheSameKey(bool typed, bool reinsert)
    {
        await InUpsertCaseAsync(typed, "disappeared-original", async owned =>
        {
            await owned.InsertAsync(new Row(100, "target", "original", "original-description", Created, Updated), owned.Token);
            var pending = owned.Start("requested", "requested-description");
            var write = await owned.Gate.WriteEntered.WaitAsync(owned.Token);
            AssertSharedWriteShape(write, "UPDATE");
            Assert.Equal(100, Assert.Single(write.IntegerParameters));
            var replacement = new Row(200, "target", "replacement", "replacement-description", ConcurrentCreated, ConcurrentUpdated);
            await owned.DeleteOriginalAsync(reinsert ? replacement : null, owned.Token);
            Assert.Equal(reinsert ? new[] { replacement } : [], await owned.ReadRowsAsync(owned.Token));
            owned.Gate.Release();
            var failure = await ObserveAsync(pending, owned.Token);
            var actual = await owned.ReadRowsAsync(owned.Token);
            if (typed) Assert.IsType<DBConcurrencyException>(failure);
            else Assert.IsType<DbUpdateConcurrencyException>(failure);
            Assert.Equal(reinsert ? new[] { replacement } : [], actual);
            Assert.Equal(2, owned.Gate.CommandsSent);
            await owned.AssertRecoveryAsync(write.Pid);
            owned.Report(new { scenario = "disappeared-original", typed, reinsert, write,
                exceptionType = failure?.GetType().FullName, returned = failure is null ? await pending : (bool?)null, actual });
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReadWaitPrecedesClientStampWithoutATransactionSpanningReadAndWrite(bool typed, bool existing)
    {
        await InUpsertCaseAsync(typed, "clock-and-transaction", async owned =>
        {
            if (existing)
                await owned.InsertAsync(new Row(100, "target", "original", "original-description", Created, Updated), owned.Token);
            await owned.LockAsync();
            var pending = owned.Start("requested", "requested-description");
            var first = await owned.Gate.FirstEntered.WaitAsync(owned.Token);
            Assert.Equal("SELECT", first.Kind);
            var waiting = await owned.ObserveWaitAsync(first.Pid, "AccessShareLock", pending);
            Assert.NotNull(waiting.TransactionStarted);
            var releaseBefore = DateTime.UtcNow;
            await owned.UnlockAsync(owned.Token);
            var releaseAfter = DateTime.UtcNow;
            var write = await owned.Gate.WriteEntered.WaitAsync(owned.Token);
            AssertSharedWriteShape(write, existing ? "UPDATE" : "INSERT");
            Assert.Equal(first.Pid, write.Pid);
            Assert.All(write.UtcParameters, captured => Assert.True(captured >= releaseBefore));
            var beforeWrite = await owned.ReadBackendAsync(write.Pid, owned.Token);
            Assert.True(beforeWrite.State is "idle" or "idle in transaction");
            // EF may open a write-only SaveChanges transaction. The read
            // transaction observed under the lock must not span the pair.
            Assert.True(beforeWrite.TransactionStarted is null ||
                beforeWrite.TransactionStarted != waiting.TransactionStarted);
            owned.Gate.Release();
            Assert.True(await pending.WaitAsync(owned.Token));
            var actual = Assert.Single(await owned.ReadRowsAsync(owned.Token));
            Assert.Equal("requested", actual.Value);
            Assert.Equal("requested-description", actual.Description);
            var logicalUtc = Assert.Single(write.UtcParameters.Distinct());
            Assert.Equal(WireUtc(logicalUtc), actual.UpdatedAt);
            if (!existing) Assert.Equal(actual.CreatedAt, actual.UpdatedAt);
            if (existing) Assert.Equal(Created, actual.CreatedAt);
            Assert.Equal(2, owned.Gate.CommandsSent);
            await owned.AssertRecoveryAsync(write.Pid);
            owned.Report(new { scenario = "clock-and-transaction", typed, existing, first, waiting,
                releaseBefore, releaseAfter, write, beforeWrite, actual });
        });
    }

    private static void AssertSharedWriteShape(WriteEntry write, string expectedKind)
    {
        Assert.Equal(expectedKind, write.Kind);
        Assert.Equal(2, write.Ordinal);
        Assert.Equal(1, write.CompletedSelects);
        Assert.NotEmpty(write.UtcParameters);
        Assert.Single(write.UtcParameters.Distinct());
    }

    private async Task InUpsertCaseAsync(bool typed, string scenario, Func<OwnedCase, Task> work)
    {
        var schema = $"gs_upsert_{Guid.NewGuid():N}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var gate = new UpsertGate();
        NpgsqlDataSource? source = null;
        ServiceProvider? provider = null;
        ActivityListener? listener = null;
        OwnedCase? owned = null;
        var createdSchema = false;
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            createdSchema = true;
            await CreateSchemaAsync(schema, deadline.Token);
            var sourceString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                SearchPath = schema, ApplicationName = schema, Pooling = true,
                MinPoolSize = 0, MaxPoolSize = 1, Multiplexing = false,
                Timeout = 10, CommandTimeout = 20, CancellationTimeout = 1000
            }.ConnectionString;
            listener = new ActivityListener
            {
                ShouldListenTo = activitySource => activitySource.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> sampling) => ActivitySamplingResult.None
            };
            ActivitySource.AddActivityListener(listener);
            var builder = new NpgsqlDataSourceBuilder(sourceString)
                .UseLoggerFactory(gate).EnableParameterLogging(false);
            builder.ConfigureTracing(options =>
            {
                options.EnablePhysicalOpenTracing(false);
                options.ConfigureCommandFilter(gate.BeforeSend);
            });
            source = builder.Build();
            IGlobalSettingRepository repository;
            if (typed) repository = new NpgsqlGlobalSettingRepository(source);
            else
            {
                var services = new ServiceCollection();
                services.AddDbContextFactory<ConduitDbContext>(options => options.UseNpgsql(source));
                provider = services.BuildServiceProvider(new ServiceProviderOptions
                    { ValidateOnBuild = true, ValidateScopes = true });
                var factory = provider.GetRequiredService<IDbContextFactory<ConduitDbContext>>();
                Assert.StartsWith("Microsoft.EntityFrameworkCore.Internal.DbContextFactory`1", factory.GetType().FullName!, StringComparison.Ordinal);
                repository = new GlobalSettingRepository(factory, NullLogger<GlobalSettingRepository>.Instance);
            }
            owned = new OwnedCase(this, schema, sourceString, source, repository, gate, caller.Token);
            await work(owned);
            Assert.Equal(1, gate.WriteCallbacksCompleted);
        }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
        [
            async cleanup => { caller.Cancel(); gate.Release(); if (owned is not null) await owned.ReapAsync(cleanup); },
            async cleanup => { if (owned is not null) await owned.UnlockAsync(cleanup); },
            async cleanup => { if (owned is not null) await owned.DisposeTransactionAsync(cleanup); },
            async cleanup => { if (owned is not null) await owned.DisposeBlockerAsync(cleanup); },
            async cleanup => { if (owned is not null) await owned.ReapAsync(cleanup); },
            async cleanup => { if (provider is not null) await provider.DisposeAsync().AsTask().WaitAsync(cleanup); },
            async cleanup => { if (source is not null) await source.DisposeAsync().AsTask().WaitAsync(cleanup); },
            cleanup => { listener?.Dispose(); return Task.CompletedTask; },
            cleanup => { gate.Dispose(); return Task.CompletedTask; },
            async cleanup => { if (createdSchema) await DropSchemaAsync(schema, cleanup); }
        ], CleanupLimit, "Upsert fixture owners were not independently cleaned up."),
        $"Upsert {scenario} work and owned cleanup both failed.");
    }

    private string ManagementConnection() => new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { Pooling = false, Timeout = 5, CommandTimeout = 5, ApplicationName = "gs_upsert_control" }.ConnectionString;

    private async Task CreateSchemaAsync(string schema, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(ManagementConnection());
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
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task DropSchemaAsync(string schema, CancellationToken token)
    {
        Assert.StartsWith("gs_upsert_", schema, StringComparison.Ordinal);
        Assert.Equal(42, schema.Length);
        Assert.True(Guid.TryParseExact(schema[10..], "N", out _));
        await using var connection = new NpgsqlConnection(ManagementConnection());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT COUNT(*) FROM pg_catalog.pg_namespace WHERE nspname=@schema";
        command.Parameters.AddWithValue("schema", schema);
        Assert.Equal(0L, await command.ExecuteScalarAsync(token));
        _output.WriteLine("GLOBAL_SETTING_UPSERT_SCHEMA_ABSENT=" + schema);
    }

    private static async Task<Exception?> ObserveAsync(Task<bool> pending, CancellationToken token) =>
        await Record.ExceptionAsync(async () => await pending.WaitAsync(token));

    private static PostgresException? FindPostgres(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current is PostgresException postgres) return postgres;
        return null;
    }

    private static DateTime WireUtc(DateTime value) => new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);
    private sealed record Row(int Id, string Key, string Value, string? Description, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record Backend(int Pid, string State, DateTime? TransactionStarted);
    private sealed record FirstEntry(int Pid, string Kind);
    private sealed record WriteEntry(int Pid, string Kind, int Ordinal, int CompletedSelects,
        DateTime[] UtcParameters, int[] IntegerParameters);

    private sealed class OwnedCase(GlobalSettingUpsertPersistenceParityTests owner, string schema,
        string sourceConnection, NpgsqlDataSource source, IGlobalSettingRepository repository,
        UpsertGate gate, CancellationToken token)
    {
        private NpgsqlConnection? _blocker;
        private NpgsqlTransaction? _transaction;
        private Task<bool>? _pending;
        public CancellationToken Token => token;
        public UpsertGate Gate => gate;

        public Task<bool> Start(string value, string? description)
        {
            Assert.Null(_pending);
            return _pending = Task.Run(() => repository.UpsertAsync("target", value, description, token));
        }

        public async Task ReapAsync(CancellationToken cleanup)
        {
            if (_pending is null) return;
            try { await _pending.WaitAsync(cleanup); }
            catch (Exception) when (_pending.IsCanceled || _pending.IsFaulted) { }
        }

        public async Task<Row[]> ReadRowsAsync(CancellationToken readToken)
        {
            await using var connection = new NpgsqlConnection(owner.ManagementConnection());
            await connection.OpenAsync(readToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT "Id", "Key", "Value", "Description", "CreatedAt", "UpdatedAt"
                FROM "{schema}"."GlobalSettings" ORDER BY "Id"
                """;
            var rows = new List<Row>();
            await using var reader = await command.ExecuteReaderAsync(readToken);
            while (await reader.ReadAsync(readToken)) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDateTime(4), reader.GetDateTime(5)));
            return rows.ToArray();
        }

        public async Task InsertAsync(Row row, CancellationToken writeToken)
        {
            await using var connection = new NpgsqlConnection(owner.ManagementConnection());
            await connection.OpenAsync(writeToken);
            await using var command = connection.CreateCommand();
            command.CommandText = InsertSql();
            Bind(command, row);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(writeToken));
        }

        public async Task ReplaceFieldsAsync(Row row, CancellationToken writeToken)
        {
            await using var connection = new NpgsqlConnection(owner.ManagementConnection());
            await connection.OpenAsync(writeToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE "{schema}"."GlobalSettings" SET "Key"=@key, "Value"=@value,
                    "Description"=@description, "CreatedAt"=@created, "UpdatedAt"=@updated WHERE "Id"=@id
                """;
            Bind(command, row);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(writeToken));
        }

        public async Task DeleteOriginalAsync(Row? replacement, CancellationToken writeToken)
        {
            await using var connection = new NpgsqlConnection(owner.ManagementConnection());
            await connection.OpenAsync(writeToken);
            await using var transaction = await connection.BeginTransactionAsync(writeToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM \"{schema}\".\"GlobalSettings\" WHERE \"Id\"=100";
            Assert.Equal(1, await command.ExecuteNonQueryAsync(writeToken));
            if (replacement is not null)
            {
                command.CommandText = InsertSql();
                Bind(command, replacement);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(writeToken));
            }
            await transaction.CommitAsync(writeToken);
        }

        private string InsertSql() => $"""
            INSERT INTO "{schema}"."GlobalSettings" ("Id", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
            VALUES (@id,@key,@value,@description,@created,@updated)
            """;

        private static void Bind(NpgsqlCommand command, Row row)
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, row.Id);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, row.Key);
            command.Parameters.AddWithValue("value", NpgsqlDbType.Varchar, row.Value);
            command.Parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Varchar) { Value = (object?)row.Description ?? DBNull.Value });
            command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, row.CreatedAt);
            command.Parameters.AddWithValue("updated", NpgsqlDbType.TimestampTz, row.UpdatedAt);
        }

        public async Task LockAsync()
        {
            _blocker = new NpgsqlConnection(owner.ManagementConnection());
            await _blocker.OpenAsync(token);
            _transaction = await _blocker.BeginTransactionAsync(token);
            await using var command = _blocker.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = $"LOCK TABLE \"{schema}\".\"GlobalSettings\" IN ACCESS EXCLUSIVE MODE";
            await command.ExecuteNonQueryAsync(token);
        }

        public async Task UnlockAsync(CancellationToken cleanup)
        {
            if (_transaction is null) return;
            await _transaction.RollbackAsync(cleanup);
            await _transaction.DisposeAsync().AsTask().WaitAsync(cleanup);
            _transaction = null;
        }

        public async Task DisposeBlockerAsync(CancellationToken cleanup)
        {
            if (_blocker is not null) await _blocker.DisposeAsync().AsTask().WaitAsync(cleanup);
        }

        public async Task DisposeTransactionAsync(CancellationToken cleanup)
        {
            if (_transaction is not null) await _transaction.DisposeAsync().AsTask().WaitAsync(cleanup);
        }

        public async Task<Backend> ReadBackendAsync(int pid, CancellationToken readToken)
        {
            await using var connection = new NpgsqlConnection(owner.ManagementConnection());
            await connection.OpenAsync(readToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pid,state,xact_start FROM pg_catalog.pg_stat_activity
                WHERE pid=@pid AND application_name=@application AND datname=current_database()
                """;
            command.Parameters.AddWithValue("pid", pid);
            command.Parameters.AddWithValue("application", new NpgsqlConnectionStringBuilder(sourceConnection).ApplicationName!);
            await using var reader = await command.ExecuteReaderAsync(readToken);
            Assert.True(await reader.ReadAsync(readToken));
            var result = new Backend(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2));
            Assert.False(await reader.ReadAsync(readToken));
            return result;
        }

        public async Task<Backend> ObserveWaitAsync(int readerPid, string mode, Task pending)
        {
            Assert.NotNull(_blocker);
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
            observation.CancelAfter(TimeSpan.FromSeconds(10));
            await using var connection = new NpgsqlConnection(owner.ManagementConnection());
            await connection.OpenAsync(observation.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT activity.pid,activity.state,activity.xact_start
                FROM pg_catalog.pg_stat_activity activity
                JOIN pg_catalog.pg_locks waiting ON waiting.pid=activity.pid
                JOIN pg_catalog.pg_class relation ON relation.oid=waiting.relation
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
                JOIN pg_catalog.pg_locks blocking ON blocking.relation=waiting.relation
                    AND blocking.pid=@blocker AND blocking.granted AND blocking.mode='AccessExclusiveLock'
                WHERE activity.pid=@reader AND activity.application_name=@application
                    AND activity.datname=current_database() AND activity.state='active'
                    AND activity.wait_event_type='Lock' AND @blocker=ANY(pg_catalog.pg_blocking_pids(activity.pid))
                    AND waiting.locktype='relation' AND NOT waiting.granted AND waiting.mode=@mode
                    AND waiting.relation=pg_catalog.to_regclass(@relation)
                    AND namespace.nspname=@schema AND relation.relname='GlobalSettings'
                """;
            command.Parameters.AddWithValue("blocker", _blocker!.ProcessID);
            command.Parameters.AddWithValue("reader", readerPid);
            command.Parameters.AddWithValue("application", new NpgsqlConnectionStringBuilder(sourceConnection).ApplicationName!);
            command.Parameters.AddWithValue("mode", mode);
            command.Parameters.AddWithValue("relation", $"\"{schema}\".\"GlobalSettings\"");
            command.Parameters.AddWithValue("schema", schema);
            while (true)
            {
                Assert.False(pending.IsCompleted, "Upsert must remain in the exact observed relation wait.");
                await using (var reader = await command.ExecuteReaderAsync(observation.Token))
                {
                    if (await reader.ReadAsync(observation.Token))
                    {
                        var result = new Backend(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2));
                        Assert.Equal(readerPid, result.Pid);
                        Assert.NotNull(result.TransactionStarted);
                        return result;
                    }
                }
                await Task.Delay(25, observation.Token);
            }
        }

        public async Task AssertRecoveryAsync(int expectedPid)
        {
            await using var connection = await source.OpenConnectionAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            Assert.Equal(1, await command.ExecuteScalarAsync(token));
            Assert.Equal(expectedPid, connection.ProcessID);
        }

        public void Report<T>(T value) => owner._output.WriteLine("GLOBAL_SETTING_UPSERT_CONFORMANCE=" + JsonSerializer.Serialize(value));
    }

    // Npgsql10.0.3's public tracing hook runs with processed parameters before
    // sending SQL. It never changes the query, parameters or response. Each
    // source has its own gate; the listener merely makes the hook eligible and
    // emits no Activity data. Parameter logging stays disabled throughout.
    private sealed class UpsertGate : ILoggerFactory, ILogger
    {
        private readonly TaskCompletionSource<FirstEntry> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WriteEntry> _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sent, _completedSelects, _writes, _writeCallbacksCompleted;
        public Task<FirstEntry> FirstEntered => _first.Task;
        public Task<WriteEntry> WriteEntered => _write.Task;
        public int CommandsSent => Volatile.Read(ref _sent);
        public int WriteCallbacksCompleted => Volatile.Read(ref _writeCallbacksCompleted);

        public bool BeforeSend(NpgsqlCommand command)
        {
            if (!command.CommandText.Contains("\"GlobalSettings\"", StringComparison.Ordinal)) return false;
            var kind = Kind(command.CommandText);
            var ordinal = Interlocked.Increment(ref _sent);
            var pid = command.Connection!.ProcessID;
            _first.TrySetResult(new(pid, kind));
            if (kind == "SELECT") return false;
            Assert.True(kind is "INSERT" or "UPDATE");
            Assert.Equal(1, Interlocked.Increment(ref _writes));
            var timestampParameters = command.Parameters.Cast<NpgsqlParameter>()
                .Where(parameter => parameter.Value is DateTime).ToArray();
            foreach (var parameter in timestampParameters)
            {
                Assert.Equal(NpgsqlDbType.TimestampTz, parameter.NpgsqlDbType);
                Assert.Equal(DateTimeKind.Utc, ((DateTime)parameter.Value!).Kind);
            }
            var entry = new WriteEntry(pid, kind, ordinal, Volatile.Read(ref _completedSelects),
                timestampParameters.Select(parameter => (DateTime)parameter.Value!).ToArray(),
                command.Parameters.Cast<NpgsqlParameter>().Where(parameter => parameter.NpgsqlDbType == NpgsqlDbType.Integer)
                    .Select(parameter => Assert.IsType<int>(parameter.Value)).ToArray());
            _write.TrySetResult(entry);
            _release.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Interlocked.Increment(ref _writeCallbacksCompleted);
            return false;
        }

        public void Release() => _release.TrySetResult();
        public ILogger CreateLogger(string categoryName) => categoryName == "Npgsql.Command" ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToArray();
            Assert.DoesNotContain(values, field => field.Key == "Parameters");
            var sql = values.FirstOrDefault(field => field.Key == "CommandText").Value as string;
            if (sql is null || !sql.Contains("\"GlobalSettings\"", StringComparison.Ordinal)) return;
            if (eventId.Id == NpgsqlEventId.CommandExecutionCompleted && Kind(sql) == "SELECT")
                Interlocked.Increment(ref _completedSelects);
        }

        private static string Kind(string sql)
        {
            var trimmed = sql.TrimStart();
            foreach (var kind in new[] { "SELECT", "INSERT", "UPDATE" })
                if (trimmed.StartsWith(kind, StringComparison.OrdinalIgnoreCase)) return kind;
            throw new InvalidOperationException("Unexpected global-setting SQL command kind.");
        }
        public void Dispose() => Release();
    }
}
#endif
