using System.Text.Json.Nodes;

using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed partial class FunctionExecutionPersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    private static readonly DateTime Instant = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task InsertsPreserveAllScalarsAndGenerateOnlyEmptyIds()
    {
        await InSchemaAsync(async (connectionString, efSource, source) =>
        {
            foreach (var writer in Writers(efSource, source))
            {
                foreach (var row in new[] { false, true }.Select(Snapshot))
                {
                    var originalId = row.Id;
                    var id = await writer.CreateAsync(row);
                    id.Should().Be(row.Id).And.NotBeEmpty();
                    if (originalId != Guid.Empty) id.Should().Be(originalId);
                    else id.Version.Should().Be(4);
                    await AssertRowAsync(connectionString, row);
                }
            }
        });
    }

    [Fact]
    public async Task UpdatesReplaceSnapshotsAndRestoreFailedVersionsWithoutChangingSuccessfulOverflow()
    {
        await InSchemaAsync(async (connectionString, efSource, source) =>
        {
            foreach (var writer in Writers(efSource, source))
            {
                var original = Snapshot(true);
                await writer.CreateAsync(original);
                var replacement = Snapshot(false);
                replacement.Id = original.Id;
                replacement.Version = original.Version;
                replacement.FunctionConfigurationId = 2;
                replacement.VirtualKeyId = -99;
                replacement.RequestedAt = Instant.AddDays(-2);
                replacement.ExecutionMode = ExecutionMode.Synchronous;
                replacement.State = ExecutionState.Completed;
                replacement.RetryCount = -3;
                (await writer.UpdateAsync(replacement)).Should().BeTrue();
                replacement.Version.Should().Be(8);
                await AssertRowAsync(connectionString, replacement);

                original.StatusMessage = "stale";
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    (await writer.UpdateAsync(original)).Should().BeFalse();
                    original.Version.Should().Be(7);
                    await AssertRowAsync(connectionString, replacement);
                }

                var missing = Snapshot(true);
                (await writer.UpdateAsync(missing)).Should().BeFalse();
                missing.Version.Should().Be(7);
                (await ReadAsync(connectionString, missing.Id)).Should().BeNull();
                var empty = Snapshot(false);
                (await writer.UpdateAsync(empty)).Should().BeFalse();
                empty.Id.Should().NotBeEmpty();
                empty.Id.Version.Should().Be(7);
                empty.Version.Should().Be(7);
                (await ReadAsync(connectionString, empty.Id)).Should().BeNull();

                var overflow = Snapshot(true);
                overflow.Version = int.MaxValue;
                await writer.CreateAsync(overflow);
                (await writer.UpdateAsync(overflow)).Should().BeTrue();
                overflow.Version.Should().Be(int.MinValue);
                await AssertRowAsync(connectionString, overflow);
            }
        });
    }

    [Fact]
    public async Task ConcurrentDetachedSnapshotsHaveExactlyOneWinnerAcrossAdapters()
    {
        await InSchemaAsync(async (connectionString, efSource, source) =>
        {
            var writers = Writers(efSource, source);
            var seed = Snapshot(true);
            await writers[0].CreateAsync(seed);
            var contenders = writers.Select((_, index) =>
            {
                var row = Snapshot(true);
                row.Id = seed.Id;
                row.StatusMessage = $"winner {index}";
                return row;
            }).ToArray();
            var results = await Task.WhenAll(writers.Select((writer, index) => writer.UpdateAsync(contenders[index])));
            results.Count(result => result).Should().Be(1);
            var winner = contenders[Array.IndexOf(results, true)];
            for (var index = 0; index < contenders.Length; index++)
            {
                contenders[index].Version.Should().Be(results[index] ? 8 : 7);
                if (!results[index])
                {
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        (await writers[index].UpdateAsync(contenders[index])).Should().BeFalse();
                        contenders[index].Version.Should().Be(7);
                        await AssertRowAsync(connectionString, winner);
                    }
                }
            }
            await AssertRowAsync(connectionString, winner);
        }, maxPoolSize: 3);
    }

    [Fact]
    public async Task DatabaseFailuresPropagateWithoutInsertsOrPartialUpdates()
    {
        await InSchemaAsync(async (connectionString, efSource, source) =>
        {
            foreach (var writer in Writers(efSource, source))
            {
                var seed = Snapshot(true);
                await writer.CreateAsync(seed);
                var duplicate = Snapshot(true);
                duplicate.Id = seed.Id;
                await AssertSqlStateAsync(() => writer.CreateAsync(duplicate), PostgresErrorCodes.UniqueViolation);
                await AssertRowAsync(connectionString, seed);
                var invalidForeignKey = Snapshot(false);
                invalidForeignKey.FunctionConfigurationId = int.MinValue;
                await AssertSqlStateAsync(() => writer.CreateAsync(invalidForeignKey), PostgresErrorCodes.ForeignKeyViolation);
                invalidForeignKey.Id.Should().NotBeEmpty();
                (await ReadAsync(connectionString, invalidForeignKey.Id)).Should().BeNull();
                var invalidJson = Snapshot(true);
                invalidJson.Id = seed.Id;
                invalidJson.ResponseJson = "invalid json";
                await AssertSqlStateAsync(() => writer.UpdateAsync(invalidJson), PostgresErrorCodes.InvalidTextRepresentation);
                invalidJson.Version.Should().Be(7);
                await AssertRowAsync(connectionString, seed);
            }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationOfPoolAndObservedSqlWaitsRestoresVersionsAndReleasesSlots(int adapter)
    {
        await InSchemaAsync(async (connectionString, efSource, source) =>
        {
            var writer = Writers(efSource, source)[adapter];
            var seed = Snapshot(true);
            await writer.CreateAsync(seed);
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var cancelledInsertIds = new List<Guid>();
            await using (var held = await source.OpenConnectionAsync(recovery.Token))
            {
                foreach (var create in new[] { true, false })
                {
                    var row = Snapshot(!create);
                    if (!create) row.Id = seed.Id;
                    using var cancelled = new CancellationTokenSource();
                    Task write = create ? writer.CreateAsync(row, cancelled.Token) : writer.UpdateAsync(row, cancelled.Token);
                    try
                    {
                        write.IsCompleted.Should().BeFalse();
                        row.Id.Should().NotBeEmpty();
                        row.Version.Should().Be(create ? 7 : 8);
                        if (create) cancelledInsertIds.Add(row.Id);
                        cancelled.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                    finally { await DrainAsync(write, cancelled); }
                    row.Version.Should().Be(7);
                }
            }
            foreach (var id in cancelledInsertIds) (await ReadAsync(connectionString, id)).Should().BeNull();
            await AssertRowAsync(connectionString, seed);
            var retry = Snapshot(false);
            await writer.CreateAsync(retry, recovery.Token);

            await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                { Pooling = false, ApplicationName = "execution_blocker" }.ConnectionString);
            await blocker.OpenAsync(recovery.Token);
            await using var transaction = await blocker.BeginTransactionAsync(recovery.Token);
            await using (var command = blocker.CreateCommand())
            {
                command.CommandText = "LOCK TABLE \"FunctionExecutions\" IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync(recovery.Token);
            }
            var update = Snapshot(true);
            update.Id = seed.Id;
            using var sqlCancelled = new CancellationTokenSource();
            var blocked = writer.UpdateAsync(update, sqlCancelled.Token);
            try
            {
                await WaitForLockWaitAsync(connectionString, blocked, recovery.Token);
                sqlCancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
                update.Version.Should().Be(7);
            }
            finally { await DrainAsync(blocked, sqlCancelled); }
            await transaction.RollbackAsync(recovery.Token);
            await AssertRowAsync(connectionString, seed);
            (await writer.UpdateAsync(seed, recovery.Token)).Should().BeTrue();
            await AssertRowAsync(connectionString, seed);
        }, singleAdapter: true);
    }

    [Fact]
    public async Task PrecancelledCallsRetainGeneratedIdsAndRestoreVersionsAndScalarPortsRejectGraphs()
    {
        await InSchemaAsync(async (connectionString, efSource, source) =>
        {
            var writers = Writers(efSource, source);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            foreach (var writer in writers)
            {
                var insert = Snapshot(false);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.CreateAsync(insert, cancelled.Token));
                insert.Id.Should().NotBeEmpty();
                insert.Version.Should().Be(7);
                (await ReadAsync(connectionString, insert.Id)).Should().BeNull();
                var update = Snapshot(false);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.UpdateAsync(update, cancelled.Token));
                update.Id.Version.Should().Be(7);
                update.Version.Should().Be(7);
                (await ReadAsync(connectionString, update.Id)).Should().BeNull();
            }
            foreach (var writer in writers.Skip(1))
            {
                await Assert.ThrowsAsync<ArgumentNullException>(() => writer.CreateAsync(null!));
                await Assert.ThrowsAsync<ArgumentNullException>(() => writer.UpdateAsync(null!));
                var graph = Snapshot(true);
                graph.FunctionConfiguration = new FunctionConfiguration { Id = 1, ConfigurationName = "Navigation" };
                await Assert.ThrowsAsync<ArgumentException>(() => writer.CreateAsync(graph));
                graph.FunctionConfiguration = null;
                graph.AuditEvents.Add(new FunctionExecutionAudit());
                await Assert.ThrowsAsync<ArgumentException>(() => writer.UpdateAsync(graph));
                graph.Version.Should().Be(7);
            }
        });
    }

    private async Task InSchemaAsync(Func<string, NpgsqlDataSource, NpgsqlDataSource, Task> test, int maxPoolSize = 1, bool singleAdapter = false)
    {
        var schema = $"function_execution_{Guid.NewGuid():N}";
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString))
            {
                await connection.OpenAsync(deadline.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = $$"""
                    CREATE SCHEMA "{{schema}}";
                    SET search_path TO "{{schema}}";
                    CREATE TABLE "FunctionConfigurations" (
                        "Id" integer PRIMARY KEY, "ProviderType" integer NOT NULL, "ConfigurationName" text NOT NULL,
                        "Purpose" integer NOT NULL, "DefaultExecutionMode" integer NOT NULL, "BaseUrl" text NULL,
                        "IsEnabled" boolean NOT NULL, "TimeoutSeconds" integer NULL, "CacheTtlMinutes" integer NULL,
                        "MaxRetries" integer NULL, "ProviderSettings" jsonb NULL, "ParameterSchema" jsonb NULL,
                        "Description" text NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
                    INSERT INTO "FunctionConfigurations" VALUES
                        (1, 197, 'O''Hare 雪', -9, 123, 'https://example.test', true, -1, 0, 5,
                         '{"unknown":[null,true,1]}', '[]', '', '2026-10-11T09:00:00Z', '2026-10-11T09:00:01Z'),
                        (2, 0, 'disabled', 0, 0, NULL, false, NULL, NULL, NULL,
                         'null', NULL, NULL, '2026-10-11T09:00:00Z', '2026-10-11T09:00:01Z');
                    CREATE TABLE "FunctionExecutions" (
                        "Id" uuid PRIMARY KEY, "FunctionConfigurationId" integer NOT NULL REFERENCES "FunctionConfigurations" ("Id") ON DELETE CASCADE,
                        "VirtualKeyId" integer NOT NULL, "ExecutionMode" integer NOT NULL, "State" integer NOT NULL,
                        "RequestedAt" timestamptz NOT NULL, "StartedAt" timestamptz NULL, "CompletedAt" timestamptz NULL,
                        "Duration" interval NULL, "RequestJson" jsonb NULL, "ResponseJson" jsonb NULL,
                        "ErrorMessage" varchar(2000) NULL, "EstimatedCost" numeric(18,8) NULL, "ActualCost" numeric(18,8) NULL,
                        "CostCalculationDetails" jsonb NULL, "RetryCount" integer NOT NULL, "NextRetryAt" timestamptz NULL,
                        "LeasedBy" varchar(100) NULL, "LeaseExpiryTime" timestamptz NULL, "Version" integer NOT NULL,
                        "WebhookUrl" varchar(1000) NULL, "WebhookDelivered" boolean NOT NULL,
                        "ProgressPercentage" integer NULL, "StatusMessage" varchar(500) NULL);
                    """;
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
                { SearchPath = schema, ApplicationName = schema, MaxPoolSize = maxPoolSize }.ConnectionString;
            await using var source = NpgsqlDataSource.Create(connectionString);
            if (singleAdapter)
                await test(connectionString, source, source);
            else
            {
                await using var efSource = NpgsqlDataSource.Create(connectionString);
                await test(connectionString, efSource, source);
            }
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString);
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await command.ExecuteNonQueryAsync(deadline.Token);
        }
    }

    private static IFunctionExecutionRuntimeWriter[] Writers(NpgsqlDataSource efSource, NpgsqlDataSource source)
    {
        var factory = new ContextFactory(efSource);
        return [new LegacyWriter(new FunctionExecutionRepository(factory, NullLogger<FunctionExecutionRepository>.Instance)),
            new EfFunctionExecutionRuntimeWriter(factory), new NpgsqlFunctionExecutionRuntimeWriter(source)];
    }

    private static FunctionExecution Snapshot(bool populated) => new()
    {
        Id = populated ? Guid.NewGuid() : Guid.Empty, FunctionConfigurationId = 1, VirtualKeyId = 42,
        ExecutionMode = (ExecutionMode)123, State = (ExecutionState)(-9), RequestedAt = Instant,
        StartedAt = populated ? Instant.AddSeconds(1) : null, CompletedAt = populated ? Instant.AddSeconds(3) : null,
        Duration = populated ? TimeSpan.FromTicks(-12345670) : null,
        RequestJson = populated ? """{"quoted":"O'Hare 雪","nested":[1,null,false]}""" : null,
        ResponseJson = populated ? "null" : null, ErrorMessage = populated ? "O'Hare 雪" : null,
        EstimatedCost = populated ? 1234567890.12345678m : null, ActualCost = populated ? -0.00000001m : null,
        CostCalculationDetails = populated ? "[]" : null, RetryCount = 4, NextRetryAt = populated ? Instant.AddDays(1) : null,
        LeasedBy = populated ? "worker 雪" : null, LeaseExpiryTime = populated ? Instant.AddMinutes(5) : null,
        Version = 7, WebhookUrl = populated ? "https://example.test/O'Hare" : null, WebhookDelivered = populated,
        ProgressPercentage = populated ? -1 : null, StatusMessage = populated ? "" : null
    };

    private static async Task<FunctionExecution?> ReadAsync(string connectionString, Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var context = new ContextFactory(source).CreateDbContext();
        return await context.FunctionExecutions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, deadline.Token);
    }

    private static async Task AssertRowAsync(string connectionString, FunctionExecution expected)
    {
        var actual = (await ReadAsync(connectionString, expected.Id))!;
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected, options => options.Excluding(row => row.FunctionConfiguration)
            .Excluding(row => row.AuditEvents).Excluding(row => row.RequestJson).Excluding(row => row.ResponseJson)
            .Excluding(row => row.CostCalculationDetails));
        foreach (var pair in new[] { (actual.RequestJson, expected.RequestJson), (actual.ResponseJson, expected.ResponseJson),
                     (actual.CostCalculationDetails, expected.CostCalculationDetails) })
        {
            (pair.Item1 is null).Should().Be(pair.Item2 is null);
            if (pair.Item1 is not null) JsonNode.DeepEquals(JsonNode.Parse(pair.Item1), JsonNode.Parse(pair.Item2!)).Should().BeTrue();
        }
        actual.RequestedAt.Kind.Should().Be(DateTimeKind.Utc);
        actual.FunctionConfiguration.Should().BeNull();
        actual.AuditEvents.Should().BeEmpty();
    }

    private static async Task AssertSqlStateAsync(Func<Task> write, string state)
    {
        var exception = await Record.ExceptionAsync(write);
        exception.Should().NotBeNull();
        exception!.GetBaseException().Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(state);
    }

    private static async Task DrainAsync(Task write, CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        try { await write.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private static async Task WaitForLockWaitAsync(string connectionString, Task write, CancellationToken token)
    {
        var applicationName = new NpgsqlConnectionStringBuilder(connectionString).ApplicationName;
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = "execution_observer" }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @application
                AND wait_event_type = 'Lock' AND cardinality(pg_blocking_pids(pid)) > 0)
            """;
        command.Parameters.AddWithValue("application", applicationName!);
        while (true)
        {
            if (write.IsCompleted) { await write; throw new InvalidOperationException("Write completed before observed lock wait."); }
            if (await command.ExecuteScalarAsync(token) is true) return;
            await Task.Delay(25, token);
        }
    }

    // Match the production EF DbContextFactory: constructing a context does not inspect its token.
    private sealed class ContextFactory(NpgsqlDataSource dataSource) : IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(dataSource).Options;
        public ConduitDbContext CreateDbContext() => new(_options);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class LegacyWriter(FunctionExecutionRepository repository) : IFunctionExecutionRuntimeWriter
    {
        public Task<Guid> CreateAsync(FunctionExecution row, CancellationToken token = default) => repository.CreateAsync(row, token);
        public Task<bool> UpdateAsync(FunctionExecution row, CancellationToken token = default) => repository.UpdateAsync(row, token);
    }
}
