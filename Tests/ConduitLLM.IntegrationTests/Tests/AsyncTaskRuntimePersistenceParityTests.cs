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
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
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
        await WithOwnedSourcesAsync(efSchema, npgsqlSchema, async (efSource, typedSource) =>
        {
            var efRepository = new AsyncTaskRepository(
                new TestDbContextFactory(efSource),
                NullLogger<AsyncTaskRepository>.Instance);
            await ExerciseContractAsync(new EfAsyncTaskRuntimeStore(efRepository));
            await ExerciseContractAsync(new NpgsqlAsyncTaskRuntimeStore(typedSource));
        });
    }

    [Theory]
    [InlineData("unsupported", AsyncTaskRuntimeRetryStatus.UnsupportedTaskType)]
    [InlineData("missing", AsyncTaskRuntimeRetryStatus.Missing)]
    [InlineData("already-prepared", AsyncTaskRuntimeRetryStatus.AlreadyPrepared)]
    [InlineData("retry-limit", AsyncTaskRuntimeRetryStatus.RetryLimitExceeded)]
    [InlineData("not-indeterminate", AsyncTaskRuntimeRetryStatus.NotIndeterminate)]
    [InlineData("reconcile-leased", AsyncTaskRuntimeRetryStatus.NotIndeterminate)]
    [InlineData("prepared-image", AsyncTaskRuntimeRetryStatus.Prepared)]
    [InlineData("prepared-video", AsyncTaskRuntimeRetryStatus.Prepared)]
    public async Task TypedRetryPreparationAndZeroRowClassificationsReuseTheirOnlyPoolSlot(
        string scenario, AsyncTaskRuntimeRetryStatus expectedStatus)
    {
        var schema = $"task_retry_single_{Guid.NewGuid():N}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
            {
                MaxPoolSize = 1, Timeout = 10, CommandTimeout = 5, CancellationTimeout = 1000
            }.ConnectionString);
            var store = new NpgsqlAsyncTaskRuntimeStore(source);
            const string taskId = "single-slot-retry";
            const string dispatchId = "new-dispatch";
            const string reason = "owned retry reason";
            if (scenario != "missing")
            {
                var instant = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
                var seed = NewTask(taskId, state: 6,
                    type: scenario == "unsupported" ? "audio_generation"
                        : scenario == "prepared-video" ? "video_generation" : "image_generation");
                seed.Payload = "{\"prompt\":\"owned retry input\"}";
                seed.Progress = 37;
                seed.ProgressMessage = "preserve progress";
                seed.Result = "{\"original\":true}";
                seed.Error = "preserve original error";
                seed.CreatedAt = instant;
                seed.CompletedAt = instant.AddMinutes(1);
                seed.VirtualKeyId = 137;
                seed.Metadata = "{\"source\":\"single-slot retry\"}";
                seed.LeasedBy = scenario == "reconcile-leased" ? "reconcile:owned" : null;
                seed.LeaseExpiryTime = instant.AddYears(5);
                seed.ProviderInvocationStartedAt = instant.AddSeconds(1);
                seed.ProviderInvocationCompletedAt = instant.AddSeconds(2);
                seed.ProviderOperationId = "original-operation";
                seed.RetryDispatchId = "original-dispatch";
                seed.Version = 11;
                seed.RetryCount = scenario == "retry-limit" ? seed.MaxRetries : 1;
                seed.IsRetryable = false;
                seed.NextRetryAt = instant.AddYears(5);
                if (scenario == "already-prepared")
                {
                    seed.State = 0;
                    seed.RetryDispatchId = dispatchId;
                }
                if (scenario == "not-indeterminate") seed.State = 1;
                await store.CreateAsync(seed, deadline.Token);
            }

            // Independent raw reads witness all 26 durable columns, including
            // fields unrelated to retry preparation and missing-row absence.
            var before = await ReadRetryRowAsync(schema, taskId, deadline.Token);
            var began = DateTime.UtcNow.AddMilliseconds(-1);
            var prepared = await PrepareSingleSlotRetryAsync(store, taskId, dispatchId, reason, scenario, deadline.Token);
            var ended = DateTime.UtcNow.AddMilliseconds(1);
            var after = await ReadRetryRowAsync(schema, taskId, deadline.Token);
            Assert.Equal(expectedStatus, prepared.Status);
            if (expectedStatus == AsyncTaskRuntimeRetryStatus.Prepared)
            {
                Assert.NotNull(before);
                Assert.NotNull(after);
                var expected = new Dictionary<string, object?>(before)
                {
                    ["State"] = 0, ["IsRetryable"] = true,
                    ["Error"] = reason, ["RetryDispatchId"] = dispatchId,
                    ["LeasedBy"] = null, ["LeaseExpiryTime"] = null,
                    ["ProviderInvocationStartedAt"] = null,
                    ["ProviderInvocationCompletedAt"] = null, ["ProviderOperationId"] = null,
                    ["CompletedAt"] = null, ["NextRetryAt"] = null,
                    ["UpdatedAt"] = after["UpdatedAt"],
                    ["RetryCount"] = (int)before["RetryCount"]! + 1,
                    ["Version"] = (int)before["Version"]! + 1
                };
                var updatedAt = Assert.IsType<DateTime>(after["UpdatedAt"]);
                Assert.Equal(DateTimeKind.Utc, updatedAt.Kind);
                Assert.InRange(updatedAt, began, ended);
                AssertRetryRow(expected, after, scenario);
                Assert.NotNull(prepared.Task);
                AssertRetryRow(after, RetryRecordFields(prepared.Task), scenario);

                var redelivery = await PrepareSingleSlotRetryAsync(
                    store, taskId, dispatchId, "redelivery reason", scenario, deadline.Token);
                Assert.Equal(AsyncTaskRuntimeRetryStatus.AlreadyPrepared, redelivery.Status);
                Assert.NotNull(redelivery.Task);
                AssertRetryRow(after, RetryRecordFields(redelivery.Task), scenario);
                AssertRetryRow(after, await ReadRetryRowAsync(schema, taskId, deadline.Token), scenario);
            }
            else
            {
                AssertRetryRow(before, after, scenario);
                if (expectedStatus == AsyncTaskRuntimeRetryStatus.Missing) Assert.Null(prepared.Task);
                else
                {
                    Assert.NotNull(prepared.Task);
                    AssertRetryRow(before, RetryRecordFields(prepared.Task), scenario);
                }
            }

            // A fresh command must lease the same single-slot source after every
            // return path, including the zero-row fallback and replay lookup.
            using var reuseDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            reuseDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            await using var reusable = await source.OpenConnectionAsync(reuseDeadline.Token);
            await using var probe = new NpgsqlCommand("SELECT 1", reusable);
            Assert.Equal(1, await probe.ExecuteScalarAsync(reuseDeadline.Token));
        }, () => DropSchemaWithRetryBudgetAsync(schema),
            $"Single-slot async-task retry case '{scenario}' and owned schema cleanup failed.");
    }

    private static async Task<AsyncTaskRuntimeRetryPreparation> PrepareSingleSlotRetryAsync(
        NpgsqlAsyncTaskRuntimeStore store, string taskId, string dispatchId, string reason,
        string scenario, CancellationToken token)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        caller.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await store.PrepareIndeterminateTaskRetryAsync(taskId, dispatchId, reason, caller.Token);
        }
        catch (Exception failure)
        {
            throw NativeFixtureDiagnostics.Failure(
                $"PrepareIndeterminateTaskRetryAsync MaxPoolSize=1 case '{scenario}' must finish within its caller budget.", failure);
        }
    }

    private async Task<Dictionary<string, object?>?> ReadRetryRowAsync(string schema, string id, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(ManagementConnectionString($"{schema}_snapshot"));
        await observer.OpenAsync(token);
        await using var command = new NpgsqlCommand($"SELECT * FROM \"{schema}\".\"AsyncTasks\" WHERE \"Id\" = @id", observer);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal(26, reader.FieldCount);
        if (!await reader.ReadAsync(token)) return null;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < reader.FieldCount; index++)
            fields.Add(reader.GetName(index), reader.IsDBNull(index) ? null : reader.GetValue(index));
        Assert.False(await reader.ReadAsync(token));
        return fields;
    }

    private static Dictionary<string, object?> RetryRecordFields(AsyncTaskRuntimeRecord task) => new()
    {
        ["Id"] = task.Id, ["Type"] = task.Type, ["State"] = task.State,
        ["Payload"] = task.Payload, ["Progress"] = task.Progress, ["ProgressMessage"] = task.ProgressMessage,
        ["Result"] = task.Result, ["Error"] = task.Error, ["CreatedAt"] = task.CreatedAt,
        ["UpdatedAt"] = task.UpdatedAt, ["CompletedAt"] = task.CompletedAt, ["VirtualKeyId"] = task.VirtualKeyId,
        ["Metadata"] = task.Metadata, ["IsArchived"] = task.IsArchived, ["ArchivedAt"] = task.ArchivedAt,
        ["LeasedBy"] = task.LeasedBy, ["LeaseExpiryTime"] = task.LeaseExpiryTime,
        ["ProviderInvocationStartedAt"] = task.ProviderInvocationStartedAt,
        ["ProviderInvocationCompletedAt"] = task.ProviderInvocationCompletedAt,
        ["ProviderOperationId"] = task.ProviderOperationId, ["RetryDispatchId"] = task.RetryDispatchId,
        ["Version"] = task.Version, ["RetryCount"] = task.RetryCount, ["MaxRetries"] = task.MaxRetries,
        ["IsRetryable"] = task.IsRetryable, ["NextRetryAt"] = task.NextRetryAt
    };

    private static void AssertRetryRow(
        Dictionary<string, object?>? expected, Dictionary<string, object?>? actual, string scenario)
    {
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Assert.Equal(26, expected.Count);
        Assert.Equal(26, actual.Count);
        foreach (var (name, value) in expected)
            Assert.True(actual.TryGetValue(name, out var stored) && Equals(value, stored),
                $"Single-slot retry '{scenario}' changed or omitted durable field '{name}'.");
    }

    private async Task DropSchemaWithRetryBudgetAsync(string schema)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        await DropSchemaAsync(schema, cleanup.Token);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task OwnedSourcesReleaseTheirBackendsAfterSuccessfulFailedOrCancelledWork(string outcome)
    {
        var efSchema = $"task_runtime_ef_{Guid.NewGuid():N}";
        var typedSchema = $"task_runtime_np_{Guid.NewGuid():N}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = new NpgsqlConnection(ManagementConnectionString($"task_runtime_observer_{Guid.NewGuid():N}"));
        await observer.OpenAsync(deadline.Token);
        await using var sentinelSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            ApplicationName = $"task_runtime_sentinel_{Guid.NewGuid():N}",
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 1
        }.ConnectionString);
        var sentinelPid = await BackendPidAsync(sentinelSource, deadline.Token);
        var efPid = 0;
        var typedPid = 0;
        Exception? primary = null;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var actual = await Record.ExceptionAsync(() => WithOwnedSourcesAsync(efSchema, typedSchema,
            async (efSource, typedSource) =>
            {
                var efStore = new EfAsyncTaskRuntimeStore(new AsyncTaskRepository(
                    new TestDbContextFactory(efSource), NullLogger<AsyncTaskRepository>.Instance));
                var typedStore = new NpgsqlAsyncTaskRuntimeStore(typedSource);
                // Both production adapters must actually use their owned pool before
                // observing its backend; inspecting an unopened source proves nothing.
                Assert.Null(await efStore.GetByIdAsync("missing", deadline.Token));
                Assert.Null(await typedStore.GetByIdAsync("missing", deadline.Token));
                efPid = await BackendPidAsync(efSource, deadline.Token);
                typedPid = await BackendPidAsync(typedSource, deadline.Token);
                try
                {
                    if (outcome == "failure")
                    {
                        await using var connection = await efSource.OpenConnectionAsync(deadline.Token);
                        await using var command = new NpgsqlCommand("SELECT 1 / 0", connection);
                        await command.ExecuteScalarAsync(deadline.Token);
                    }
                    else if (outcome == "cancellation")
                    {
                        await typedStore.GetByIdAsync("missing", cancelled.Token);
                    }
                }
                catch (Exception exception) { primary = exception; throw; }
            }));

        if (outcome == "success") Assert.Null(actual);
        else
        {
            Assert.NotNull(primary);
            Assert.Same(primary, actual);
            if (outcome == "failure")
                Assert.Equal(PostgresErrorCodes.DivisionByZero, Assert.IsType<PostgresException>(actual).SqlState);
            else
                Assert.Equal(cancelled.Token, Assert.IsAssignableFrom<OperationCanceledException>(actual).CancellationToken);
        }
        Assert.True(efPid > 0);
        Assert.True(typedPid > 0);
        Assert.NotEqual(efPid, typedPid);
        Assert.NotEqual(sentinelPid, efPid);
        Assert.NotEqual(sentinelPid, typedPid);
        await WaitForOwnedBackendsToDisappearAsync(observer, efSchema, typedSchema, [efPid, typedPid], deadline.Token);
        Assert.False(await SchemaExistsAsync(observer, efSchema, deadline.Token));
        Assert.False(await SchemaExistsAsync(observer, typedSchema, deadline.Token));
        // The unrelated idle source must retain its backend across owned cleanup.
        // Reopening it is stronger than keeping an already-open sentinel lease.
        Assert.Equal(sentinelPid, await BackendPidAsync(sentinelSource, deadline.Token));
    }

    [Fact]
    public async Task BlockedFirstSchemaDropIsBoundedAndStillDropsTheSecondWithoutLosingTheWorkFailure()
    {
        var efSchema = $"task_runtime_ef_{Guid.NewGuid():N}";
        var typedSchema = $"task_runtime_np_{Guid.NewGuid():N}";
        var primary = new InvalidOperationException("Injected async-task fixture failure.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = new NpgsqlConnection(ManagementConnectionString($"task_runtime_observer_{Guid.NewGuid():N}"));
        await observer.OpenAsync(deadline.Token);
        NpgsqlConnection? blocker = null;
        NpgsqlTransaction? transaction = null;
        var efPid = 0;
        var typedPid = 0;
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            var elapsed = new System.Diagnostics.Stopwatch();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => WithOwnedSourcesAsync(
                efSchema, typedSchema, async (efSource, typedSource) =>
                {
                    efPid = await BackendPidAsync(efSource, deadline.Token);
                    typedPid = await BackendPidAsync(typedSource, deadline.Token);
                    blocker = new NpgsqlConnection(ManagementConnectionString($"{efSchema}_blocker"));
                    await blocker.OpenAsync(deadline.Token);
                    transaction = await blocker.BeginTransactionAsync(deadline.Token);
                    await using var command = new NpgsqlCommand(
                        $"LOCK TABLE \"{efSchema}\".\"AsyncTasks\" IN ACCESS SHARE MODE", blocker);
                    await command.ExecuteNonQueryAsync(deadline.Token);
                    elapsed.Start();
                    throw primary;
                }, TimeSpan.FromSeconds(1)));

            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
            Assert.Same(primary, failure.InnerExceptions[0]);
            var cleanup = Assert.IsType<AggregateException>(failure.InnerExceptions[1]);
            Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(cleanup.InnerExceptions));
            await WaitForOwnedBackendsToDisappearAsync(observer, efSchema, typedSchema, [efPid, typedPid], deadline.Token);
            Assert.True(await SchemaExistsAsync(observer, efSchema, deadline.Token));
            Assert.False(await SchemaExistsAsync(observer, typedSchema, deadline.Token));
        }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
            [async _ => { if (transaction is not null) await transaction.DisposeAsync(); },
             async _ => { if (blocker is not null) await blocker.DisposeAsync(); },
             token => DropSchemaAsync(efSchema, token),
             token => DropSchemaAsync(typedSchema, token)],
            CleanupTimeout, "Async-task cleanup regression resources could not all be released."),
            "Async-task cleanup regression and resource release failed.");
    }

    private async Task WithOwnedSourcesAsync(
        string efSchema,
        string typedSchema,
        Func<NpgsqlDataSource, NpgsqlDataSource, Task> work,
        TimeSpan? cleanupTimeout = null)
    {
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            await CreateSchemaAsync(efSchema);
            await CreateSchemaAsync(typedSchema);
            // All contexts and connection leases in work are joined before these
            // two sources are disposed, and both sources end before schema drops.
            await using var efSource = NpgsqlDataSource.Create(WithSearchPath(efSchema));
            await using var typedSource = NpgsqlDataSource.Create(WithSearchPath(typedSchema));
            await work(efSource, typedSource);
        }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
            [token => DropSchemaAsync(efSchema, token), token => DropSchemaAsync(typedSchema, token)],
            cleanupTimeout ?? CleanupTimeout, "Async-task owned schemas could not all be removed."),
            "Async-task fixture work and owned schema cleanup failed.");
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
        using var deadline = new CancellationTokenSource(CleanupTimeout);
        await using var connection = new NpgsqlConnection(ManagementConnectionString($"{schema}_management"));
        await connection.OpenAsync(deadline.Token);
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
        await command.ExecuteNonQueryAsync(deadline.Token);
    }

    private async Task DropSchemaAsync(string schema, CancellationToken token)
    {
        // This connection never enters the fixture's shared implicit pool. Its
        // independent caller budget covers both opening and the schema command.
        await using var connection = new NpgsqlConnection(ManagementConnectionString($"{schema}_management"));
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync(token);
    }

    private string ManagementConnectionString(string applicationName) =>
        new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 10,
            CancellationTimeout = 1000
        }.ConnectionString;

    private string WithSearchPath(string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = schema,
            ApplicationName = schema,
            Pooling = true,
            MinPoolSize = 0
        };
        return builder.ConnectionString;
    }

    private static async Task<int> BackendPidAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        return connection.ProcessID;
    }

    private static async Task<bool> SchemaExistsAsync(NpgsqlConnection observer, string schema, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regnamespace(@schema) IS NOT NULL", observer);
        command.Parameters.AddWithValue("schema", schema);
        return (bool)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task WaitForOwnedBackendsToDisappearAsync(
        NpgsqlConnection observer, string efSchema, string typedSchema, int[] pids, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE pid = ANY(@pids) OR application_name = ANY(@applications))
            """, observer);
        command.Parameters.AddWithValue("pids", pids);
        command.Parameters.AddWithValue("applications", new[]
        {
            efSchema, typedSchema, $"{efSchema}_management", $"{typedSchema}_management"
        });
        while (await command.ExecuteScalarAsync(token) is not true)
            await Task.Delay(25, token);
    }

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
}
