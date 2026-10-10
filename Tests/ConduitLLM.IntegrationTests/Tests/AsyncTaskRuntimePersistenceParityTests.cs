using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// Runs the Gateway async-task runtime contract against EF and typed Npgsql.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class AsyncTaskRuntimePersistenceParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;

    public AsyncTaskRuntimePersistenceParityTests(PostgresLockTestContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EfAndTypedNpgsqlImplementTheSameRuntimeContract()
    {
        var efSchema = $"task_runtime_ef_{Guid.NewGuid():N}";
        var npgsqlSchema = $"task_runtime_np_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(efSchema);
            await CreateSchemaAsync(npgsqlSchema);

            var efRepository = new AsyncTaskRepository(
                new TestDbContextFactory(WithSearchPath(efSchema)),
                NullLogger<AsyncTaskRepository>.Instance);
            await ExerciseContractAsync(new EfAsyncTaskRuntimeStore(efRepository));

            await using var dataSource = NpgsqlDataSource.Create(WithSearchPath(npgsqlSchema));
            await ExerciseContractAsync(new NpgsqlAsyncTaskRuntimeStore(dataSource));
        }
        finally
        {
            await DropSchemaAsync(efSchema);
            await DropSchemaAsync(npgsqlSchema);
        }
    }

    private static async Task ExerciseContractAsync(IAsyncTaskRuntimeStore store)
    {
        (await store.GetByIdAsync("missing")).Should().BeNull();
        (await store.DeleteAsync("missing")).Should().BeFalse();

        var pending = NewTask("pending");
        (await store.CreateAsync(pending)).Should().Be("pending");
        var read = await store.GetByIdAsync("pending");
        read.Should().BeEquivalentTo(pending, options => options
            .Excluding(task => task.CreatedAt)
            .Excluding(task => task.UpdatedAt));
        read!.CreatedAt.Should().BeCloseTo(pending.CreatedAt, TimeSpan.FromMilliseconds(1));

        read.Progress = 25;
        read.ProgressMessage = "working";
        read.Payload = "{\"prompt\":\"test\"}";
        (await store.UpdateAsync(read)).Should().BeTrue();
        (await store.GetByIdAsync("pending"))!.Progress.Should().Be(25);

        var page = await store.GetByStateAsync(0, 0, 500);
        page.TotalCount.Should().Be(1);
        page.Tasks.Should().ContainSingle(task => task.Id == "pending");

        (await store.TryClaimTaskAsync("pending", "worker-a", TimeSpan.FromMinutes(2)))
            .Should().Be(AsyncTaskRuntimeClaimStatus.Claimed);
        // A progress snapshot read before the durable claim must not overwrite its
        // version, state, or lease when that delayed update finally arrives.
        read.Progress = 99;
        (await store.UpdateAsync(read)).Should().BeFalse();
        var afterStaleUpdate = await store.GetByIdAsync("pending");
        afterStaleUpdate!.State.Should().Be(1);
        afterStaleUpdate.Progress.Should().Be(25);
        afterStaleUpdate.LeasedBy.Should().Be("worker-a");
        (await store.TryClaimTaskAsync("pending", "worker-b", TimeSpan.FromMinutes(2)))
            .Should().Be(AsyncTaskRuntimeClaimStatus.AlreadyClaimed);
        (await store.ExtendLeaseAsync("pending", "worker-b", TimeSpan.FromMinutes(3)))
            .Should().BeFalse();
        (await store.ExtendLeaseAsync("pending", "worker-a", TimeSpan.FromMinutes(3)))
            .Should().BeTrue();
        (await store.MarkProviderInvocationStartedAsync("pending", "worker-a"))
            .Should().BeTrue();
        (await store.MarkProviderInvocationCompletedAsync("pending", "worker-a", "provider-42"))
            .Should().BeTrue();
        var claimed = await store.GetByIdAsync("pending");
        claimed!.State.Should().Be(1);
        claimed.ProviderInvocationStartedAt.Should().NotBeNull();
        claimed.ProviderInvocationCompletedAt.Should().NotBeNull();
        claimed.ProviderOperationId.Should().Be("provider-42");

        await store.CreateAsync(NewTask("concurrent-claim"));
        var claimResults = await Task.WhenAll(
            store.TryClaimTaskAsync("concurrent-claim", "worker-a", TimeSpan.FromMinutes(1)),
            store.TryClaimTaskAsync("concurrent-claim", "worker-b", TimeSpan.FromMinutes(1)));
        claimResults.Should().ContainSingle(result => result == AsyncTaskRuntimeClaimStatus.Claimed);
        claimResults.Should().ContainSingle(result => result == AsyncTaskRuntimeClaimStatus.AlreadyClaimed);

        await store.CreateAsync(NewTask("terminal", state: 2));
        (await store.TryClaimTaskAsync("terminal", "worker", TimeSpan.FromMinutes(1)))
            .Should().Be(AsyncTaskRuntimeClaimStatus.Terminal);

        await store.CreateAsync(NewTask("indeterminate", state: 6));
        var preparation = await store.PrepareIndeterminateTaskRetryAsync(
            "indeterminate",
            "dispatch-1",
            "operator approved");
        preparation.Status.Should().Be(AsyncTaskRuntimeRetryStatus.Prepared);
        preparation.Task!.State.Should().Be(0);
        preparation.Task.RetryCount.Should().Be(1);
        (await store.PrepareIndeterminateTaskRetryAsync(
            "indeterminate",
            "dispatch-1",
            "redelivery")).Status.Should().Be(AsyncTaskRuntimeRetryStatus.AlreadyPrepared);

        await store.CreateAsync(NewTask("unsupported", state: 6, type: "audio_generation"));
        (await store.PrepareIndeterminateTaskRetryAsync(
            "unsupported",
            "dispatch-unsupported",
            "operator approved")).Status.Should().Be(
                AsyncTaskRuntimeRetryStatus.UnsupportedTaskType);
        var exhausted = NewTask("retry-exhausted", state: 6);
        exhausted.RetryCount = exhausted.MaxRetries;
        await store.CreateAsync(exhausted);
        (await store.PrepareIndeterminateTaskRetryAsync(
            "retry-exhausted",
            "dispatch-exhausted",
            "operator approved")).Status.Should().Be(
                AsyncTaskRuntimeRetryStatus.RetryLimitExceeded);

        await store.CreateAsync(NewTask("fail-indeterminate", state: 6));
        (await store.FailIndeterminateTaskWithoutChargeAsync(
            "fail-indeterminate",
            "confirmed failure",
            "provider-99")).Should().BeTrue();
        var failed = await store.GetByIdAsync("fail-indeterminate");
        failed!.State.Should().Be(3);
        failed.IsRetryable.Should().BeFalse();
        failed.ProviderOperationId.Should().Be("provider-99");

        var completedOld = NewTask("completed-old", state: 2);
        completedOld.CreatedAt = DateTime.UtcNow.AddDays(-10);
        completedOld.UpdatedAt = completedOld.CreatedAt;
        completedOld.CompletedAt = completedOld.CreatedAt;
        await store.CreateAsync(completedOld);
        (await store.ArchiveOldTasksAsync(TimeSpan.FromDays(1), TimeSpan.FromDays(7)))
            .Should().Be(1);
        (await store.GetByIdAsync("completed-old"))!.IsArchived.Should().BeTrue();

        var cleanup = NewTask("cleanup", state: 2);
        cleanup.IsArchived = true;
        cleanup.ArchivedAt = DateTime.UtcNow.AddDays(-40);
        await store.CreateAsync(cleanup);
        (await store.GetTaskIdsForCleanupAsync(TimeSpan.FromDays(30), 10))
            .Should().ContainSingle().Which.Should().Be("cleanup");
        (await store.BulkDeleteAsync(["cleanup"])).Should().Be(1);
        (await store.GetByIdAsync("cleanup")).Should().BeNull();
        (await store.BulkDeleteAsync([])).Should().Be(0);

        (await store.TryClaimTaskAsync("missing", "worker", TimeSpan.FromMinutes(1)))
            .Should().Be(AsyncTaskRuntimeClaimStatus.Missing);
    }

    private static AsyncTaskRuntimeRecord NewTask(
        string id,
        int state = 0,
        string type = "image_generation")
    {
        var now = DateTime.UtcNow;
        return new AsyncTaskRuntimeRecord
        {
            Id = id,
            Type = type,
            State = state,
            CreatedAt = now,
            UpdatedAt = now,
            VirtualKeyId = 1,
            Metadata = "{\"virtualKeyId\":1,\"model\":\"test-model\"}",
            MaxRetries = 3,
            IsRetryable = true
        };
    }

    private async Task CreateSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."AsyncTasks" (
                "Id" character varying(50) PRIMARY KEY,
                "Type" character varying(100) NOT NULL,
                "State" integer NOT NULL,
                "Payload" text NULL,
                "Progress" integer NOT NULL,
                "ProgressMessage" character varying(500) NULL,
                "Result" text NULL,
                "Error" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "CompletedAt" timestamp with time zone NULL,
                "VirtualKeyId" integer NOT NULL,
                "Metadata" text NULL,
                "IsArchived" boolean NOT NULL,
                "ArchivedAt" timestamp with time zone NULL,
                "LeasedBy" character varying(100) NULL,
                "LeaseExpiryTime" timestamp with time zone NULL,
                "ProviderInvocationStartedAt" timestamp with time zone NULL,
                "ProviderInvocationCompletedAt" timestamp with time zone NULL,
                "ProviderOperationId" character varying(200) NULL,
                "RetryDispatchId" character varying(64) NULL,
                "Version" integer NOT NULL,
                "RetryCount" integer NOT NULL,
                "MaxRetries" integer NOT NULL,
                "IsRetryable" boolean NOT NULL,
                "NextRetryAt" timestamp with time zone NULL
            );
            """;
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

    private string WithSearchPath(string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = schema
        };
        return builder.ConnectionString;
    }

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
