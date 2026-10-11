using System.Text.Json;

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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;
using NpgsqlTypes;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class FunctionPricingPersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    // PostgreSQL timestamps preserve microseconds, not arbitrary DateTime ticks.
    private static DateTime Instant => new(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);

    [Fact]
    public async Task EfAndTypedPricingPreserveEligibilityRankingCanonicalIdsAndAllScalars()
    {
        var schema = $"function_pricing_{Guid.NewGuid():N}";
        var asOf = Instant;
        try
        {
            await SeedAsync(schema, asOf);
            var connectionString = WithSearchPath(schema);
            await using var source = NpgsqlDataSource.Create(connectionString);
            var stores = CreateStores(connectionString, source);
            var expected = (await stores[0].FindEligibleCostAsync(1, asOf))!;
            expected.Id.Should().Be(102); // Eligibility precedes ranking, newest equal-priority eligible cost wins.
            foreach (var store in stores)
            {
                var selected = (await store.FindEligibleCostAsync(1, asOf))!;
                selected.Should().BeEquivalentTo(expected);
                selected.CostName.Should().Be("O'Hare 雪");
                selected.ProviderType.Should().Be(FunctionProviderType.Tavily);
                selected.Purpose.Should().Be(FunctionPurpose.Answer);
                selected.PricingModel.Should().Be(FunctionPricingModel.Hybrid);
                selected.Description.Should().BeEmpty();
                selected.BaseCost.Should().Be(1234567890.12345678m);
                selected.CostPerExecution.Should().Be(0.00000001m);
                selected.CostPerResult.Should().Be(0m);
                selected.CostPerToken.Should().Be(-2.34567890m);
                selected.CostPerMinute.Should().Be(9999999999.99999999m);
                selected.TieredPricing.Should().Contain("雪");
                selected.PricingConfiguration.Should().Be("null"); // JSON null is not SQL NULL.
                selected.IsActive.Should().BeTrue();
                selected.Priority.Should().Be(1);
                selected.EffectiveDate.Should().Be(asOf.AddDays(-1));
                selected.ExpiryDate.Should().Be(asOf.AddDays(1));
                selected.CreatedAt.Should().Be(asOf.AddDays(-3));
                selected.UpdatedAt.Should().Be(asOf.AddDays(-2));
                selected.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
                selected.FunctionMappings.Should().BeEmpty();
                selected.CostName = "detached mutation";
                (await store.FindEligibleCostAsync(1, asOf))!.CostName.Should().Be("O'Hare 雪");
                (await store.FindEligibleCostAsync(3, asOf)).Should().BeNull(); // Only invalid candidates.
                foreach (var missing in new[] { -1, 0, 5, 999 })
                    (await store.FindEligibleCostAsync(missing, asOf)).Should().BeNull();
                var nullable = (await store.FindEligibleCostAsync(4, asOf))!;
                nullable.Id.Should().Be(101); // Shadow points at much higher priority 106; canonical still wins.
                nullable.Purpose.Should().BeNull();
                nullable.Description.Should().BeNull();
                nullable.BaseCost.Should().BeNull();
                nullable.CostPerExecution.Should().BeNull();
                nullable.CostPerResult.Should().BeNull();
                nullable.CostPerToken.Should().BeNull();
                nullable.CostPerMinute.Should().BeNull();
                nullable.TieredPricing.Should().BeNull();
                nullable.PricingConfiguration.Should().BeNull();
                nullable.ExpiryDate.Should().BeNull();
                var tied = (await store.FindEligibleCostAsync(7, asOf))!;
                tied.Id.Should().BeOneOf(102, 110); // Exact ties have no specified winner.
            }

            // The existing service is the live billing selection oracle. These candidates
            // have day-wide windows, so this comparison does not race a date boundary.
            using var memory = new MemoryCache(new MemoryCacheOptions());
            var legacy = new FunctionCostService(
                new FunctionCostRepository(new ContextFactory(connectionString), NullLogger<FunctionCostRepository>.Instance),
                new FunctionCostMappingRepository(new ContextFactory(connectionString), NullLogger<FunctionCostMappingRepository>.Instance),
                memory, NullLogger<FunctionCostService>.Instance);
            var legacyCost = await legacy.GetCostForConfigurationAsync(1);
            legacyCost.Should().BeEquivalentTo(expected, options => options.Excluding(cost => cost.FunctionMappings));
        }
        finally { await DropAsync(schema); }
    }

    [Fact]
    public async Task EffectiveAndExpiryBoundariesAndUnknownEnumsRemainExact()
    {
        var schema = $"function_pricing_{Guid.NewGuid():N}";
        var asOf = Instant;
        try
        {
            await SeedAsync(schema, asOf);
            var connectionString = WithSearchPath(schema);
            await using var source = NpgsqlDataSource.Create(connectionString);
            foreach (var store in CreateStores(connectionString, source))
            {
                (await store.FindEligibleCostAsync(2, asOf.AddTicks(-10))).Should().BeNull();
                var boundary = (await store.FindEligibleCostAsync(2, asOf))!;
                boundary.Id.Should().Be(107);
                ((int)boundary.ProviderType).Should().Be(197);
                ((int)boundary.Purpose!).Should().Be(-9);
                ((int)boundary.PricingModel).Should().Be(123);
                boundary.EffectiveDate.Should().Be(asOf);
                boundary.ExpiryDate.Should().Be(asOf.AddTicks(20));
                boundary.EffectiveDate.Kind.Should().Be(DateTimeKind.Utc);
                boundary.ExpiryDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
                using (var configuration = JsonDocument.Parse(boundary.PricingConfiguration!))
                {
                    configuration.RootElement.GetProperty("quoted").GetString().Should().Be("O'Hare 雪");
                    configuration.RootElement.GetProperty("nested").GetProperty("mode").GetString().Should().Be("deep");
                }
                using (var tiers = JsonDocument.Parse(boundary.TieredPricing!))
                    tiers.RootElement.GetArrayLength().Should().Be(0);
                (await store.FindEligibleCostAsync(2, asOf.AddTicks(10)))!.Id.Should().Be(107);
                (await store.FindEligibleCostAsync(2, asOf.AddTicks(20))).Should().BeNull();
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.FindEligibleCostAsync(1, asOf, cancelled.Token));
                (await store.FindEligibleCostAsync(1, asOf))!.Id.Should().Be(102);
            }
        }
        finally { await DropAsync(schema); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOfSqlAndPoolWaitsReleasesTheSingleConnectionSlot(bool typed)
    {
        var schema = $"function_pricing_{Guid.NewGuid():N}";
        var asOf = Instant;
        try
        {
            await SeedAsync(schema, asOf);
            var connectionString = WithSearchPath(schema);
            await using var source = NpgsqlDataSource.Create(connectionString);
            var store = CreateStores(connectionString, source)[typed ? 1 : 0];
            (await store.FindEligibleCostAsync(1, asOf))!.Id.Should().Be(102);
            await using (var held = typed ? await source.OpenConnectionAsync() : new NpgsqlConnection(connectionString))
            {
                if (!typed) await held.OpenAsync();
                using var cancelled = new CancellationTokenSource();
                var read = store.FindEligibleCostAsync(1, asOf, cancelled.Token);
                try
                {
                    read.IsCompleted.Should().BeFalse();
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally { await DrainCancelledAsync(read, cancelled); }
            }
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            (await store.FindEligibleCostAsync(1, asOf, recovery.Token))!.Id.Should().Be(102);
            await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                { Pooling = false, ApplicationName = $"{schema}_blocker" }.ConnectionString);
            await blocker.OpenAsync(recovery.Token);
            await using var transaction = await blocker.BeginTransactionAsync(recovery.Token);
            await using (var command = blocker.CreateCommand())
            {
                command.CommandText = "LOCK TABLE \"FunctionCostMappings\" IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync(recovery.Token);
            }
            using var cancelledRead = new CancellationTokenSource();
            var blockedRead = store.FindEligibleCostAsync(1, asOf, cancelledRead.Token);
            try
            {
                await WaitForLockWaitAsync(connectionString, schema, blockedRead, recovery.Token);
                cancelledRead.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedRead.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { await DrainCancelledAsync(blockedRead, cancelledRead); }
            await transaction.RollbackAsync(recovery.Token);
            (await store.FindEligibleCostAsync(1, asOf, recovery.Token))!.Id.Should().Be(102);
        }
        finally { await DropAsync(schema); }
    }

    private static async Task DrainCancelledAsync(Task read, CancellationTokenSource cancelled)
    {
        cancelled.Cancel();
        try { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    }

    private static IFunctionPricingRuntimeStore[] CreateStores(string connectionString, NpgsqlDataSource source) =>
        [new EfFunctionPricingRuntimeStore(new ContextFactory(connectionString)), new NpgsqlFunctionPricingRuntimeStore(source)];

    private async Task SeedAsync(string schema, DateTime asOf)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            SET search_path TO "{schema}";
            CREATE TABLE "FunctionConfigurations" (
                "Id" integer PRIMARY KEY, "ProviderType" integer NOT NULL,
                "ConfigurationName" varchar(200) NOT NULL, "Purpose" integer NOT NULL,
                "DefaultExecutionMode" integer NOT NULL, "BaseUrl" varchar(500) NULL,
                "IsEnabled" boolean NOT NULL, "TimeoutSeconds" integer NULL,
                "CacheTtlMinutes" integer NULL, "MaxRetries" integer NULL,
                "ProviderSettings" jsonb NULL, "ParameterSchema" jsonb NULL,
                "Description" varchar(1000) NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
            INSERT INTO "FunctionConfigurations"
                SELECT n, @exa, 'Configuration ' || n, @search, 1, NULL, FALSE,
                    NULL, NULL, NULL, NULL, NULL, NULL, @asOf, @asOf FROM generate_series(1, 7) n;
            CREATE TABLE "FunctionCosts" (
                "Id" integer PRIMARY KEY, "CostName" varchar(200) NOT NULL, "ProviderType" integer NOT NULL,
                "Purpose" integer NULL, "Description" varchar(500) NULL, "BaseCost" decimal(18,8) NULL,
                "PricingModel" integer NOT NULL, "CostPerExecution" decimal(18,8) NULL,
                "CostPerResult" decimal(18,8) NULL, "CostPerToken" decimal(18,8) NULL,
                "CostPerMinute" decimal(18,8) NULL, "TieredPricing" jsonb NULL, "PricingConfiguration" jsonb NULL,
                "IsActive" boolean NOT NULL, "EffectiveDate" timestamptz NOT NULL, "ExpiryDate" timestamptz NULL,
                "Priority" integer NOT NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
            INSERT INTO "FunctionCosts" VALUES
                (101, 'Nullable', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                    TRUE, @asOf - interval '2 days', NULL, 1, @created, @updated),
                (102, 'O''Hare 雪', @tavily, @answer, '', 1234567890.12345678, @hybrid,
                    0.00000001, 0, -2.34567890, 9999999999.99999999, @tiers, 'null',
                    TRUE, @asOf - interval '1 day', @asOf + interval '1 day', 1, @created, @updated),
                (103, 'Inactive', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                    FALSE, @asOf - interval '1 day', NULL, 90, @created, @updated),
                (104, 'Future', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                    TRUE, @asOf + interval '1 day', NULL, 80, @created, @updated),
                (105, 'Expired', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                    TRUE, @asOf - interval '1 day', @asOf, 70, @created, @updated),
                (106, 'Unmapped canonical shadow target', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                    TRUE, @asOf - interval '1 day', NULL, 100, @created, @updated),
                (107, 'Exact boundary and unknown enums', 197, -9, NULL, NULL, 123, NULL, NULL, NULL, NULL, '[]', @settings,
                    TRUE, @asOf, @asOf + interval '2 microseconds', 5, @created, @updated),
                (108, 'Newer lower priority', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                    TRUE, @asOf, NULL, 0, @created, @updated),
                (110, 'Exact rank tie', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                    TRUE, @asOf - interval '1 day', NULL, 1, @created, @updated);
            CREATE TABLE "FunctionCostMappings" (
                "Id" integer PRIMARY KEY, "FunctionConfigurationId" integer NOT NULL REFERENCES "FunctionConfigurations" ("Id"),
                "FunctionCostId" integer NOT NULL REFERENCES "FunctionCosts" ("Id"),
                "FunctionCostId1" integer NULL REFERENCES "FunctionCosts" ("Id"), "IsActive" boolean NOT NULL,
                "CreatedAt" timestamptz NOT NULL, UNIQUE ("FunctionConfigurationId", "FunctionCostId"));
            INSERT INTO "FunctionCostMappings" VALUES
                (1, 1, 101, NULL, TRUE, @asOf), (2, 1, 102, 106, TRUE, @asOf),
                (3, 1, 103, NULL, TRUE, @asOf), (4, 1, 104, NULL, TRUE, @asOf),
                (5, 1, 105, NULL, TRUE, @asOf), (6, 1, 106, NULL, FALSE, @asOf),
                (7, 1, 108, NULL, TRUE, @asOf), (8, 2, 107, NULL, TRUE, @asOf),
                (9, 3, 103, 102, TRUE, @asOf), (10, 3, 104, NULL, TRUE, @asOf),
                (11, 3, 105, NULL, TRUE, @asOf), (12, 4, 101, 106, TRUE, @asOf),
                (13, 4, 106, NULL, FALSE, @asOf), (14, 7, 102, NULL, TRUE, @asOf),
                (15, 7, 110, NULL, TRUE, @asOf);
            """;
        command.Parameters.AddWithValue("asOf", NpgsqlDbType.TimestampTz, asOf);
        command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, asOf.AddDays(-3));
        command.Parameters.AddWithValue("updated", NpgsqlDbType.TimestampTz, asOf.AddDays(-2));
        command.Parameters.AddWithValue("exa", NpgsqlDbType.Integer, (int)FunctionProviderType.Exa);
        command.Parameters.AddWithValue("tavily", NpgsqlDbType.Integer, (int)FunctionProviderType.Tavily);
        command.Parameters.AddWithValue("search", NpgsqlDbType.Integer, (int)FunctionPurpose.Search);
        command.Parameters.AddWithValue("answer", NpgsqlDbType.Integer, (int)FunctionPurpose.Answer);
        command.Parameters.AddWithValue("flat", NpgsqlDbType.Integer, (int)FunctionPricingModel.FlatRate);
        command.Parameters.AddWithValue("hybrid", NpgsqlDbType.Integer, (int)FunctionPricingModel.Hybrid);
        command.Parameters.AddWithValue("tiers", NpgsqlDbType.Jsonb, """{"tiers":[{"cost":0.1,"name":"雪"}],"unknown":[1,null]}""");
        command.Parameters.AddWithValue("settings", NpgsqlDbType.Jsonb, """{"quoted":"O'Hare 雪","nested":{"mode":"deep"}}""");
        await command.ExecuteNonQueryAsync(deadline.Token);
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

    private string WithSearchPath(string schema) => new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { SearchPath = schema, ApplicationName = schema, MaxPoolSize = 1 }.ConnectionString;

    private static async Task WaitForLockWaitAsync(string connectionString, string applicationName, Task read, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = $"{applicationName}_observer" }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @application
                AND wait_event_type = 'Lock' AND cardinality(pg_blocking_pids(pid)) > 0)
            """;
        command.Parameters.AddWithValue("application", applicationName);
        while (true)
        {
            if (read.IsCompleted)
            {
                await read;
                throw new InvalidOperationException("Pricing read completed before its lock wait was observed.");
            }
            if (await command.ExecuteScalarAsync(token) is true) return;
            await Task.Delay(25, token);
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
