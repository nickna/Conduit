#if !CONDUIT_NATIVE_AOT
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;
using NpgsqlTypes;

using Xunit;
using Xunit.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class AsyncTaskRuntimeGuardPersistenceParityTests(
    PostgresLockTestContainerFixture fixture, ITestOutputHelper output)
{
    private static readonly DateTime Past = new(2000, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Future = new(2100, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private const string Worker = "guard-worker";
    private const string UnknownOutcome = "Provider outcome is unknown after the processing lease expired.";

    [Theory]
    [InlineData(false, "start-archived")]
    [InlineData(true, "start-archived")]
    [InlineData(false, "start-expired")]
    [InlineData(true, "start-expired")]
    [InlineData(false, "start-repeated")]
    [InlineData(true, "start-repeated")]
    [InlineData(false, "complete-before-start")]
    [InlineData(true, "complete-before-start")]
    [InlineData(false, "extend-archived")]
    [InlineData(true, "extend-archived")]
    [InlineData(false, "extend-terminal")]
    [InlineData(true, "extend-terminal")]
    [InlineData(false, "claim-pending-evidence")]
    [InlineData(true, "claim-pending-evidence")]
    [InlineData(false, "claim-expired-completion-only")]
    [InlineData(true, "claim-expired-completion-only")]
    [InlineData(false, "claim-archived-evidence")]
    [InlineData(true, "claim-archived-evidence")]
    public async Task RejectedOrUncertainGuardsPreserveTheCompleteDurableContract(bool typed, string scenario)
    {
        await WithFixtureAsync(async (owned, token) =>
        {
            var seed = Seed(scenario);
            await owned.InsertAsync(seed, token);
            var before = await owned.ReadAsync(seed.Id, token);
            var began = DateTime.UtcNow.AddSeconds(-1);
            var store = owned.Store(typed);
            object result = scenario switch
            {
                "start-archived" or "start-expired" or "start-repeated" =>
                    await store.MarkProviderInvocationStartedAsync(seed.Id, Worker, token),
                "complete-before-start" =>
                    await store.MarkProviderInvocationCompletedAsync(seed.Id, Worker, "new-operation", token),
                "extend-archived" or "extend-terminal" =>
                    await store.ExtendLeaseAsync(seed.Id, Worker, TimeSpan.FromMinutes(3), token),
                _ => await store.TryClaimTaskAsync(seed.Id, "new-worker", TimeSpan.FromMinutes(2), token)
            };
            var ended = DateTime.UtcNow.AddSeconds(1);
            var after = await owned.ReadAsync(seed.Id, token);
            var expectedResult = scenario switch
            {
                "claim-pending-evidence" or "claim-expired-completion-only" =>
                    (object)AsyncTaskRuntimeClaimStatus.Indeterminate,
                "claim-archived-evidence" => AsyncTaskRuntimeClaimStatus.AlreadyClaimed,
                _ => false
            };
            var changes = ChangedFields(before, after);
            // Emit both independent durable witnesses before the conformance
            // assertion, so every original-adapter difference survives in TRX.
            output.WriteLine("ASYNC_TASK_GUARD_WITNESS=" + JsonSerializer.Serialize(new
            {
                typed, scenario, fieldCount = 26,
                result = result.ToString(), expectedResult = expectedResult.ToString(),
                before, after, changedFields = changes
            }));

            Assert.Equal(expectedResult, result);
            if (scenario is "claim-pending-evidence" or "claim-expired-completion-only")
            {
                Assert.Equal(6, after.State);
                Assert.False(after.IsRetryable);
                Assert.Equal(UnknownOutcome, after.Error);
                Assert.Null(after.LeasedBy);
                Assert.Null(after.LeaseExpiryTime);
                Assert.Equal(before.Version + 1, after.Version);
                Assert.NotNull(after.CompletedAt);
                Assert.InRange(after.UpdatedAt, began, ended);
                Assert.Equal(DateTimeKind.Utc, after.UpdatedAt.Kind);
                Assert.Equal(after.UpdatedAt, after.CompletedAt);
                var expected = Copy(before);
                expected.State = 6;
                expected.IsRetryable = false;
                expected.Error = UnknownOutcome;
                expected.LeasedBy = null;
                expected.LeaseExpiryTime = null;
                expected.CompletedAt = after.CompletedAt;
                expected.UpdatedAt = after.UpdatedAt;
                expected.Version++;
                AssertSnapshot(expected, after);
            }
            else AssertSnapshot(before, after);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanClaimsAndFallbacksPreserveEveryUnchangedField(bool typed)
    {
        await WithFixtureAsync(async (owned, token) =>
        {
            var store = owned.Store(typed);
            var duration = TimeSpan.FromMinutes(2);
            foreach (var pending in new[] { true, false })
            {
                var seed = Seed(pending ? "clean-pending" : "clean-expired");
                seed.State = pending ? 0 : 1;
                // Pending claims ignore an existing lease; processing claims
                // require an expired lease. Neither row has provider evidence.
                seed.LeaseExpiryTime = pending ? Future : Past;
                await owned.InsertAsync(seed, token);
                var before = await owned.ReadAsync(seed.Id, token);
                var began = DateTime.UtcNow.AddSeconds(-1);
                Assert.Equal(AsyncTaskRuntimeClaimStatus.Claimed,
                    await store.TryClaimTaskAsync(seed.Id, "clean-worker", duration, token));
                var ended = DateTime.UtcNow.AddSeconds(1);
                var after = await owned.ReadAsync(seed.Id, token);
                AssertClaim(before, after, "clean-worker", duration, began, ended);
            }

            foreach (var scenario in new[] { "null-lease-clean", "null-lease-evidence", "archived-indeterminate", "terminal" })
            {
                var seed = Seed(scenario);
                if (scenario.StartsWith("null-lease", StringComparison.Ordinal)) seed.LeaseExpiryTime = null;
                if (scenario == "null-lease-evidence") seed.ProviderInvocationStartedAt = Past;
                if (scenario == "archived-indeterminate")
                {
                    seed.State = 6;
                    seed.IsArchived = true;
                    seed.ArchivedAt = Past;
                    seed.ProviderInvocationStartedAt = Past;
                    seed.LeaseExpiryTime = Past;
                }
                if (scenario == "terminal") seed.State = 2;
                await owned.InsertAsync(seed, token);
                var before = await owned.ReadAsync(seed.Id, token);
                var expected = scenario == "archived-indeterminate" ? AsyncTaskRuntimeClaimStatus.Indeterminate
                    : scenario == "terminal" ? AsyncTaskRuntimeClaimStatus.Terminal : AsyncTaskRuntimeClaimStatus.AlreadyClaimed;
                Assert.Equal(expected, await store.TryClaimTaskAsync(seed.Id, "other-worker", duration, token));
                AssertSnapshot(before, await owned.ReadAsync(seed.Id, token));
            }
            Assert.Equal(AsyncTaskRuntimeClaimStatus.Missing,
                await store.TryClaimTaskAsync("never-inserted", "clean-worker", duration, token));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderPhasesAllowRepeatedCompletionAndPreserveOperationIdRules(bool typed)
    {
        await WithFixtureAsync(async (owned, token) =>
        {
            var seed = Seed("valid-phases");
            await owned.InsertAsync(seed, token);
            var store = owned.Store(typed);
            var before = await owned.ReadAsync(seed.Id, token);
            var began = DateTime.UtcNow.AddSeconds(-1);
            Assert.True(await store.MarkProviderInvocationStartedAsync(seed.Id, Worker, token));
            var after = await owned.ReadAsync(seed.Id, token);
            Assert.NotNull(after.ProviderInvocationStartedAt);
            Assert.InRange(after.ProviderInvocationStartedAt.Value, began, DateTime.UtcNow.AddSeconds(1));
            Assert.Equal(after.ProviderInvocationStartedAt, after.UpdatedAt);
            var expected = Copy(before);
            expected.ProviderInvocationStartedAt = after.ProviderInvocationStartedAt;
            expected.UpdatedAt = after.UpdatedAt;
            AssertSnapshot(expected, after);
            Assert.False(await store.MarkProviderInvocationStartedAsync(seed.Id, Worker, token));
            AssertSnapshot(after, await owned.ReadAsync(seed.Id, token));

            // Completion can repeat while the live lease remains valid. Null
            // retains the prior operation ID, whereas an empty ID replaces it.
            foreach (var operationId in new string?[] { "completed-operation", null, "" })
            {
                before = after;
                began = DateTime.UtcNow.AddSeconds(-1);
                Assert.True(await store.MarkProviderInvocationCompletedAsync(seed.Id, Worker, operationId, token));
                after = await owned.ReadAsync(seed.Id, token);
                Assert.NotNull(after.ProviderInvocationCompletedAt);
                Assert.InRange(after.ProviderInvocationCompletedAt.Value, began, DateTime.UtcNow.AddSeconds(1));
                Assert.Equal(after.ProviderInvocationCompletedAt, after.UpdatedAt);
                expected = Copy(before);
                expected.ProviderInvocationCompletedAt = after.ProviderInvocationCompletedAt;
                expected.ProviderOperationId = operationId ?? before.ProviderOperationId;
                expected.UpdatedAt = after.UpdatedAt;
                AssertSnapshot(expected, after); // Includes unchanged Version.
            }
            Assert.False(await store.MarkProviderInvocationStartedAsync(seed.Id, Worker, token));
            AssertSnapshot(after, await owned.ReadAsync(seed.Id, token));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionRequiresALiveOwnedNonArchivedProcessingLease(bool typed)
    {
        await WithFixtureAsync(async (owned, token) =>
        {
            var store = owned.Store(typed);
            foreach (var scenario in new[] { "archived", "expired", "null-lease", "terminal", "wrong-owner" })
            {
                var seed = Seed("completion-" + scenario);
                seed.ProviderInvocationStartedAt = Past;
                if (scenario == "archived") { seed.IsArchived = true; seed.ArchivedAt = Past; }
                if (scenario == "expired") seed.LeaseExpiryTime = Past;
                if (scenario == "null-lease") seed.LeaseExpiryTime = null;
                if (scenario == "terminal") seed.State = 2;
                if (scenario == "wrong-owner") seed.LeasedBy = "other-owner";
                await owned.InsertAsync(seed, token);
                var before = await owned.ReadAsync(seed.Id, token);
                var result = await store.MarkProviderInvocationCompletedAsync(seed.Id, Worker, "must-not-replace", token);
                var after = await owned.ReadAsync(seed.Id, token);
                output.WriteLine("ASYNC_TASK_GUARD_COMPLETION_REJECTION=" + JsonSerializer.Serialize(new
                {
                    typed, scenario, result, fieldCount = 26, before, after
                }));
                Assert.False(result);
                AssertSnapshot(before, after);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveExtensionUsesCurrentTimeAndIncrementsOnlyTheLeaseVersion(bool typed)
    {
        await WithFixtureAsync(async (owned, token) =>
        {
            var seed = Seed("valid-extension");
            await owned.InsertAsync(seed, token);
            var before = await owned.ReadAsync(seed.Id, token);
            var duration = TimeSpan.FromMinutes(3);
            var began = DateTime.UtcNow.AddSeconds(-1);
            Assert.True(await owned.Store(typed).ExtendLeaseAsync(seed.Id, Worker, duration, token));
            var after = await owned.ReadAsync(seed.Id, token);
            Assert.InRange(after.UpdatedAt, began, DateTime.UtcNow.AddSeconds(1));
            Assert.Equal(DateTimeKind.Utc, after.UpdatedAt.Kind);
            Assert.Equal(after.UpdatedAt + duration, after.LeaseExpiryTime);
            Assert.NotEqual(before.LeaseExpiryTime + duration, after.LeaseExpiryTime);
            var expected = Copy(before);
            expected.LeaseExpiryTime = after.LeaseExpiryTime;
            expected.UpdatedAt = after.UpdatedAt;
            expected.Version++;
            AssertSnapshot(expected, after);
        });
    }

    [Theory]
    [InlineData(false, "claim")]
    [InlineData(true, "claim")]
    [InlineData(false, "start")]
    [InlineData(true, "start")]
    public async Task ActualSqlContendersHaveExactlyOneGuardWinner(bool typed, string operation)
    {
        await WithFixtureAsync(async (owned, token) =>
        {
            var seed = Seed("contended-" + operation);
            if (operation == "claim") seed.State = 0;
            await owned.InsertAsync(seed, token);
            var before = await owned.ReadAsync(seed.Id, token);
            var store = owned.Store(typed);
            var duration = TimeSpan.FromMinutes(2);
            var began = DateTime.UtcNow.AddSeconds(-1);
            var contention = await owned.RunContendersAsync(async (index, callerToken) => operation == "claim"
                ? (object)await store.TryClaimTaskAsync(seed.Id, "contender-" + index, duration, callerToken)
                : await store.MarkProviderInvocationStartedAsync(seed.Id, Worker, callerToken), token);
            var results = contention.Results;
            var ended = DateTime.UtcNow.AddSeconds(1);
            var after = await owned.ReadAsync(seed.Id, token);
            var winners = results.Select((result, index) => (result, index))
                .Where(entry => operation == "claim"
                    ? Equals(entry.result, AsyncTaskRuntimeClaimStatus.Claimed) : Equals(entry.result, true)).ToArray();
            var winner = Assert.Single(winners).index;
            Assert.Equal(operation == "claim" ? (object)AsyncTaskRuntimeClaimStatus.AlreadyClaimed : false, results[1 - winner]);
            if (operation == "claim") AssertClaim(before, after, "contender-" + winner, duration, began, ended);
            else
            {
                Assert.NotNull(after.ProviderInvocationStartedAt);
                Assert.InRange(after.ProviderInvocationStartedAt.Value, began, ended);
                Assert.Equal(after.ProviderInvocationStartedAt, after.UpdatedAt);
                var expected = Copy(before);
                expected.ProviderInvocationStartedAt = after.ProviderInvocationStartedAt;
                expected.UpdatedAt = after.UpdatedAt;
                AssertSnapshot(expected, after);
            }
            output.WriteLine("ASYNC_TASK_GUARD_CONTENDERS=" + JsonSerializer.Serialize(new
            {
                typed, operation, winner, blockerPid = contention.BlockerPid, backendPids = contention.BackendPids,
                results = results.Select(result => result.ToString()).ToArray(),
                fieldCount = 26, before, after
            }));
        });
    }

    private static void AssertClaim(AsyncTaskRuntimeRecord before, AsyncTaskRuntimeRecord after,
        string worker, TimeSpan duration, DateTime began, DateTime ended)
    {
        Assert.Equal(1, after.State);
        Assert.Equal(worker, after.LeasedBy);
        Assert.InRange(after.UpdatedAt, began, ended);
        Assert.Equal(DateTimeKind.Utc, after.UpdatedAt.Kind);
        Assert.Equal(after.UpdatedAt + duration, after.LeaseExpiryTime);
        var expected = Copy(before);
        expected.State = 1;
        expected.LeasedBy = worker;
        expected.LeaseExpiryTime = after.LeaseExpiryTime;
        expected.UpdatedAt = after.UpdatedAt;
        expected.Version++;
        AssertSnapshot(expected, after);
    }

    private static AsyncTaskRuntimeRecord Seed(string scenario)
    {
        var row = new AsyncTaskRuntimeRecord
        {
            Id = "guard-" + scenario, Type = "image_generation", State = 1,
            Payload = "{\"prompt\":\"owned guard input\"}", Progress = 37,
            ProgressMessage = "preserve progress", Result = "{\"owned\":true}", Error = "preserve original error",
            CreatedAt = Past.AddYears(1), UpdatedAt = Past.AddYears(2), CompletedAt = null,
            VirtualKeyId = 137, Metadata = "{\"source\":\"guard fixture\"}",
            IsArchived = false, ArchivedAt = null, LeasedBy = Worker, LeaseExpiryTime = Future,
            ProviderInvocationStartedAt = null, ProviderInvocationCompletedAt = null,
            ProviderOperationId = "original-operation", RetryDispatchId = "original-dispatch",
            Version = 11, RetryCount = 2, MaxRetries = 9, IsRetryable = true, NextRetryAt = Future.AddDays(1)
        };
        if (scenario is "start-archived" or "extend-archived" or "claim-archived-evidence")
        {
            row.IsArchived = true;
            row.ArchivedAt = Past.AddYears(3);
        }
        if (scenario is "start-expired" or "claim-expired-completion-only" or "claim-archived-evidence")
            row.LeaseExpiryTime = Past;
        if (scenario is "start-repeated" or "claim-pending-evidence" or "claim-archived-evidence")
            row.ProviderInvocationStartedAt = Past.AddYears(4);
        if (scenario == "claim-expired-completion-only")
            row.ProviderInvocationCompletedAt = Past.AddYears(5);
        if (scenario == "claim-pending-evidence") row.State = 0;
        if (scenario == "extend-terminal")
        {
            row.State = 2;
            row.CompletedAt = Past.AddYears(6);
        }
        return row;
    }

    private static AsyncTaskRuntimeRecord Copy(AsyncTaskRuntimeRecord record) =>
        JsonSerializer.Deserialize<AsyncTaskRuntimeRecord>(JsonSerializer.Serialize(record))!;

    private static string[] ChangedFields(AsyncTaskRuntimeRecord before, AsyncTaskRuntimeRecord after)
    {
        using var left = JsonDocument.Parse(JsonSerializer.Serialize(before));
        using var right = JsonDocument.Parse(JsonSerializer.Serialize(after));
        Assert.Equal(26, left.RootElement.EnumerateObject().Count());
        Assert.Equal(26, right.RootElement.EnumerateObject().Count());
        return left.RootElement.EnumerateObject()
            .Where(field => field.Value.GetRawText() != right.RootElement.GetProperty(field.Name).GetRawText())
            .Select(field => field.Name).ToArray();
    }

    private static void AssertSnapshot(AsyncTaskRuntimeRecord expected, AsyncTaskRuntimeRecord actual) =>
        Assert.Empty(ChangedFields(expected, actual));

    private sealed record ContentionResult(object[] Results, int BlockerPid, int[] BackendPids);

    private async Task WithFixtureAsync(Func<OwnedFixture, CancellationToken, Task> work)
    {
        var schema = "task_guard_" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        OwnedFixture? owned = null;
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            owned = new OwnedFixture(fixture.ConnectionString, schema,
                removed => output.WriteLine("ASYNC_TASK_GUARD_SCHEMA_ABSENT=" + removed));
            await owned.CreateAsync(deadline.Token);
            await work(owned, deadline.Token);
        }, async () =>
        {
            deadline.Cancel();
            if (owned is not null) await owned.DisposeAsync();
        }, "Async-task guard proof and owned cleanup failed.");
    }

    private sealed class OwnedFixture
    {
        private NpgsqlDataSource? _source;
        private readonly string _rawConnectionString;
        private readonly string _schema;
        private readonly string _management;
        private readonly Action<string> _schemaRemoved;

        public OwnedFixture(string rawConnectionString, string schema, Action<string> schemaRemoved)
        {
            _rawConnectionString = rawConnectionString;
            _schema = schema;
            _schemaRemoved = schemaRemoved;
            _management = new NpgsqlConnectionStringBuilder(rawConnectionString)
            {
                Pooling = false, Timeout = 5, CommandTimeout = 5, CancellationTimeout = 1000,
                ApplicationName = schema + "_oracle"
            }.ConnectionString;
        }

        public async Task CreateAsync(CancellationToken token)
        {
            await using var connection = new NpgsqlConnection(_management);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE SCHEMA "{_schema}";
                CREATE TABLE "{_schema}"."AsyncTasks" (
                    "Id" varchar(50) PRIMARY KEY, "Type" varchar(100) NOT NULL, "State" integer NOT NULL,
                    "Payload" text, "Progress" integer NOT NULL, "ProgressMessage" varchar(500),
                    "Result" text, "Error" text, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL,
                    "CompletedAt" timestamptz, "VirtualKeyId" integer NOT NULL, "Metadata" text,
                    "IsArchived" boolean NOT NULL, "ArchivedAt" timestamptz, "LeasedBy" varchar(100),
                    "LeaseExpiryTime" timestamptz, "ProviderInvocationStartedAt" timestamptz,
                    "ProviderInvocationCompletedAt" timestamptz, "ProviderOperationId" varchar(200),
                    "RetryDispatchId" varchar(64), "Version" integer NOT NULL, "RetryCount" integer NOT NULL,
                    "MaxRetries" integer NOT NULL, "IsRetryable" boolean NOT NULL, "NextRetryAt" timestamptz);
                """;
            await command.ExecuteNonQueryAsync(token);
            _source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_rawConnectionString)
            {
                SearchPath = _schema, ApplicationName = _schema, Pooling = true,
                MinPoolSize = 0, MaxPoolSize = 2, Timeout = 5, CommandTimeout = 20, CancellationTimeout = 1000
            }.ConnectionString);
        }

        public IAsyncTaskRuntimeStore Store(bool typed) => typed
            ? new NpgsqlAsyncTaskRuntimeStore(_source!)
            : new EfAsyncTaskRuntimeStore(new AsyncTaskRepository(
                new OwnedContextFactory(_source!), NullLogger<AsyncTaskRepository>.Instance));

        public async Task<ContentionResult> RunContendersAsync(
            Func<int, CancellationToken, Task<object>> contender, CancellationToken token)
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            NpgsqlConnection? blocker = null;
            NpgsqlConnection? observer = null;
            NpgsqlTransaction? transaction = null;
            var lockReleased = false;
            var pending = new List<Task<object>>();
            ContentionResult? result = null;
            await IntegrationProofCleanup.RunAsync(async () =>
            {
                blocker = new NpgsqlConnection(_management);
                await blocker.OpenAsync(token);
                transaction = await blocker.BeginTransactionAsync(token);
                await using (var command = new NpgsqlCommand($"LOCK TABLE \"{_schema}\".\"AsyncTasks\" IN ACCESS EXCLUSIVE MODE", blocker, transaction))
                    await command.ExecuteNonQueryAsync(token);
                observer = new NpgsqlConnection(_management);
                await observer.OpenAsync(token);
                var blockerPid = blocker.ProcessID;
                pending.Add(contender(0, caller.Token));
                pending.Add(contender(1, caller.Token));
                var backendPids = await ObserveTwoBlockedUpdatesAsync(observer, blockerPid, token);
                Assert.All(pending, task => Assert.False(task.IsCompleted));
                await transaction.RollbackAsync(token);
                lockReleased = true;
                var values = await Task.WhenAll(pending).WaitAsync(token);
                result = new ContentionResult(values, blockerPid, backendPids);
            }, async () =>
            {
                caller.Cancel();
                await IntegrationProofCleanup.DrainIndependentlyAsync(
                    [DrainAsync,
                     async cleanupToken =>
                     {
                         if (transaction is not null && !lockReleased) await transaction.RollbackAsync(cleanupToken);
                     },
                     async cleanupToken =>
                     {
                         if (transaction is not null) await transaction.DisposeAsync().AsTask().WaitAsync(cleanupToken);
                     },
                     async cleanupToken =>
                     {
                         if (blocker is not null) await blocker.DisposeAsync().AsTask().WaitAsync(cleanupToken);
                     },
                     async cleanupToken =>
                     {
                         if (observer is not null) await observer.DisposeAsync().AsTask().WaitAsync(cleanupToken);
                     },
                     DrainAsync], TimeSpan.FromSeconds(5), "Async-task contender drain/unlock failed.");
            }, "Async-task contenders and cleanup failed.");
            return result!;

            async Task DrainAsync(CancellationToken cleanupToken)
            {
                // A canceled producer is an expected cleanup outcome; a timed
                // cleanup wait is still a failure. Attempt both joins even if
                // one producer faults.
                var failures = new List<Exception>();
                foreach (var task in pending)
                {
                    try { await task.WaitAsync(cleanupToken); }
                    catch (OperationCanceledException) when (task.IsCanceled) { }
                    catch (Exception exception) { failures.Add(exception); }
                }
                if (failures.Count != 0) throw new AggregateException(failures);
            }
        }

        private async Task<int[]> ObserveTwoBlockedUpdatesAsync(NpgsqlConnection observer,
            int blockerPid, CancellationToken token)
        {
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
            observation.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var command = new NpgsqlCommand("""
                    SELECT a.pid FROM pg_stat_activity a
                    WHERE a.datname=current_database() AND a.application_name=@application
                        AND a.state='active' AND a.wait_event_type='Lock'
                        AND a.query LIKE 'UPDATE %' AND a.query LIKE '%AsyncTasks%'
                        AND @blocker=ANY(pg_blocking_pids(a.pid))
                        AND EXISTS (SELECT 1 FROM pg_locks l WHERE l.pid=a.pid
                            AND l.locktype='relation' AND l.database=(SELECT oid FROM pg_database WHERE datname=current_database())
                            AND l.relation=to_regclass(@relation) AND l.mode='RowExclusiveLock' AND NOT l.granted)
                    ORDER BY a.pid
                    """, observer);
                command.Parameters.AddWithValue("application", NpgsqlDbType.Text, _schema);
                command.Parameters.AddWithValue("blocker", NpgsqlDbType.Integer, blockerPid);
                command.Parameters.AddWithValue("relation", NpgsqlDbType.Text, $"\"{_schema}\".\"AsyncTasks\"");
                var pids = new List<int>();
                await using (var reader = await command.ExecuteReaderAsync(observation.Token))
                    while (await reader.ReadAsync(observation.Token)) pids.Add(reader.GetInt32(0));
                if (pids.Count == 2)
                {
                    Assert.Equal(2, pids.Distinct().Count());
                    Assert.DoesNotContain(blockerPid, pids);
                    return pids.ToArray();
                }
                Assert.InRange(pids.Count, 0, 1);
                await Task.Delay(20, observation.Token);
            }
        }

        public async Task InsertAsync(AsyncTaskRuntimeRecord row, CancellationToken token)
        {
            await using var connection = new NpgsqlConnection(_management);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO "{_schema}"."AsyncTasks" (
                    "Id","Type","State","Payload","Progress","ProgressMessage","Result","Error",
                    "CreatedAt","UpdatedAt","CompletedAt","VirtualKeyId","Metadata","IsArchived","ArchivedAt",
                    "LeasedBy","LeaseExpiryTime","ProviderInvocationStartedAt","ProviderInvocationCompletedAt",
                    "ProviderOperationId","RetryDispatchId","Version","RetryCount","MaxRetries","IsRetryable","NextRetryAt")
                VALUES (@id,@type,@state,@payload,@progress,@message,@result,@error,@created,@updated,@completed,
                    @key,@metadata,@archived,@archivedAt,@owner,@expiry,@started,@providerCompleted,
                    @operation,@dispatch,@version,@retry,@max,@retryable,@next);
                """;
            (string Name, NpgsqlDbType Type, object? Value)[] values =
            [
                ("id", NpgsqlDbType.Varchar, row.Id), ("type", NpgsqlDbType.Varchar, row.Type),
                ("state", NpgsqlDbType.Integer, row.State), ("payload", NpgsqlDbType.Text, row.Payload),
                ("progress", NpgsqlDbType.Integer, row.Progress), ("message", NpgsqlDbType.Varchar, row.ProgressMessage),
                ("result", NpgsqlDbType.Text, row.Result), ("error", NpgsqlDbType.Text, row.Error),
                ("created", NpgsqlDbType.TimestampTz, row.CreatedAt), ("updated", NpgsqlDbType.TimestampTz, row.UpdatedAt),
                ("completed", NpgsqlDbType.TimestampTz, row.CompletedAt), ("key", NpgsqlDbType.Integer, row.VirtualKeyId),
                ("metadata", NpgsqlDbType.Text, row.Metadata), ("archived", NpgsqlDbType.Boolean, row.IsArchived),
                ("archivedAt", NpgsqlDbType.TimestampTz, row.ArchivedAt), ("owner", NpgsqlDbType.Varchar, row.LeasedBy),
                ("expiry", NpgsqlDbType.TimestampTz, row.LeaseExpiryTime), ("started", NpgsqlDbType.TimestampTz, row.ProviderInvocationStartedAt),
                ("providerCompleted", NpgsqlDbType.TimestampTz, row.ProviderInvocationCompletedAt),
                ("operation", NpgsqlDbType.Varchar, row.ProviderOperationId), ("dispatch", NpgsqlDbType.Varchar, row.RetryDispatchId),
                ("version", NpgsqlDbType.Integer, row.Version), ("retry", NpgsqlDbType.Integer, row.RetryCount),
                ("max", NpgsqlDbType.Integer, row.MaxRetries), ("retryable", NpgsqlDbType.Boolean, row.IsRetryable),
                ("next", NpgsqlDbType.TimestampTz, row.NextRetryAt)
            ];
            foreach (var value in values)
                command.Parameters.Add(new NpgsqlParameter(value.Name, value.Type) { Value = value.Value ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(token);
        }

        public async Task<AsyncTaskRuntimeRecord> ReadAsync(string id, CancellationToken token)
        {
            await using var connection = new NpgsqlConnection(_management);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT "Id","Type","State","Payload","Progress","ProgressMessage","Result","Error",
                    "CreatedAt","UpdatedAt","CompletedAt","VirtualKeyId","Metadata","IsArchived","ArchivedAt",
                    "LeasedBy","LeaseExpiryTime","ProviderInvocationStartedAt","ProviderInvocationCompletedAt",
                    "ProviderOperationId","RetryDispatchId","Version","RetryCount","MaxRetries","IsRetryable","NextRetryAt"
                FROM "{_schema}"."AsyncTasks" WHERE "Id"=@id
                """;
            command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, id);
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(26, reader.FieldCount);
            var row = new AsyncTaskRuntimeRecord
            {
                Id = reader.GetString(0), Type = reader.GetString(1), State = reader.GetInt32(2),
                Payload = Text(3), Progress = reader.GetInt32(4), ProgressMessage = Text(5),
                Result = Text(6), Error = Text(7), CreatedAt = reader.GetDateTime(8), UpdatedAt = reader.GetDateTime(9),
                CompletedAt = Time(10), VirtualKeyId = reader.GetInt32(11), Metadata = Text(12),
                IsArchived = reader.GetBoolean(13), ArchivedAt = Time(14), LeasedBy = Text(15),
                LeaseExpiryTime = Time(16), ProviderInvocationStartedAt = Time(17), ProviderInvocationCompletedAt = Time(18),
                ProviderOperationId = Text(19), RetryDispatchId = Text(20), Version = reader.GetInt32(21),
                RetryCount = reader.GetInt32(22), MaxRetries = reader.GetInt32(23), IsRetryable = reader.GetBoolean(24), NextRetryAt = Time(25)
            };
            Assert.False(await reader.ReadAsync(token));
            return row;
            string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            DateTime? Time(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
        }

        public Task DisposeAsync() => IntegrationProofCleanup.DrainIndependentlyAsync(
            [async token => { if (_source is not null) await _source.DisposeAsync().AsTask().WaitAsync(token); },
             async token =>
             {
                 await using var connection = new NpgsqlConnection(_management);
                 await connection.OpenAsync(token);
                 await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
                 await command.ExecuteNonQueryAsync(token);
             },
             async token =>
             {
                 await using var connection = new NpgsqlConnection(_management);
                 await connection.OpenAsync(token);
                 await using var command = new NpgsqlCommand("SELECT to_regnamespace(@schema) IS NULL", connection);
                 command.Parameters.AddWithValue("schema", _schema);
                 Assert.True((bool)(await command.ExecuteScalarAsync(token))!);
                 _schemaRemoved(_schema);
             }], TimeSpan.FromSeconds(5), "Async-task guard owned source/schema cleanup failed.");
    }

    private sealed class OwnedContextFactory(NpgsqlDataSource source) : IDbContextFactory<ConduitDbContext>
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
}
#endif
