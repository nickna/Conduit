using ConduitLLM.Configuration.Entities;
using ConduitLLM.Persistence;
using Microsoft.Extensions.Logging;

using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Xunit;
using Xunit.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// Runs Gateway operational metrics queries against EF and typed Npgsql.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed partial class GatewayMetricsPersistenceParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public GatewayMetricsPersistenceParityTests(PostgresLockTestContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
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
            await using var efSource = NpgsqlDataSource.Create(connectionString);
            var efStore = new EfGatewayMetricsStore(new TestDbContextFactory(efSource));
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
                "Cost" numeric(12,8) NOT NULL,
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
                "Amount" numeric(12,8) NOT NULL,
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

    private sealed class TestDbContextFactory(NpgsqlDataSource source) :
        IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(source)
                .Options;

        public ConduitDbContext CreateDbContext() => new(_options);

        public Task<ConduitDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private static readonly DateTime MetricsInstant =
        new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
    private static DateTime MetricsFrom => MetricsInstant.AddMinutes(-5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllSixMetricsReadsPreserveIndependentFieldsGroupsAndTimeBoundaries(bool typedNpgsql)
    {
        var schema = $"gm_fields_{Guid.NewGuid():N}";
        try
        {
            await CreateMetricsMatrixAsync(schema, seed: true);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var store = CreateMetricsStore(source, typedNpgsql);
            foreach (var operation in Enum.GetValues<MetricsRead>())
                await AssertMetricsReadAsync(store, operation, CancellationToken.None);

            (await store.GetGenerationTaskMetricsAsync("video_generation", MetricsFrom)).Should()
                .BeEquivalentTo(new[] { new GatewayGenerationTaskMetric(1, 1, 5) });
            (await store.GetGenerationTaskMetricsAsync("missing", MetricsFrom)).Should().BeEmpty();
            (await store.GetGenerationTaskMetricsAsync("image_generation", MetricsInstant.AddHours(1)))
                .Should().BeEmpty();
            (await store.GetModelUsageAsync(MetricsInstant.AddHours(1))).Should().BeEmpty();
            (await store.GetProviderCostsAsync(MetricsInstant.AddHours(1))).Should().BeEmpty();
            (await store.GetTopVirtualKeySpendAsync(MetricsInstant, MetricsInstant, 10)).Should().BeEmpty();
            (await store.GetTopVirtualKeySpendAsync(MetricsInstant, MetricsFrom, 10)).Should().BeEmpty();

            var first = await store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, 1);
            first.Should().Equal(new GatewayVirtualKeySpendMetric(102, 5m));
            var three = await store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, 3);
            three.Should().BeEquivalentTo(new[]
            {
                new GatewayVirtualKeySpendMetric(102, 5m),
                new GatewayVirtualKeySpendMetric(105, 3m),
                new GatewayVirtualKeySpendMetric(106, 3m)
            });
            three.Select(row => row.TotalSpend).Should().Equal(5m, 3m, 3m);
            // Equal totals have no key tie-breaker. Both tied keys belong in limit=3.
            var two = await store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, 2);
            two.Should().HaveCount(2);
            two[0].Should().Be(new GatewayVirtualKeySpendMetric(102, 5m));
            two[1].TotalSpend.Should().Be(3m);
            two[1].VirtualKeyId.Should().BeOneOf(105, 106);
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllSixMetricsReadsReturnEmptyCollectionsAndZeroActiveCount(bool typedNpgsql)
    {
        var schema = $"gm_empty_{Guid.NewGuid():N}";
        try
        {
            await CreateMetricsMatrixAsync(schema, seed: false);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var store = CreateMetricsStore(source, typedNpgsql);
            (await store.GetModelUsageAsync(MetricsFrom)).Should().BeEmpty();
            (await store.GetProviderCostsAsync(MetricsFrom)).Should().BeEmpty();
            var active = await store.GetActiveEntitiesAsync(MetricsInstant);
            active.ActiveVirtualKeyCount.Should().Be(0);
            active.MappingsByProvider.Should().BeEmpty();
            var queue = await store.GetTaskQueueMetricsAsync();
            queue.QueueDepths.Should().BeEmpty();
            queue.OldestPendingTasks.Should().BeEmpty();
            (await store.GetGenerationTaskMetricsAsync("image_generation", MetricsFrom)).Should().BeEmpty();
            (await store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, 100)).Should().BeEmpty();
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllSixMetricsReadsCancelBeforeExecutionAndInOccupiedPoolThenRecover(bool typedNpgsql)
    {
        var schema = $"gm_pool_{Guid.NewGuid():N}";
        try
        {
            await CreateMetricsMatrixAsync(schema, seed: true);
            foreach (var operation in Enum.GetValues<MetricsRead>())
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var connectionString = MetricsPoolConnectionString(schema, $"gm_pool_{Guid.NewGuid():N}");
                await using var source = NpgsqlDataSource.Create(connectionString);
                var store = CreateMetricsStore(source, typedNpgsql);
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        InvokeMetricsReadAsync(store, operation, cancelled.Token));
                }
                await AssertMetricsPoolReusableAsync(source, deadline.Token);
                await AssertMetricsReadAsync(store, operation, deadline.Token);

                // EF and typed share this exact data source, so this occupies the sole pool slot.
                await using (var occupied = await source.OpenConnectionAsync(deadline.Token))
                {
                    occupied.ProcessID.Should().BeGreaterThan(0);
                    using var cancelled = new CancellationTokenSource();
                    var pending = InvokeMetricsReadAsync(store, operation, cancelled.Token);
                    try
                    {
                        pending.IsCompleted.Should().BeFalse("the only matching pool connection is occupied");
                        cancelled.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                            pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                    finally { await ReapMetricsReadAsync(pending, cancelled); }
                }
                await AssertMetricsPoolReusableAsync(source, deadline.Token);
                await AssertMetricsReadAsync(store, operation, deadline.Token);
            }
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllEightMetricsSqlStatementsCancelAfterObservedRelationWaitThenRecover(bool typedNpgsql)
    {
        var schema = $"gm_sql_{Guid.NewGuid():N}";
        try
        {
            await CreateMetricsMatrixAsync(schema, seed: true);
            var targets = new[]
            {
                new MetricsSqlTarget(MetricsRead.Usage, "RequestLogs"),
                new MetricsSqlTarget(MetricsRead.Cost, "RequestLogs"),
                new MetricsSqlTarget(MetricsRead.Active, "VirtualKeys"),
                new MetricsSqlTarget(MetricsRead.Active, "ModelProviderMappings", Second: true),
                new MetricsSqlTarget(MetricsRead.Queue, "AsyncTasks"),
                new MetricsSqlTarget(MetricsRead.Queue, "AsyncTasks", Second: true),
                new MetricsSqlTarget(MetricsRead.Generation, "AsyncTasks"),
                new MetricsSqlTarget(MetricsRead.Spend, "VirtualKeySpendHistory")
            };
            targets.Should().HaveCount(8);
            foreach (var target in targets)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var application = $"gm_sql_{Guid.NewGuid():N}";
                var connectionString = MetricsPoolConnectionString(schema, application);
                using var gate = new MetricsCommandGate(target);
                var builder = new NpgsqlDataSourceBuilder(connectionString);
                builder.UseLoggerFactory(gate).EnableParameterLogging(false);
                await using var source = builder.Build();
                var store = CreateMetricsStore(source, typedNpgsql);
                var readerPid = await AssertMetricsPoolReusableAsync(source, deadline.Token);
                gate.Arm();
                await CancelMetricsSqlWaitAsync(store, target, gate, schema, connectionString,
                    application, readerPid, deadline.Token);
                await AssertMetricsPoolReusableAsync(source, deadline.Token);
                await AssertMetricsReadAsync(store, target.Operation, deadline.Token);
            }
        }
        finally { await DropSchemaAsync(schema); }
    }

    private enum MetricsRead { Usage, Cost, Active, Queue, Generation, Spend }
    private sealed record MetricsSqlTarget(MetricsRead Operation, string Table, bool Second = false);

    private static IGatewayMetricsStore CreateMetricsStore(NpgsqlDataSource source, bool typedNpgsql) =>
        typedNpgsql ? new NpgsqlGatewayMetricsStore(source) :
            new EfGatewayMetricsStore(new MetricsDataSourceContextFactory(source));

    private string MetricsPoolConnectionString(string schema, string application) =>
        new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
        {
            ApplicationName = application, MaxPoolSize = 1, MinPoolSize = 0,
            Multiplexing = false, LogParameters = false
        }.ConnectionString;

    private static Task InvokeMetricsReadAsync(IGatewayMetricsStore store, MetricsRead operation,
        CancellationToken token) => operation switch
    {
        MetricsRead.Usage => store.GetModelUsageAsync(MetricsFrom, token),
        MetricsRead.Cost => store.GetProviderCostsAsync(MetricsFrom, token),
        MetricsRead.Active => store.GetActiveEntitiesAsync(MetricsInstant, token),
        MetricsRead.Queue => store.GetTaskQueueMetricsAsync(token),
        MetricsRead.Generation => store.GetGenerationTaskMetricsAsync("image_generation", MetricsFrom, token),
        MetricsRead.Spend => store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, 100, token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static async Task AssertMetricsReadAsync(IGatewayMetricsStore store, MetricsRead operation,
        CancellationToken token)
    {
        switch (operation)
        {
            case MetricsRead.Usage:
                (await store.GetModelUsageAsync(MetricsFrom, token)).Should().BeEquivalentTo(new[]
                {
                    new GatewayModelUsageMetric("model-雪", "unknown", 15.5),
                    new GatewayModelUsageMetric("model-雪", "", 0),
                    new GatewayModelUsageMetric("model-b", "Providerβ", 80.5),
                    new GatewayModelUsageMetric("future", "Providerβ", 120.25),
                    new GatewayModelUsageMetric("negative", "negative-provider", -5.25),
                    new GatewayModelUsageMetric("model-case", "Providerβ", 1.75),
                    new GatewayModelUsageMetric("model-case", "providerβ", 2.75)
                });
                break;
            case MetricsRead.Cost:
                (await store.GetProviderCostsAsync(MetricsFrom, token)).Should().BeEquivalentTo(new[]
                {
                    new GatewayProviderCostMetric("unknown", 1m),
                    new GatewayProviderCostMetric("Providerβ", 5.50000001m)
                });
                break;
            case MetricsRead.Active:
                var active = await store.GetActiveEntitiesAsync(MetricsInstant, token);
                active.ActiveVirtualKeyCount.Should().Be(3);
                active.MappingsByProvider.Should().BeEquivalentTo(new[]
                {
                    new GatewayProviderMappingMetric(201, 2),
                    new GatewayProviderMappingMetric(202, 1)
                });
                break;
            case MetricsRead.Queue:
                var queue = await store.GetTaskQueueMetricsAsync(token);
                queue.QueueDepths.Should().BeEquivalentTo(new[]
                {
                    new GatewayTaskQueueMetric("image_generation", 0, 3),
                    new GatewayTaskQueueMetric("image_generation", 1, 2),
                    new GatewayTaskQueueMetric("video_generation", 0, 1),
                    new GatewayTaskQueueMetric("video_generation", 1, 1),
                    new GatewayTaskQueueMetric("通知", 0, 1),
                    new GatewayTaskQueueMetric("processing_only", 1, 1)
                });
                queue.OldestPendingTasks.Should().BeEquivalentTo(new[]
                {
                    new GatewayPendingTaskMetric("image_generation", MetricsFrom.AddTicks(-10)),
                    new GatewayPendingTaskMetric("video_generation", MetricsInstant.AddMinutes(-10)),
                    new GatewayPendingTaskMetric("通知", MetricsInstant.AddMinutes(2))
                });
                queue.OldestPendingTasks.Should().OnlyContain(row =>
                    row.OldestCreatedAt.Kind == DateTimeKind.Utc);
                break;
            case MetricsRead.Generation:
                var generation = await store.GetGenerationTaskMetricsAsync("image_generation", MetricsFrom, token);
                var expectedGeneration = new[]
                {
                    new GatewayGenerationTaskMetric(0, 3, null),
                    new GatewayGenerationTaskMetric(1, 3, 0),
                    new GatewayGenerationTaskMetric(2, 4, 8d / 3d),
                    new GatewayGenerationTaskMetric(3, 1, -2.5),
                    new GatewayGenerationTaskMetric(4, 1, null),
                    new GatewayGenerationTaskMetric(5, 1, null),
                    new GatewayGenerationTaskMetric(6, 1, null),
                    new GatewayGenerationTaskMetric(8, 1, 0.5)
                };
                generation.Select(row => (row.State, row.Count)).Should().BeEquivalentTo(
                    expectedGeneration.Select(row => (row.State, row.Count)));
                foreach (var expected in expectedGeneration)
                {
                    var row = generation.Single(row => row.State == expected.State);
                    row.AverageDurationSeconds.HasValue.Should().Be(expected.AverageDurationSeconds.HasValue);
                    if (expected.AverageDurationSeconds.HasValue)
                        row.AverageDurationSeconds!.Value.Should().BeApproximately(
                            expected.AverageDurationSeconds.Value, 1e-9);
                }
                break;
            case MetricsRead.Spend:
                var spend = await store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, 100, token);
                spend.Should().BeEquivalentTo(new[]
                {
                    new GatewayVirtualKeySpendMetric(102, 5m),
                    new GatewayVirtualKeySpendMetric(105, 3m),
                    new GatewayVirtualKeySpendMetric(106, 3m),
                    new GatewayVirtualKeySpendMetric(101, 2m),
                    new GatewayVirtualKeySpendMetric(103, 0m),
                    new GatewayVirtualKeySpendMetric(104, -2.00000001m)
                });
                spend.Select(row => row.TotalSpend).Should().Equal(5m, 3m, 3m, 2m, 0m, -2.00000001m);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private async Task CreateMetricsMatrixAsync(string schema, bool seed, CancellationToken cancellationToken = default)
    {
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
            { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var efSource = NpgsqlDataSource.Create(WithSearchPath(schema));
        await using var context = new TestDbContextFactory(efSource).CreateDbContext();
        // Use the actual production model, including FK and unique indexes. EnsureCreated would
        // see unrelated shared fixture tables, so execute its creation script in this owned schema.
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = context.Database.GenerateCreateScript();
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!seed) return;

        context.VirtualKeyGroups.Add(new VirtualKeyGroup
            { Id = 1, GroupName = "metrics zero-balance group", Balance = 0m });
        foreach (var id in Enumerable.Range(101, 7))
            context.VirtualKeys.Add(new VirtualKey
            {
                Id = id, VirtualKeyGroupId = 1, KeyHash = $"metrics-{id}", KeyName = $"metrics key {id}",
                IsEnabled = id is not (105 or 106),
                ExpiresAt = id switch
                {
                    102 or 106 => MetricsInstant.AddMinutes(1),
                    103 => MetricsInstant,
                    104 => MetricsInstant.AddTicks(-10),
                    _ => null
                }
            });
        var providers = new[]
        {
            new Provider { Id = 201, ProviderName = "metrics enabled A", IsEnabled = true },
            new Provider { Id = 202, ProviderName = "metrics enabled B", IsEnabled = true },
            new Provider { Id = 203, ProviderName = "metrics disabled", IsEnabled = false }
        };
        context.Providers.AddRange(providers);
        var model = new Model
        {
            Id = 601, Name = "metrics inactive model", IsActive = false,
            Series = new ModelSeries
            {
                Id = 501, Name = "metrics series", Parameters = "{}",
                Author = new ModelAuthor { Id = 401, Name = "metrics author" }
            }
        };
        var providerIds = new[] { 201, 201, 201, 202, 203 };
        for (var index = 0; index < providerIds.Length; index++)
            context.ModelProviderMappings.Add(new ModelProviderMapping
            {
                Id = 301 + index, ModelAlias = $"metrics-alias-{index}", ProviderModelId = $"metrics-model-{index}",
                ProviderId = providerIds[index], Provider = providers.Single(row => row.Id == providerIds[index]),
                IsEnabled = index != 2,
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation
                {
                    Id = 701 + index, Model = model, Identifier = $"metrics-identifier-{index}",
                    Provider = ProviderType.OpenAI, IsEnabled = index != 1
                }
            });

        context.RequestLogs.AddRange(
            MatrixRequest("model-雪", null, 10.5, 0.12345678m, MetricsFrom, null),
            MatrixRequest("model-雪", "unknown", 20.5, 0.87654322m, MetricsInstant.AddMinutes(-1), MetricsInstant.AddMinutes(-1)),
            MatrixRequest("model-雪", "", 0, 0m, MetricsInstant.AddMinutes(-1), null),
            MatrixRequest("model-b", "Providerβ", -1.25, 2.00000001m, MetricsFrom.AddTicks(-10), MetricsFrom),
            MatrixRequest("model-b", "Providerβ", 80.5, 9m, MetricsInstant, MetricsFrom.AddTicks(-10)),
            MatrixRequest("future", "Providerβ", 120.25, 3.5m, MetricsInstant.AddMinutes(1), MetricsInstant.AddMinutes(1)),
            MatrixRequest("excluded", "OpenAI", 999, 7m, MetricsFrom.AddTicks(-10), null),
            MatrixRequest("negative", "negative-provider", -5.25, -1m, MetricsInstant, null),
            MatrixRequest("model-case", "Providerβ", 1.75, 0m, MetricsInstant, null),
            MatrixRequest("model-case", "providerβ", 2.75, 0m, MetricsInstant, null));

        context.AsyncTasks.AddRange(
            MatrixTask("pending-boundary", "image_generation", 0, MetricsFrom),
            MatrixTask("pending-before", "image_generation", 0, MetricsFrom.AddTicks(-10)),
            MatrixTask("pending-future", "image_generation", 0, MetricsInstant.AddMinutes(1)),
            MatrixTask("processing-null", "image_generation", 1, MetricsInstant),
            MatrixTask("processing-zero", "image_generation", 1, MetricsInstant, duration: 0),
            MatrixTask("completed-short", "image_generation", 2, MetricsInstant.AddMinutes(-1), duration: 1.25),
            MatrixTask("completed-long", "image_generation", 2, MetricsInstant.AddMinutes(-2), duration: 2.75),
            MatrixTask("completed-null", "image_generation", 2, MetricsInstant),
            MatrixTask("archived-completed", "image_generation", 2, MetricsInstant, duration: 4, archived: true),
            MatrixTask("failed-negative", "image_generation", 3, MetricsInstant, duration: -2.5),
            MatrixTask("cancelled", "image_generation", 4, MetricsInstant),
            MatrixTask("timed-out", "image_generation", 5, MetricsInstant),
            MatrixTask("indeterminate", "image_generation", 6, MetricsInstant),
            MatrixTask("unknown-state", "image_generation", 8, MetricsInstant, duration: 0.5),
            MatrixTask("archived-pending", "image_generation", 0, MetricsInstant, archived: true),
            MatrixTask("archived-processing", "image_generation", 1, MetricsInstant, archived: true),
            MatrixTask("video-pending", "video_generation", 0, MetricsInstant.AddMinutes(-10)),
            MatrixTask("video-processing", "video_generation", 1, MetricsInstant, duration: 5),
            MatrixTask("unicode-pending", "通知", 0, MetricsInstant.AddMinutes(2)),
            MatrixTask("processing-only", "processing_only", 1, MetricsInstant));

        context.VirtualKeySpendHistory.AddRange(
            MatrixSpend(101, 1.12345678m, MetricsFrom),
            MatrixSpend(101, 0.87654322m, MetricsInstant.AddMinutes(-1)),
            MatrixSpend(102, 5.50000001m, MetricsInstant.AddMinutes(-1)),
            MatrixSpend(102, -0.50000001m, MetricsInstant.AddMinutes(-1)),
            MatrixSpend(103, 0m, MetricsInstant.AddMinutes(-1)),
            MatrixSpend(104, -2.00000001m, MetricsInstant.AddMinutes(-1)),
            MatrixSpend(105, 3m, MetricsInstant.AddMinutes(-1)),
            MatrixSpend(106, 3m, MetricsInstant.AddMinutes(-1)),
            MatrixSpend(101, 99m, MetricsFrom.AddTicks(-10)),
            MatrixSpend(101, 100m, MetricsInstant),
            MatrixSpend(101, 100m, MetricsInstant.AddMinutes(1)));
        await context.SaveChangesAsync(cancellationToken);
    }

    private static RequestLog MatrixRequest(string model, string? provider, double latency, decimal cost,
        DateTime timestamp, DateTime? billed) => new()
    {
        VirtualKeyId = 101, ModelName = model, ProviderType = provider, RequestType = "chat",
        ResponseTimeMs = latency, Cost = cost, Timestamp = timestamp, BilledAtUtc = billed
    };

    private static ConduitLLM.Configuration.Entities.AsyncTask MatrixTask(string id, string type, int state,
        DateTime created, double? duration = null, bool archived = false) => new()
    {
        Id = id, VirtualKeyId = 101, Type = type, State = state, CreatedAt = created, UpdatedAt = created,
        CompletedAt = duration.HasValue ? created.AddSeconds(duration.Value) : null, IsArchived = archived
    };

    private static VirtualKeySpendHistory MatrixSpend(int keyId, decimal amount, DateTime timestamp) => new()
    {
        VirtualKeyId = keyId, Amount = amount, Timestamp = timestamp,
        Date = MetricsInstant.AddYears(-1) // Date is intentionally unrelated to the queried Timestamp.
    };

    private static async Task<int> AssertMetricsPoolReusableAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        (await command.ExecuteScalarAsync(token)).Should().Be(1);
        return connection.ProcessID;
    }

    private static async Task CancelMetricsSqlWaitAsync(IGatewayMetricsStore store, MetricsSqlTarget target,
        MetricsCommandGate gate, string schema, string connectionString, string application, int readerPid,
        CancellationToken token)
    {
        await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = $"{application}_blocker" }.ConnectionString);
        await blocker.OpenAsync(token);
        await using var transaction = await blocker.BeginTransactionAsync(token);
        using var cancelled = new CancellationTokenSource();
        // ExecutingCommand logging is synchronous before Npgsql sends bytes. Keep the test caller free.
        var pending = Task.Run(() => InvokeMetricsReadAsync(store, target.Operation, cancelled.Token));
        try
        {
            var entered = await gate.Entered.WaitAsync(token);
            entered.Ordinal.Should().Be(target.Second ? 2 : 1);
            entered.CompletedBefore.Should().Be(target.Second ? 1 : 0);
            if (target.Operation == MetricsRead.Queue && target.Second)
                entered.Sql.Should().ContainEquivalentOf("min(");
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"LOCK TABLE \"{schema}\".\"{target.Table}\" IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync(token);
            }
            gate.Release();
            await WaitForMetricsRelationWaitAsync(connectionString, application, readerPid, blocker.ProcessID,
                schema, target.Table, pending, token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            gate.Release();
            try { await ReapMetricsReadAsync(pending, cancelled); }
            finally { await transaction.RollbackAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    private static async Task WaitForMetricsRelationWaitAsync(string connectionString, string application,
        int readerPid, int blockerPid, string schema, string table, Task pending, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = $"{application}_observer" }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
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
        command.Parameters.AddWithValue("application", application);
        command.Parameters.AddWithValue("relation", $"\"{schema}\".\"{table}\"");
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        while (true)
        {
            if (pending.IsCompleted)
            {
                await pending;
                throw new InvalidOperationException("Metrics read completed before its exact SQL relation wait was observed.");
            }
            if (await command.ExecuteScalarAsync(token) is int pid)
            {
                pid.Should().Be(readerPid);
                return;
            }
            await Task.Delay(25, token);
        }
    }

    private static async Task ReapMetricsReadAsync(Task pending, CancellationTokenSource cancelled)
    {
        cancelled.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    }

    private sealed class MetricsDataSourceContextFactory(NpgsqlDataSource source) : IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(source).Options;
        public ConduitDbContext CreateDbContext() => new(_options);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed record MetricsGateEntry(string Sql, int Ordinal, int CompletedBefore);

    private sealed class MetricsCommandGate(MetricsSqlTarget target) : ILoggerFactory, ILogger
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource<MetricsGateEntry> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _claimed;
        private int _started;
        private int _completed;
        public Task<MetricsGateEntry> Entered => _entered.Task;

        public void Arm()
        {
            Volatile.Write(ref _started, 0);
            Volatile.Write(ref _completed, 0);
            Volatile.Write(ref _armed, 1);
        }

        public void Release() => _release.Set();
        public ILogger CreateLogger(string categoryName) => categoryName == "Npgsql.Command"
            ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Volatile.Read(ref _armed) == 0 ||
                state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToArray();
            if (values.Any(field => field.Key == "Parameters"))
                throw new InvalidOperationException("Metrics SQL gate must never receive parameter values.");
            var sql = values.FirstOrDefault(field => field.Key == "CommandText").Value as string;
            if (sql is null) return;
            if (eventId.Id == NpgsqlEventId.CommandExecutionCompleted)
            {
                Interlocked.Increment(ref _completed);
                return;
            }
            if (eventId.Id != NpgsqlEventId.ExecutingCommand) return;
            var ordinal = Interlocked.Increment(ref _started);
            if (!sql.Contains($"\"{target.Table}\"", StringComparison.Ordinal)) return;
            if (target.Operation == MetricsRead.Queue &&
                sql.Contains("min(", StringComparison.OrdinalIgnoreCase) != target.Second) return;
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return;
            _entered.TrySetResult(new MetricsGateEntry(sql, ordinal, Volatile.Read(ref _completed)));
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Metrics before-send SQL gate was not released within its bounded lifetime.");
        }

        public void Dispose()
        {
            Release();
            _release.Dispose();
        }
    }


}
