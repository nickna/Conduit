using ConduitLLM.Configuration.Data;
using ConduitLLM.IntegrationTests.Infrastructure;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class SchemaVersionProbeTests(PostgresLockTestContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = $"schema_probe_{Guid.NewGuid():N}";
    private readonly string _applicationName = $"schema-probe-{Guid.NewGuid():N}";
    private NpgsqlConnection _control = null!;
    private NpgsqlDataSource _dataSource = null!;
    private SchemaVersionProbe Probe => new(_dataSource);

    public async Task InitializeAsync()
    {
        _control = new NpgsqlConnection(fixture.ConnectionString);
        await _control.OpenAsync();
        await ExecuteAsync($"CREATE SCHEMA \"{_schema}\"");
        _dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = _schema,
            ApplicationName = _applicationName,
            Timeout = 5,
            CommandTimeout = 10,
            MaxPoolSize = 1,
            MinPoolSize = 0
        }.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        try { await ExecuteAsync($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE"); }
        finally { await _control.DisposeAsync(); }
    }

    [Fact]
    public async Task HistoryLifecycleReportsExactLatestVersionAndRecoversAfterRemoval()
    {
        Assert.Null((await Probe.GetStatusAsync()).AppliedVersion);
        await CreateHistoryAsync();
        Assert.Null((await Probe.GetStatusAsync()).AppliedVersion);
        await InsertVersionAsync("20200101000000_Old");
        var stale = await Probe.GetStatusAsync();
        Assert.Equal("20200101000000_Old", stale.AppliedVersion);
        Assert.False(stale.IsCurrent);
        Assert.Equal(ConduitSchemaVersion.Current, stale.ExpectedVersion);

        await InsertVersionAsync(ConduitSchemaVersion.Current);
        // Inserting an older row last must not change the latest version.
        await InsertVersionAsync("20210101000000_Older");
        Assert.True((await Probe.GetStatusAsync()).IsCurrent);
        await InsertVersionAsync("99999999999999_Future");
        var future = await Probe.GetStatusAsync();
        Assert.Equal("99999999999999_Future", future.AppliedVersion);
        Assert.False(future.IsCurrent);

        await ExecuteAsync($"DROP TABLE \"{_schema}\".\"__EFMigrationsHistory\"");
        Assert.Null((await Probe.GetStatusAsync()).AppliedVersion);
        await CreateHistoryAsync();
        await InsertVersionAsync(ConduitSchemaVersion.Current.ToLowerInvariant());
        Assert.False((await Probe.GetStatusAsync()).IsCurrent);
        await ExecuteAsync($"TRUNCATE \"{_schema}\".\"__EFMigrationsHistory\"");
        await InsertVersionAsync(ConduitSchemaVersion.Current);
        Assert.True((await Probe.GetStatusAsync()).IsCurrent);
    }

    [Fact]
    public async Task MalformedHistoryPropagatesUndefinedColumn()
    {
        await ExecuteAsync($"CREATE TABLE \"{_schema}\".\"__EFMigrationsHistory\" (\"WrongColumn\" text)");
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Probe.GetStatusAsync());
        Assert.Equal(PostgresErrorCodes.UndefinedColumn, exception.SqlState);
    }

    [Fact]
    public async Task CancelledTokenPropagatesAndPoolCanBeReused()
    {
        await CreateHistoryAsync();
        await InsertVersionAsync(ConduitSchemaVersion.Current);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe.GetStatusAsync(cancellation.Token));
        Assert.True((await Probe.GetStatusAsync()).IsCurrent);
    }

    [Fact]
    public async Task BlockedQueryCanBeCancelledAndPoolCanBeReused()
    {
        await CreateHistoryAsync();
        await InsertVersionAsync(ConduitSchemaVersion.Current);
        await using var transaction = await _control.BeginTransactionAsync();
        await ExecuteAsync($"LOCK TABLE \"{_schema}\".\"__EFMigrationsHistory\" IN ACCESS EXCLUSIVE MODE", transaction);
        using var cancellation = new CancellationTokenSource();
        var pending = Probe.GetStatusAsync(cancellation.Token);
        try
        {
            await WaitForBlockedProbeAsync();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
        Assert.True((await Probe.GetStatusAsync()).IsCurrent);
    }

    [Fact]
    public async Task HistoryDroppedWhileProbeWaitsBecomesUninitializedAndCanRecover()
    {
        await CreateHistoryAsync();
        await InsertVersionAsync(ConduitSchemaVersion.Current);
        await using var transaction = await _control.BeginTransactionAsync();
        await ExecuteAsync($"DROP TABLE \"{_schema}\".\"__EFMigrationsHistory\"", transaction);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = Probe.GetStatusAsync(cancellation.Token);
        try
        {
            await WaitForBlockedProbeAsync();
            await transaction.CommitAsync();
            Assert.Null((await pending).AppliedVersion);
        }
        finally
        {
            cancellation.Cancel();
            // Disposing an uncommitted transaction releases the DDL lock on failures.
            await transaction.DisposeAsync();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
        await CreateHistoryAsync();
        await InsertVersionAsync(ConduitSchemaVersion.Current);
        Assert.True((await Probe.GetStatusAsync()).IsCurrent);
    }

    [Fact]
    public async Task UnreachableDatabasePropagatesConnectionFailure()
    {
        await using var unreachable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unreachable;Username=conduit;Password=conduit;Timeout=1");
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => new SchemaVersionProbe(unreachable).GetStatusAsync());
    }

    private Task CreateHistoryAsync() => ExecuteAsync($"""
        CREATE TABLE "{_schema}"."__EFMigrationsHistory" (
            "MigrationId" varchar(150) PRIMARY KEY,
            "ProductVersion" varchar(32) NOT NULL)
        """);

    private async Task InsertVersionAsync(string version)
    {
        await using var command = _control.CreateCommand();
        command.CommandText = $"""
            INSERT INTO "{_schema}"."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES (@version, '10.0.3')
            """;
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = _control.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task WaitForBlockedProbeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Observe each poll in autocommit mode: pg_stat_activity snapshots are
        // otherwise retained throughout the blocker transaction.
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync(timeout.Token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE application_name = @application
                  AND wait_event_type = 'Lock'
                  AND @blocker = ANY(pg_blocking_pids(pid)))
            """;
        command.Parameters.AddWithValue("application", _applicationName);
        command.Parameters.AddWithValue("blocker", _control.ProcessID);
        while (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!)
        {
            await Task.Delay(25, timeout.Token);
        }
    }
}
