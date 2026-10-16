using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Services;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;
using NpgsqlTypes;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class FunctionExecutionConfigurationPersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public async Task ConfigurationReadsPreserveScalarsDisabledRowsAndEmptyInputBehavior()
    {
        var schema = $"function_runtime_{Guid.NewGuid():N}";
        try
        {
            await SeedAsync(schema);
            var connectionString = WithSearchPath(schema);
            await using var source = NpgsqlDataSource.Create(connectionString);
            await using var efSource = NpgsqlDataSource.Create(connectionString);
            var stores = CreateStores(efSource, source);
            foreach (var id in new[] { -1, 0, 1, 2, 3, 999 })
                (await stores[1].GetConfigurationAsync(id)).Should()
                    .BeEquivalentTo(await stores[0].GetConfigurationAsync(id));

            IReadOnlyList<int>?[] inputs = [null, Array.Empty<int>(), new[] { 2, 1, 2, 3, -1, 999 }, new[] { 999 }];
            foreach (var ids in inputs)
                (await stores[1].GetConfigurationsAsync(ids)).Should()
                    .BeEquivalentTo(await stores[0].GetConfigurationsAsync(ids));
            foreach (var store in stores)
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                (await store.GetConfigurationsAsync(null, cancelled.Token)).Should().BeEmpty();
                (await store.GetConfigurationsAsync(Array.Empty<int>(), cancelled.Token)).Should().BeEmpty();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.GetConfigurationsAsync(new[] { 1 }, cancelled.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.GetConfigurationAsync(1, cancelled.Token));

                var disabled = (await store.GetConfigurationAsync(2))!;
                disabled.IsEnabled.Should().BeFalse();
                disabled.ConfigurationName.Should().Be("O'Hare 雪");
                disabled.ProviderSettings.Should().Contain("deep");
                disabled.ParameterSchema.Should().Contain("example");
                disabled.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
                disabled.UpdatedAt.Should().Be(disabled.CreatedAt.AddMinutes(1));
                disabled.CostMappings.Should().BeEmpty();
                disabled.Executions.Should().BeEmpty();
                disabled.ConfigurationName = "detached mutation";
                (await store.GetConfigurationAsync(2))!.ConfigurationName.Should().Be("O'Hare 雪");
                (await store.GetConfigurationsAsync(new[] { 2, 1, 2, 3, -1, 999 }))
                    .Select(configuration => configuration.Id).Should().BeEquivalentTo([1, 2, 3]);
                var unknown = (await store.GetConfigurationAsync(3))!;
                ((int)unknown.ProviderType).Should().Be(197);
                ((int)unknown.Purpose).Should().Be(-9);
                ((int)unknown.DefaultExecutionMode).Should().Be(123);
            }
        }
        finally { await DropAsync(schema); }
    }

    [Fact]
    public async Task CredentialsPreserveLegacyOrderingScopesOpaqueSecretsAndDetachedReads()
    {
        var schema = $"function_runtime_{Guid.NewGuid():N}";
        try
        {
            await SeedAsync(schema);
            var connectionString = WithSearchPath(schema);
            await using var source = NpgsqlDataSource.Create(connectionString);
            await using var efSource = NpgsqlDataSource.Create(connectionString);
            var stores = CreateStores(efSource, source);
            var legacy = new FunctionCredentialRepository(new ContextFactory(efSource),
                NullLogger<FunctionCredentialRepository>.Instance);
            foreach (var provider in new[] { FunctionProviderType.Exa, FunctionProviderType.Tavily, (FunctionProviderType)197 })
            {
                var expected = await legacy.GetByProviderTypeAsync(provider);
                foreach (var store in stores)
                {
                    var credentials = await store.ListCredentialsForProviderAsync(provider);
                    credentials.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
                }
            }
            foreach (var store in stores)
            {
                var credentials = await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa);
                credentials.Select(credential => credential.Id).Should().Equal(103, 100, 101, 102, 104, 107, 105);
                CredentialSelector.SelectForConfiguration(credentials, 1)!.Id.Should().Be(101);
                CredentialSelector.SelectForConfiguration(credentials, 2)!.Id.Should().Be(102);
                CredentialSelector.SelectForConfiguration(credentials, 3)!.Id.Should().Be(100);
                CredentialSelector.SelectForConfiguration(credentials, 999)!.Id.Should().Be(100);
                var scoped = credentials.Single(credential => credential.Id == 101);
                scoped.ApiKey.Should().Be("enc:v1:opaque=='雪'");
                scoped.BaseUrl.Should().Be("https://example.test/scoped");
                scoped.Organization.Should().Be("O'Hare 雪");
                scoped.FunctionAccountGroup.Should().Be(32);
                scoped.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
                scoped.UpdatedAt.Should().Be(scoped.CreatedAt.AddMinutes(1));
                credentials.Single(credential => credential.Id == 102).ApiKey.Should().Be("enc:v1:invalid");
                scoped.ApiKey = "mutated in memory";
                (await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa))
                    .Single(credential => credential.Id == 101).ApiKey.Should().Be("enc:v1:opaque=='雪'");
                var unknown = (await store.ListCredentialsForProviderAsync((FunctionProviderType)197)).Single();
                unknown.ApiKey.Should().BeNull();
                unknown.KeyName.Should().BeNull();
                unknown.Organization.Should().BeNull();
                unknown.BaseUrl.Should().BeNull();
                unknown.FunctionAccountGroup.Should().Be(short.MaxValue);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, cancelled.Token));
            }
        }
        finally { await DropAsync(schema); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledPoolAcquisitionReleasesTheSingleConnectionSlot(bool typed)
    {
        var schema = $"function_runtime_{Guid.NewGuid():N}";
        try
        {
            await SeedAsync(schema);
            var connectionString = WithSearchPath(schema);
            await using var source = NpgsqlDataSource.Create(connectionString);
            var store = CreateStores(source, source)[typed ? 1 : 0];
            (await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa)).Count.Should().Be(7);
            await using var held = await source.OpenConnectionAsync();
            using var cancelled = new CancellationTokenSource();
            var read = store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, cancelled.Token);
            try
            {
                // The only connection is held by this test. The read is pending
                // before cancellation, so it cannot have reached PostgreSQL.
                read.IsCompleted.Should().BeFalse();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                cancelled.Cancel();
                try { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            }
            await held.CloseAsync();
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            (await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, recovery.Token)).Count.Should().Be(7);
        }
        finally { await DropAsync(schema); }
    }

    [Theory]
    [InlineData(false, nameof(IFunctionExecutionConfigurationStore.GetConfigurationAsync))]
    [InlineData(false, nameof(IFunctionExecutionConfigurationStore.GetConfigurationsAsync))]
    [InlineData(false, nameof(IFunctionExecutionConfigurationStore.ListCredentialsForProviderAsync))]
    [InlineData(true, nameof(IFunctionExecutionConfigurationStore.GetConfigurationAsync))]
    [InlineData(true, nameof(IFunctionExecutionConfigurationStore.GetConfigurationsAsync))]
    [InlineData(true, nameof(IFunctionExecutionConfigurationStore.ListCredentialsForProviderAsync))]
    public async Task SqlInFlightCancellationReleasesTheSingleConnectionSlot(bool typed, string readMethod)
    {
        var schema = $"function_runtime_{Guid.NewGuid():N}";
        var table = readMethod == nameof(IFunctionExecutionConfigurationStore.ListCredentialsForProviderAsync)
            ? "FunctionCredentials" : "FunctionConfigurations";
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Only the explicit Cancel after the observed SQL wait cancels this token.
        // Setup/observer timeouts must fail the proof, not satisfy its assertion.
        using var cancelledRead = new CancellationTokenSource();
        NpgsqlDataSource? source = null;
        NpgsqlConnection? blocker = null;
        NpgsqlConnection? observer = null;
        NpgsqlTransaction? transaction = null;
        Task? pending = null;
        var lockReleased = false;

        await IntegrationProofCleanup.RunAsync(async () =>
        {
            await SeedAsync(schema, setup.Token);
            var connectionString = new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
            {
                ApplicationName = schema, MinPoolSize = 0, MaxPoolSize = 1,
                Timeout = 5, CommandTimeout = 30, CancellationTimeout = 1000
            }.ConnectionString;
            source = NpgsqlDataSource.Create(connectionString);
            var store = CreateStores(source, source)[typed ? 1 : 0];
            await AssertConfigurationReadsRecoverAsync(store, setup.Token);
            var readerPid = await AssertConfigurationPoolReusableAsync(source, setup.Token);

            blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                { Pooling = false, ApplicationName = $"{schema}_blocker" }.ConnectionString);
            await blocker.OpenAsync(setup.Token);
            transaction = await blocker.BeginTransactionAsync(setup.Token);
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"LOCK TABLE \"{schema}\".\"{table}\" IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync(setup.Token);
            }
            observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                { Pooling = false, ApplicationName = $"{schema}_observer" }.ConnectionString);
            await observer.OpenAsync(setup.Token);

            pending = InvokeConfigurationReadAsync(store, readMethod, cancelledRead.Token);
            await WaitForConfigurationRelationWaitAsync(observer, schema, table,
                readerPid, blocker.ProcessID, pending, setup.Token);
            cancelledRead.Cancel();
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancelledRead.Token, cancellation.CancellationToken);

            // The table lock is still held. Reusing the only pooled slot for an
            // unrelated scalar proves cancellation reaped the command itself.
            using (var poolRecovery = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await AssertConfigurationPoolReusableAsync(source, poolRecovery.Token);
            using (var release = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await transaction.RollbackAsync(release.Token);
            lockReleased = true;
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await AssertConfigurationReadsRecoverAsync(store, recovery.Token);
        }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
            [async token =>
                {
                    cancelledRead.Cancel();
                    if (pending is not null) await ReapConfigurationReadAsync(pending, token);
                },
             async token =>
                {
                    if (transaction is not null && !lockReleased)
                        await transaction.RollbackAsync(token);
                },
             async token =>
                {
                    if (blocker is not null) await blocker.DisposeAsync().AsTask().WaitAsync(token);
                },
             // Releasing/disposal of the blocker runs even if the first drain
             // fails, then the pending operation is joined again before its pool.
             async token =>
                {
                    if (pending is not null) await ReapConfigurationReadAsync(pending, token);
                },
             async token =>
                {
                    if (transaction is not null) await transaction.DisposeAsync().AsTask().WaitAsync(token);
                },
             async token =>
                {
                    if (observer is not null) await observer.DisposeAsync().AsTask().WaitAsync(token);
                },
             async token =>
                {
                    if (source is not null) await source.DisposeAsync().AsTask().WaitAsync(token);
                },
             token => DropAsync(schema, token)],
            TimeSpan.FromSeconds(5), "Pre-execution configuration SQL cancellation owners could not all be drained."),
            "Pre-execution configuration SQL cancellation and owned cleanup failed.");
    }

    private static Task InvokeConfigurationReadAsync(
        IFunctionExecutionConfigurationStore store, string readMethod, CancellationToken token) => readMethod switch
    {
        nameof(IFunctionExecutionConfigurationStore.GetConfigurationAsync) => store.GetConfigurationAsync(2, token),
        nameof(IFunctionExecutionConfigurationStore.GetConfigurationsAsync) =>
            store.GetConfigurationsAsync(new[] { 2, 1, 2, 3, -1, 999 }, token),
        nameof(IFunctionExecutionConfigurationStore.ListCredentialsForProviderAsync) =>
            store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, token),
        _ => throw new ArgumentOutOfRangeException(nameof(readMethod))
    };

    private static async Task AssertConfigurationReadsRecoverAsync(
        IFunctionExecutionConfigurationStore store, CancellationToken token)
    {
        var configuration = (await store.GetConfigurationAsync(2, token))!;
        configuration.ConfigurationName.Should().Be("O'Hare 雪");
        configuration.IsEnabled.Should().BeFalse();
        (await store.GetConfigurationsAsync(new[] { 2, 1, 2, 3, -1, 999 }, token))
            .Select(row => row.Id).Should().BeEquivalentTo([1, 2, 3]);
        (await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, token))
            .Select(row => row.Id).Should().Equal(103, 100, 101, 102, 104, 107, 105);
    }

    private static async Task<int> AssertConfigurationPoolReusableAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        (await command.ExecuteScalarAsync(token)).Should().Be(1);
        return connection.ProcessID;
    }

    private static async Task ReapConfigurationReadAsync(Task pending, CancellationToken cleanupToken)
    {
        try { await pending.WaitAsync(cleanupToken); }
        // Do not mistake the independent cleanup deadline for a reaped query.
        catch (OperationCanceledException) when (pending.IsCanceled) { }
    }

    private static async Task WaitForConfigurationRelationWaitAsync(
        NpgsqlConnection observer, string schema, string table, int readerPid, int blockerPid,
        Task pending, CancellationToken setupToken)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(setupToken);
        observation.CancelAfter(TimeSpan.FromSeconds(10));
        await using var command = observer.CreateCommand();
        command.CommandTimeout = 5;
        // Autocommit observations get a fresh statistics snapshot each time.
        // Identify the actual waiter, exact relation and our actual blocker;
        // a different backend or an unrelated lock cannot satisfy this oracle.
        command.CommandText = """
            SELECT activity.pid
            FROM pg_catalog.pg_stat_activity activity
            JOIN pg_catalog.pg_locks waiting ON waiting.pid = activity.pid
            JOIN pg_catalog.pg_class relation ON relation.oid = waiting.relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_locks blocking ON blocking.relation = waiting.relation
                AND blocking.pid = @blocker AND blocking.granted
                AND blocking.mode = 'AccessExclusiveLock'
            WHERE activity.pid = @reader AND activity.application_name = @application
                AND activity.datname = current_database() AND activity.wait_event_type = 'Lock'
                AND @blocker = ANY(pg_catalog.pg_blocking_pids(activity.pid))
                AND waiting.locktype = 'relation' AND NOT waiting.granted
                AND waiting.mode = 'AccessShareLock'
                AND waiting.relation = pg_catalog.to_regclass(@relation)
                AND namespace.nspname = @schema AND relation.relname = @table
            """;
        command.Parameters.AddWithValue("reader", readerPid);
        command.Parameters.AddWithValue("blocker", blockerPid);
        command.Parameters.AddWithValue("application", schema);
        command.Parameters.AddWithValue("relation", $"\"{schema}\".\"{table}\"");
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        while (true)
        {
            if (pending.IsCompleted)
            {
                await pending;
                throw new InvalidOperationException("Pre-execution configuration read completed before its SQL relation wait was observed.");
            }
            if (await command.ExecuteScalarAsync(observation.Token) is int observed)
            {
                Assert.Equal(readerPid, observed);
                return;
            }
            await Task.Delay(25, observation.Token);
        }
    }

    private static IFunctionExecutionConfigurationStore[] CreateStores(NpgsqlDataSource efSource, NpgsqlDataSource source) =>
        [new EfFunctionExecutionConfigurationStore(new ContextFactory(efSource)),
            new NpgsqlFunctionExecutionConfigurationStore(source)];

    private async Task SeedAsync(string schema, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."FunctionConfigurations" (
                "Id" integer PRIMARY KEY, "ProviderType" integer NOT NULL,
                "ConfigurationName" varchar(200) NOT NULL, "Purpose" integer NOT NULL,
                "DefaultExecutionMode" integer NOT NULL, "BaseUrl" varchar(500) NULL,
                "IsEnabled" boolean NOT NULL, "TimeoutSeconds" integer NULL,
                "CacheTtlMinutes" integer NULL, "MaxRetries" integer NULL,
                "ProviderSettings" jsonb NULL, "ParameterSchema" jsonb NULL,
                "Description" varchar(1000) NULL, "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL);
            CREATE TABLE "{schema}"."FunctionCredentials" (
                "Id" integer PRIMARY KEY, "ProviderType" integer NOT NULL,
                "FunctionConfigurationId" integer NULL, "ApiKey" varchar(2000) NULL,
                "BaseUrl" varchar(500) NULL, "Organization" varchar(200) NULL,
                "FunctionAccountGroup" smallint NOT NULL, "IsPrimary" boolean NOT NULL,
                "IsEnabled" boolean NOT NULL, "KeyName" varchar(200) NULL,
                "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
            INSERT INTO "{schema}"."FunctionConfigurations" VALUES
                (1, @exa, 'Alpha', @search, @sync, NULL, TRUE, NULL, NULL, NULL, NULL, NULL, NULL, @created, @updated),
                (2, @exa, 'O''Hare 雪', @search, @sync, 'https://example.test/custom', FALSE, 41, 7, 0,
                    @settings, @parameters, '', @created, @updated),
                (3, 197, 'Unknown', -9, 123, NULL, TRUE, NULL, NULL, NULL, NULL, NULL, NULL, @created, @updated);
            INSERT INTO "{schema}"."FunctionCredentials" VALUES
                (100, @exa, NULL, 'global plaintext', NULL, NULL, 0, TRUE, TRUE, 'A global', @created, @updated),
                (101, @exa, 1, @opaque, 'https://example.test/scoped', 'O''Hare 雪', 32, TRUE, TRUE, 'B scoped', @created, @updated),
                (102, @exa, 2, 'enc:v1:invalid', NULL, NULL, 1, TRUE, TRUE, 'C other', @created, @updated),
                (103, @exa, 1, 'disabled', NULL, NULL, 0, TRUE, FALSE, '0 disabled', @created, @updated),
                (104, @exa, 1, NULL, NULL, NULL, 2, FALSE, TRUE, 'A secondary', @created, @updated),
                (105, @exa, NULL, NULL, NULL, NULL, 0, FALSE, TRUE, NULL, @created, @updated),
                (106, 197, NULL, NULL, NULL, NULL, 32767, FALSE, TRUE, NULL, @created, @updated),
                (107, @exa, 3, NULL, '', '', -7, FALSE, FALSE, 'Z disabled', @created, @updated);
            """;
        command.Parameters.AddWithValue("exa", (int)FunctionProviderType.Exa);
        command.Parameters.AddWithValue("search", (int)FunctionPurpose.Search);
        command.Parameters.AddWithValue("sync", (int)ExecutionMode.Synchronous);
        command.Parameters.AddWithValue("opaque", "enc:v1:opaque=='雪'");
        command.Parameters.AddWithValue("settings", NpgsqlDbType.Jsonb, """{"mode":"deep"}""");
        command.Parameters.AddWithValue("parameters", NpgsqlDbType.Jsonb, """{"example":{"query":"雪"}}""");
        var created = new DateTime(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc);
        command.Parameters.AddWithValue("created", created);
        command.Parameters.AddWithValue("updated", created.AddMinutes(1));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DropAsync(string schema, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync(deadline.Token);
    }

    private string WithSearchPath(string schema) => new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { SearchPath = schema, MaxPoolSize = 1 }.ConnectionString;

    private sealed class ContextFactory(NpgsqlDataSource dataSource) : IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(dataSource).Options;
        public ConduitDbContext CreateDbContext() => new(_options);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
