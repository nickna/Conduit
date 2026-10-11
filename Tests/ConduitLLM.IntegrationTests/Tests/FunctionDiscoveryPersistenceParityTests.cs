using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Functions.Enums;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;

using Npgsql;
using NpgsqlTypes;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class FunctionDiscoveryPersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public async Task EfAndTypedNpgsqlPreserveDiscoveryScalarsFiltersAndDetachedReads()
    {
        var schema = $"function_discovery_{Guid.NewGuid():N}";
        try
        {
            await SeedAsync(schema);
            var connectionString = WithSearchPath(schema);
            await using var source = NpgsqlDataSource.Create(connectionString);
            IFunctionDiscoveryRuntimeStore[] stores =
            [new EfFunctionDiscoveryRuntimeStore(new ContextFactory(connectionString)),
                new NpgsqlFunctionDiscoveryRuntimeStore(source)];
            FunctionPurpose?[] purposes = [null, FunctionPurpose.Search, FunctionPurpose.Answer,
                FunctionPurpose.RAG, (FunctionPurpose)(-9), (FunctionPurpose)999];
            FunctionProviderType?[] providers = [null, FunctionProviderType.Exa, FunctionProviderType.Tavily,
                FunctionProviderType.Mcp, (FunctionProviderType)197, (FunctionProviderType)999];

            var expected = await stores[0].ListEnabledAsync();
            expected.Select(configuration => configuration.Id).Should().BeEquivalentTo([1, 2, 3, 5]);
            foreach (var purpose in purposes)
            foreach (var provider in providers)
            {
                var matching = expected.Where(configuration =>
                    (!purpose.HasValue || configuration.Purpose == purpose.Value) &&
                    (!provider.HasValue || configuration.ProviderType == provider.Value)).ToArray();
                foreach (var store in stores)
                    (await store.ListEnabledAsync(purpose, provider)).Should().BeEquivalentTo(matching);
            }
            foreach (var id in new[] { -1, 0, 1, 2, 3, 4, 5, 999 })
                (await stores[1].GetEnabledByIdAsync(id)).Should()
                    .BeEquivalentTo(await stores[0].GetEnabledByIdAsync(id));

            foreach (var store in stores)
            {
                var populated = (await store.GetEnabledByIdAsync(2))!;
                populated.ConfigurationName.Should().Be("O'Hare 雪");
                populated.BaseUrl.Should().Be("https://example.test/custom");
                populated.TimeoutSeconds.Should().Be(41);
                populated.CacheTtlMinutes.Should().Be(7);
                populated.MaxRetries.Should().Be(0);
                populated.Description.Should().BeEmpty();
                populated.ProviderSettings.Should().Contain("deep");
                populated.ParameterSchema.Should().Contain("example");
                populated.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
                populated.UpdatedAt.Should().Be(populated.CreatedAt.AddMinutes(1));
                populated.CostMappings.Should().BeEmpty();
                populated.Executions.Should().BeEmpty();
                populated.ConfigurationName = "detached mutation";
                (await store.GetEnabledByIdAsync(2))!.ConfigurationName.Should().Be("O'Hare 雪");
                var nullable = (await store.GetEnabledByIdAsync(1))!;
                nullable.BaseUrl.Should().BeNull();
                nullable.TimeoutSeconds.Should().BeNull();
                nullable.CacheTtlMinutes.Should().BeNull();
                nullable.MaxRetries.Should().BeNull();
                nullable.ProviderSettings.Should().BeNull();
                nullable.ParameterSchema.Should().BeNull();
                nullable.Description.Should().BeNull();
                (await store.GetEnabledByIdAsync(4)).Should().BeNull();
            }
        }
        finally { await DropAsync(schema); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOfBlockedReadReleasesTheSingleConnectionSlot(bool typed)
    {
        var schema = $"function_discovery_{Guid.NewGuid():N}";
        try
        {
            await SeedAsync(schema);
            var connectionString = WithSearchPath(schema);
            await using var source = NpgsqlDataSource.Create(connectionString);
            IFunctionDiscoveryRuntimeStore store = typed
                ? new NpgsqlFunctionDiscoveryRuntimeStore(source)
                : new EfFunctionDiscoveryRuntimeStore(new ContextFactory(connectionString));
            (await store.ListEnabledAsync()).Count.Should().Be(4);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.ListEnabledAsync(cancellationToken: cancelled.Token));
            }
            await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                { Pooling = false, ApplicationName = $"{schema}_blocker" }.ConnectionString);
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var command = blocker.CreateCommand())
            {
                command.CommandText = "LOCK TABLE \"FunctionConfigurations\" IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync();
            }
            using var cancelledRead = new CancellationTokenSource();
            var blockedRead = store.GetEnabledByIdAsync(2, cancelledRead.Token);
            try
            {
                await WaitForLockWaitAsync(connectionString, schema, blockedRead);
                cancelledRead.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    blockedRead.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                cancelledRead.Cancel();
                try { await blockedRead.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (cancelledRead.IsCancellationRequested) { }
            }
            await transaction.RollbackAsync();
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            (await store.GetEnabledByIdAsync(2, recovery.Token))!.Id.Should().Be(2);
        }
        finally { await DropAsync(schema); }
    }

    private async Task SeedAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
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
            INSERT INTO "{schema}"."FunctionConfigurations" VALUES
                (1, @exa, 'Alpha', @search, @sync, NULL, TRUE, NULL, NULL, NULL, NULL, NULL, NULL, @created, @updated),
                (2, @tavily, 'O''Hare 雪', @search, @async, 'https://example.test/custom', TRUE, 41, 7, 0,
                    @settings, @schema, '', @created, @updated),
                (3, @exa, 'Answer', @answer, @sync, NULL, TRUE, 0, 0, 3, '"scalar"', '[]', 'description', @created, @updated),
                (4, @mcp, 'Disabled', @rag, @sync, NULL, FALSE, NULL, NULL, NULL, NULL, NULL, NULL, @created, @updated),
                (5, 197, 'Unknown enum values', -9, 123, NULL, TRUE, NULL, NULL, NULL, NULL, NULL, NULL, @created, @updated);
            """;
        command.Parameters.AddWithValue("exa", (int)FunctionProviderType.Exa);
        command.Parameters.AddWithValue("tavily", (int)FunctionProviderType.Tavily);
        command.Parameters.AddWithValue("mcp", (int)FunctionProviderType.Mcp);
        command.Parameters.AddWithValue("search", (int)FunctionPurpose.Search);
        command.Parameters.AddWithValue("answer", (int)FunctionPurpose.Answer);
        command.Parameters.AddWithValue("rag", (int)FunctionPurpose.RAG);
        command.Parameters.AddWithValue("sync", (int)ExecutionMode.Synchronous);
        command.Parameters.AddWithValue("async", (int)ExecutionMode.Asynchronous);
        command.Parameters.AddWithValue("settings", NpgsqlDbType.Jsonb, """{"mode":"deep"}""");
        command.Parameters.AddWithValue("schema", NpgsqlDbType.Jsonb, """{"example":{"query":"雪"}}""");
        var created = new DateTime(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc);
        command.Parameters.AddWithValue("created", created);
        command.Parameters.AddWithValue("updated", created.AddMinutes(1));
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropAsync(string schema)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync(deadline.Token);
    }

    private string WithSearchPath(string schema) =>
        new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { SearchPath = schema, ApplicationName = schema, MaxPoolSize = 1 }.ConnectionString;

    private static async Task WaitForLockWaitAsync(string connectionString, string applicationName, Task read)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Keep each statistics query in its own autocommit transaction so its
        // snapshot can observe the transition into the actual PostgreSQL wait.
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = $"{applicationName}_observer" }.ConnectionString);
        await observer.OpenAsync(deadline.Token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE application_name = @application AND wait_event_type = 'Lock'
                    AND cardinality(pg_blocking_pids(pid)) > 0)
            """;
        command.Parameters.AddWithValue("application", applicationName);
        while (true)
        {
            if (read.IsCompleted)
            {
                await read;
                throw new InvalidOperationException("Discovery read completed before its lock wait was observed.");
            }
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(25, deadline.Token);
        }
    }

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(connectionString).Options;
        public ConduitDbContext CreateDbContext() => new(_options);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
