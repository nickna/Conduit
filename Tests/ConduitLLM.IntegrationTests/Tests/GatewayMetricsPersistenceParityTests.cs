using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// Runs Gateway operational metrics queries against EF and typed Npgsql.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class GatewayMetricsPersistenceParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;

    public GatewayMetricsPersistenceParityTests(PostgresLockTestContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EfAndTypedNpgsqlReturnTheSameMetricsAggregates()
    {
        var schema = $"gateway_metrics_{Guid.NewGuid():N}";
        var now = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            await CreateSchemaAndSeedAsync(schema, now);
            var connectionString = WithSearchPath(schema);
            var efStore = new EfGatewayMetricsStore(new TestDbContextFactory(connectionString));
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            var npgsqlStore = new NpgsqlGatewayMetricsStore(dataSource);

            await AssertEquivalentAsync(efStore, npgsqlStore, now);
        }
        finally
        {
            await DropSchemaAsync(schema);
        }
    }

    private static async Task AssertEquivalentAsync(
        IGatewayMetricsStore expected,
        IGatewayMetricsStore actual,
        DateTime now)
    {
        var fiveMinutesAgo = now.AddMinutes(-5);
        (await actual.GetModelUsageAsync(fiveMinutesAgo)).Should()
            .BeEquivalentTo(await expected.GetModelUsageAsync(fiveMinutesAgo));
        (await actual.GetProviderCostsAsync(fiveMinutesAgo)).Should()
            .BeEquivalentTo(await expected.GetProviderCostsAsync(fiveMinutesAgo));
        (await actual.GetActiveEntitiesAsync(now)).Should()
            .BeEquivalentTo(await expected.GetActiveEntitiesAsync(now));
        (await actual.GetTaskQueueMetricsAsync()).Should()
            .BeEquivalentTo(await expected.GetTaskQueueMetricsAsync());
        (await actual.GetGenerationTaskMetricsAsync("image_generation", now.AddHours(-1))).Should()
            .BeEquivalentTo(await expected.GetGenerationTaskMetricsAsync(
                "image_generation",
                now.AddHours(-1)));
        (await actual.GetTopVirtualKeySpendAsync(now.AddMinutes(-1), now, 100)).Should()
            .BeEquivalentTo(await expected.GetTopVirtualKeySpendAsync(
                now.AddMinutes(-1),
                now,
                100));
    }

    private async Task CreateSchemaAndSeedAsync(string schema, DateTime now)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."RequestLogs" (
                "ModelName" varchar(100) NOT NULL,
                "ProviderType" varchar(50) NULL,
                "ResponseTimeMs" double precision NOT NULL,
                "Cost" numeric(10,6) NOT NULL,
                "BilledAtUtc" timestamp with time zone NULL,
                "Timestamp" timestamp with time zone NOT NULL
            );
            CREATE TABLE "{schema}"."VirtualKeys" (
                "Id" integer PRIMARY KEY,
                "IsEnabled" boolean NOT NULL,
                "ExpiresAt" timestamp with time zone NULL
            );
            CREATE TABLE "{schema}"."Providers" (
                "Id" integer PRIMARY KEY,
                "IsEnabled" boolean NOT NULL
            );
            CREATE TABLE "{schema}"."ModelProviderMappings" (
                "Id" integer PRIMARY KEY,
                "ProviderId" integer NOT NULL,
                "IsEnabled" boolean NOT NULL
            );
            CREATE TABLE "{schema}"."AsyncTasks" (
                "Id" varchar(50) PRIMARY KEY,
                "Type" varchar(100) NOT NULL,
                "State" integer NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "CompletedAt" timestamp with time zone NULL,
                "IsArchived" boolean NOT NULL
            );
            CREATE TABLE "{schema}"."VirtualKeySpendHistory" (
                "Id" integer PRIMARY KEY,
                "VirtualKeyId" integer NOT NULL,
                "Amount" numeric(10,6) NOT NULL,
                "Timestamp" timestamp with time zone NOT NULL
            );

            INSERT INTO "{schema}"."RequestLogs" VALUES
                ('model-a', 'OpenAI', 100, 1.25, @recent, @recent),
                ('model-a', 'OpenAI', 300, 0.75, NULL, @recent),
                ('model-old', NULL, 500, 9.00, @old, @old);
            INSERT INTO "{schema}"."VirtualKeys" VALUES
                (1, TRUE, NULL), (2, TRUE, @future), (3, TRUE, @old), (4, FALSE, NULL);
            INSERT INTO "{schema}"."Providers" VALUES (10, TRUE), (11, FALSE);
            INSERT INTO "{schema}"."ModelProviderMappings" VALUES
                (20, 10, TRUE), (21, 10, FALSE), (22, 11, TRUE);
            INSERT INTO "{schema}"."AsyncTasks" VALUES
                ('pending-1', 'image_generation', 0, @pendingOld, NULL, FALSE),
                ('pending-2', 'image_generation', 0, @recent, NULL, FALSE),
                ('processing', 'image_generation', 1, @recent, NULL, FALSE),
                ('completed', 'image_generation', 2, @completedStart, @completedEnd, FALSE),
                ('archived', 'image_generation', 0, @old, NULL, TRUE),
                ('video', 'video_generation', 1, @recent, NULL, FALSE);
            INSERT INTO "{schema}"."VirtualKeySpendHistory" VALUES
                (1, 1, 1.25, @recent), (2, 1, 0.75, @recent),
                (3, 2, 3.50, @recent), (4, 3, 9.00, @old);
            """;
        command.Parameters.AddWithValue("recent", now.AddMinutes(-1));
        command.Parameters.AddWithValue("old", now.AddHours(-2));
        command.Parameters.AddWithValue("future", now.AddHours(1));
        command.Parameters.AddWithValue("pendingOld", now.AddMinutes(-30));
        command.Parameters.AddWithValue("completedStart", now.AddMinutes(-20));
        command.Parameters.AddWithValue("completedEnd", now.AddMinutes(-18));
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    private string WithSearchPath(string schema) =>
        new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = schema
        }.ConnectionString;

    private sealed class TestDbContextFactory(string connectionString) :
        IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(connectionString)
                .Options;

        public ConduitDbContext CreateDbContext() => new(_options);

        public Task<ConduitDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
