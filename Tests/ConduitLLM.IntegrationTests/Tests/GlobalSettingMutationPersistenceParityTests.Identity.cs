#if !CONDUIT_NATIVE_AOT
using System.Data;

using ConduitLLM.Configuration.Entities;

using ConduitLLM.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class GlobalSettingMutationPersistenceParityTests
{
    [Theory]
    [InlineData("ef-default")]
    [InlineData("typed")]
    public Task NonzeroIdentityCreateUpdateAndMissingUpdatesShareTheContract(string providerKind) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var observations = new List<object>();
            foreach (var id in new[] { 90, -7, int.MinValue })
            {
                var entity = new GlobalSetting { Id = id, Key = "explicit-" + id, Value = "created",
                    Description = "negative-description", CreatedAt = default, UpdatedAt = OriginalUpdated };
                var before = CallerSnapshot.From(entity);
                var pending = boundary.Repository.CreateAsync(entity, boundary.Token);
                boundary.Track(pending);
                var stamped = AssertIdentityStamped(true, before, entity, allowGeneratedId: false);
                Assert.Equal(id, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                boundary.Observed(pending);
                AssertIdentitySnapshot(stamped, CallerSnapshot.From(entity));
                await AssertIdentityRowAsync(boundary, entity);
                var createdCaller = CallerSnapshot.From(entity);
                Assert.Equal(1, boundary.Counter.Started);
                boundary.Counter.Reset();

                entity.Value = "updated";
                entity.Description = null;
                entity.CreatedAt = OriginalCreated;
                entity.UpdatedAt = OriginalUpdated;
                before = CallerSnapshot.From(entity);
                var update = boundary.Repository.UpdateAsync(entity, boundary.Token);
                boundary.Track(update);
                stamped = AssertIdentityStamped(false, before, entity, allowGeneratedId: false);
                Assert.True(await update.WaitAsync(TimeSpan.FromSeconds(5)));
                boundary.Observed(update);
                AssertIdentitySnapshot(stamped, CallerSnapshot.From(entity));
                await AssertIdentityRowAsync(boundary, entity);
                Assert.Equal(1, boundary.Counter.Started);
                boundary.Counter.Reset();
                observations.Add(new { scenario = "nonzero-create-update", id,
                    createdCaller, updatedCaller = CallerSnapshot.From(entity), explicitIdRetained = true });
            }
            foreach (var missingId in new[] { 900000, -900 })
            {
            var missing = new GlobalSetting { Id = missingId, Key = "missing-" + missingId, Value = "missing",
                Description = "missing-description", CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated };
            var expectedRows = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
            var missingBefore = CallerSnapshot.From(missing);
            var missingTask = boundary.Repository.UpdateAsync(missing, boundary.Token);
            boundary.Track(missingTask);
            var missingStamped = AssertIdentityStamped(false, missingBefore, missing, allowGeneratedId: false);
            var failure = await Record.ExceptionAsync(async () => await missingTask.WaitAsync(TimeSpan.FromSeconds(5)));
            boundary.Observed(missingTask);
            if (providerKind == "typed") Assert.IsType<DBConcurrencyException>(failure);
            else Assert.IsType<DbUpdateConcurrencyException>(failure);
            AssertIdentitySnapshot(missingStamped, CallerSnapshot.From(missing));
            Assert.Equal(expectedRows, await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token));
            Assert.Equal(1, boundary.Counter.Started);
            await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
            observations.Add(new { scenario = "missing-nonzero-update", missingBefore,
                after = CallerSnapshot.From(missing), exceptionType = failure!.GetType().FullName,
                settingSqlStarted = boundary.Counter.Started, databaseUnchanged = true });
            boundary.Counter.Reset();
            }
            WriteIdentityObservation(new { group = "nonzero-identities", providerKind, observations });
        });

    [Theory]
    [InlineData("ef-default")]
    [InlineData("typed")]
    public Task ZeroIdentityUpdateRetainsCreatedAtAndGeneratesOnlyTheId(string providerKind) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var observations = new List<object>();
            foreach (var defaultCreatedAt in new[] { false, true })
            {
                var entity = new GlobalSetting { Id = 0, Key = "zero-update-" + defaultCreatedAt,
                    Value = "inserted-by-update", Description = null,
                    CreatedAt = defaultCreatedAt ? default : OriginalCreated, UpdatedAt = OriginalUpdated };
                var before = CallerSnapshot.From(entity);
                var pending = boundary.Repository.UpdateAsync(entity, boundary.Token);
                boundary.Track(pending);
                var stamped = AssertIdentityStamped(false, before, entity, allowGeneratedId: true);
                Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                boundary.Observed(pending);
                Assert.True(entity.Id > 0);
                AssertIdentitySnapshot(stamped with { Id = entity.Id }, CallerSnapshot.From(entity));
                var row = await AssertIdentityRowAsync(boundary, entity);
                var serverCreatedAt = await ReadIdentityCreatedTextAsync(boundary, entity.Id);
                if (defaultCreatedAt)
                {
                    Assert.Equal(DateTime.MinValue, entity.CreatedAt);
                    Assert.Equal(DateTimeKind.Unspecified, entity.CreatedAt.Kind);
                    Assert.Equal(DateTime.MinValue, row.CreatedAt);
                    Assert.Equal(DateTimeKind.Unspecified, row.CreatedAt.Kind);
                    Assert.Equal("-infinity", serverCreatedAt);
                }
                Assert.Equal(1, boundary.Counter.Started);
                boundary.Counter.Reset();
                await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
                observations.Add(new { scenario = "zero-update", defaultCreatedAt, before,
                    after = CallerSnapshot.From(entity), callerCreatedKind = entity.CreatedAt.Kind.ToString(),
                    persisted = row, persistedCreatedKind = row.CreatedAt.Kind.ToString(), serverCreatedAt });
            }
            WriteIdentityObservation(new { group = "zero-update-created-at", providerKind, observations });
        });

    [Theory]
    [InlineData("ef-default")]
    [InlineData("typed")]
    public Task ExplicitIdentityLeavesSequenceUnchangedAndGeneratedCollisionIsObservable(string providerKind) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var sequenceBefore = await ReadIdentitySequenceAsync(boundary);
            var explicitEntity = new GlobalSetting { Id = 1, Key = "explicit-one", Value = "explicit",
                CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated };
            var explicitTask = boundary.Repository.CreateAsync(explicitEntity, boundary.Token);
            boundary.Track(explicitTask);
            Assert.Equal(1, await explicitTask.WaitAsync(TimeSpan.FromSeconds(5)));
            boundary.Observed(explicitTask);
            await AssertIdentityRowAsync(boundary, explicitEntity);
            var sequenceAfterExplicit = await ReadIdentitySequenceAsync(boundary);
            Assert.Equal(sequenceBefore, sequenceAfterExplicit);
            var beforeRows = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
            var generated = new GlobalSetting { Id = 0, Key = "generated-collision", Value = "generated",
                Description = "collision-description", CreatedAt = default, UpdatedAt = OriginalUpdated };
            var before = CallerSnapshot.From(generated);
            var pending = boundary.Repository.CreateAsync(generated, boundary.Token);
            boundary.Track(pending);
            var stamped = AssertIdentityStamped(true, before, generated, allowGeneratedId: false);
            var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            boundary.Observed(pending);
            if (providerKind == "typed") Assert.IsType<PostgresException>(failure);
            else Assert.IsType<DbUpdateException>(failure);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, FindBoundaryPostgresException(failure!)?.SqlState);
            AssertIdentitySnapshot(stamped, CallerSnapshot.From(generated));
            Assert.Equal(beforeRows, await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token));
            var sequenceAfterFailure = await ReadIdentitySequenceAsync(boundary);
            Assert.Equal(new IdentitySequence(1, false), sequenceBefore);
            Assert.Equal(new IdentitySequence(1, true), sequenceAfterFailure);
            var next = new GlobalSetting { Key = "generated-next", Value = "next", CreatedAt = OriginalCreated };
            var recovery = boundary.Repository.CreateAsync(next, boundary.Token);
            boundary.Track(recovery);
            Assert.Equal(2, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
            boundary.Observed(recovery);
            await AssertIdentityRowAsync(boundary, next);
            var sequenceAfterRecovery = await ReadIdentitySequenceAsync(boundary);
            Assert.Equal(new IdentitySequence(2, true), sequenceAfterRecovery);
            await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
            WriteIdentityObservation(new { scenario = "explicit-generated-sequence", providerKind, sequenceBefore,
                sequenceAfterExplicit, sequenceAfterFailure, sequenceAfterRecovery, callerAfterFailure = generated,
                failureType = failure!.GetType().FullName, sqlState = FindBoundaryPostgresException(failure)?.SqlState,
                nextGeneratedId = next.Id, unchangedDatabaseOnFailure = true });
        });

    [Theory]
    [InlineData("ef-default")]
    [InlineData("typed")]
    public Task DuplicateIdentityAndKeyFailuresRetainCallerAndRows(string providerKind) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var observations = new List<object>();
            foreach (var (scenario, create, entity) in new[]
            {
                ("duplicate-id", true, new GlobalSetting { Id = 100, Key = "different-key", Value = "duplicate-id",
                    Description = "description", CreatedAt = default, UpdatedAt = OriginalUpdated }),
                ("explicit-duplicate-key", true, new GlobalSetting { Id = -7, Key = "seed", Value = "duplicate-key",
                    Description = null, CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated }),
                ("zero-update-duplicate-key-supplied", false, new GlobalSetting { Id = 0, Key = "seed", Value = "duplicate-key",
                    Description = null, CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated }),
                ("zero-update-duplicate-key-default", false, new GlobalSetting { Id = 0, Key = "seed", Value = "duplicate-key",
                    Description = "description", CreatedAt = default, UpdatedAt = OriginalUpdated }),
                ("zero-update-oversized-default", false, new GlobalSetting { Id = 0, Key = "oversized-zero", Value = new string('v', 2001),
                    Description = "description", CreatedAt = default, UpdatedAt = OriginalUpdated })
            })
            {
                var expectedRows = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
                var before = CallerSnapshot.From(entity);
                Task pending = create ? boundary.Repository.CreateAsync(entity, boundary.Token)
                    : boundary.Repository.UpdateAsync(entity, boundary.Token);
                boundary.Track(pending);
                var stamped = AssertIdentityStamped(create, before, entity, allowGeneratedId: false);
                var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                boundary.Observed(pending);
                if (providerKind == "typed") Assert.IsType<PostgresException>(failure);
                else Assert.IsType<DbUpdateException>(failure);
                Assert.Equal(scenario == "zero-update-oversized-default" ? PostgresErrorCodes.StringDataRightTruncation
                    : PostgresErrorCodes.UniqueViolation, FindBoundaryPostgresException(failure!)?.SqlState);
                AssertIdentitySnapshot(stamped, CallerSnapshot.From(entity));
                Assert.Equal(expectedRows, await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token));
                Assert.Equal(1, boundary.Counter.Started);
                boundary.Counter.Reset();
                await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
                observations.Add(new { scenario, before, after = CallerSnapshot.From(entity),
                    createdKind = entity.CreatedAt.Kind.ToString(), failureType = failure!.GetType().FullName,
                    sqlState = FindBoundaryPostgresException(failure)?.SqlState, databaseUnchanged = true });
            }
            WriteIdentityObservation(new { group = "duplicate-identity-key", providerKind, observations });
        });

    [Theory]
    [InlineData("ef-default")]
    [InlineData("typed")]
    public Task IdentityBranchesRespectPreCancellationBeforeCallerMutation(string providerKind) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var expectedRows = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
            var initialPid = await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
            var observations = new List<object>();
            foreach (var (name, create, entity) in IdentityCancellationCases())
            {
                var before = CallerSnapshot.From(entity);
                using var caller = new CancellationTokenSource();
                caller.Cancel();
                var pending = InvokeMutationAsync(boundary.Repository, create ? "create" : "update", entity, caller.Token);
                boundary.Track(pending);
                await AssertCallerCancellationAsync(pending, caller.Token);
                boundary.Observed(pending);
                AssertIdentitySnapshot(before, CallerSnapshot.From(entity));
                Assert.Equal(expectedRows, await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token));
                Assert.Equal(initialPid, await BoundaryPoolUsableAsync(boundary.Source, boundary.Token));
                Assert.Equal(0, boundary.Counter.Started);
                observations.Add(new { name, before, after = CallerSnapshot.From(entity), callerCreatedKind = entity.CreatedAt.Kind.ToString(),
                    exactCallerToken = true, settingSqlStarted = 0, databaseUnchanged = true, samePhysicalBackend = true });
            }
            WriteIdentityObservation(new { group = "identity-pre-cancel", providerKind, observations });
        });

    [Theory]
    [InlineData("ef-default", false)]
    [InlineData("ef-default", true)]
    [InlineData("typed", false)]
    [InlineData("typed", true)]
    public Task IdentityBranchesCancelAtOccupiedPoolOrObservedWriteLock(string providerKind, bool serverWait) =>
        WithBoundaryAsync(providerKind, async boundary =>
        {
            await CalibrateBoundaryCounterAsync(boundary);
            var expectedRows = await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token);
            var observations = new List<object>();
            foreach (var (name, create, entity) in IdentityCancellationCases())
            {
                var initialPid = await BoundaryPoolUsableAsync(boundary.Source, boundary.Token);
                NpgsqlConnection? held = null;
                NpgsqlConnection? blocker = null;
                NpgsqlTransaction? transaction = null;
                Task<object?>? pending = null;
                CallerSnapshot? stamped = null;
                using var caller = new CancellationTokenSource();
                await IntegrationProofCleanup.RunAsync(async () =>
                {
                    if (serverWait)
                    {
                        blocker = new NpgsqlConnection(BoundaryManagementConnection());
                        await blocker.OpenAsync(boundary.Token);
                        transaction = await blocker.BeginTransactionAsync(boundary.Token);
                        await using var command = blocker.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = $"LOCK TABLE \"{boundary.Schema}\".\"GlobalSettings\" IN ACCESS EXCLUSIVE MODE";
                        await command.ExecuteNonQueryAsync(boundary.Token);
                    }
                    else
                    {
                        held = await boundary.Source.OpenConnectionAsync(boundary.Token);
                        Assert.Equal(initialPid, held.ProcessID);
                    }
                    var before = CallerSnapshot.From(entity);
                    pending = InvokeMutationAsync(boundary.Repository, create ? "create" : "update", entity, caller.Token);
                    boundary.Track(pending);
                    Assert.False(pending.IsCompleted);
                    var admitted = AssertIdentityStamped(create, before, entity, allowGeneratedId: false);
                    stamped = admitted;
                    if (serverWait) await WaitForBoundaryWriteLockAsync(boundary, initialPid, blocker!.ProcessID, pending);
                    Assert.Equal(serverWait ? 1 : 0, boundary.Counter.Started);
                    caller.Cancel();
                    await AssertCallerCancellationAsync(pending, caller.Token);
                    boundary.Observed(pending);
                    AssertIdentitySnapshot(admitted, CallerSnapshot.From(entity));
                    if (serverWait)
                    {
                        Assert.Equal(initialPid, await BoundaryPoolUsableAsync(boundary.Source, boundary.Token));
                        Assert.NotNull(transaction!.Connection);
                    }
                }, async () => await IntegrationProofCleanup.DrainIndependentlyAsync(
                [
                    async token => { caller.Cancel(); if (pending is not null) await boundary.DrainAsync(pending, token); },
                    async token => { if (held is not null) await held.DisposeAsync().AsTask().WaitAsync(token); },
                    async token => { if (transaction is not null) await transaction.RollbackAsync(token); },
                    async token => { if (blocker is not null) await blocker.DisposeAsync().AsTask().WaitAsync(token); },
                    async token => { if (pending is not null) await boundary.DrainAsync(pending, token); },
                    async token => { if (transaction is not null) await transaction.DisposeAsync().AsTask().WaitAsync(token); }
                ], CleanupLimit, "The identity write, slot and blocker were not independently joined."),
                    "The identity cancellation proof failed and its cleanup also failed.");
                Assert.Equal(expectedRows, await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token));
                Assert.Equal(initialPid, await BoundaryPoolUsableAsync(boundary.Source, boundary.Token));
                Assert.NotNull(stamped);
                AssertIdentitySnapshot(stamped, CallerSnapshot.From(entity));
                observations.Add(new { name, create, beforeCancel = stamped, afterCancel = CallerSnapshot.From(entity),
                    callerCreatedKind = entity.CreatedAt.Kind.ToString(), serverWait, settingSqlStarted = boundary.Counter.Started,
                    exactCallerToken = true, databaseUnchanged = true, samePhysicalBackend = true });
                boundary.Counter.Reset();
            }
            WriteIdentityObservation(new { group = "identity-pool-or-sql-cancel", providerKind, serverWait, observations });
        });

    private static IEnumerable<(string Name, bool Create, GlobalSetting Entity)> IdentityCancellationCases()
    {
        yield return ("generated-create", true, new GlobalSetting { Key = "generated-create", Value = "value",
            CreatedAt = default, UpdatedAt = OriginalUpdated });
        yield return ("explicit-positive-create", true, new GlobalSetting { Id = 90, Key = "positive-create", Value = "value",
            Description = "description", CreatedAt = default, UpdatedAt = OriginalUpdated });
        yield return ("explicit-negative-create", true, new GlobalSetting { Id = -7, Key = "negative-create", Value = "value",
            CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated });
        yield return ("zero-update-supplied", false, new GlobalSetting { Id = 0, Key = "zero-supplied", Value = "value",
            Description = "description", CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated });
        yield return ("zero-update-default", false, new GlobalSetting { Id = 0, Key = "zero-default", Value = "value",
            CreatedAt = default, UpdatedAt = OriginalUpdated });
        yield return ("existing-update", false, new GlobalSetting { Id = 100, Key = "seed", Value = "changed",
            CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated });
        yield return ("missing-positive-update", false, new GlobalSetting { Id = 900000, Key = "missing-positive", Value = "value",
            Description = "description", CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated });
        yield return ("missing-negative-update", false, new GlobalSetting { Id = -900, Key = "missing-negative", Value = "value",
            CreatedAt = OriginalCreated, UpdatedAt = OriginalUpdated });
    }

    private static CallerSnapshot AssertIdentityStamped(bool create, CallerSnapshot before, GlobalSetting entity, bool allowGeneratedId)
    {
        var actual = CallerSnapshot.From(entity);
        if (!allowGeneratedId) Assert.Equal(before.Id, actual.Id);
        Assert.Equal(before.Key, actual.Key);
        Assert.Equal(before.Value, actual.Value);
        Assert.Equal(before.Description, actual.Description);
        Assert.Equal(DateTimeKind.Utc, actual.UpdatedAt.Kind);
        Assert.NotEqual(before.UpdatedAt, actual.UpdatedAt);
        if (create && before.CreatedAt == default)
        {
            Assert.Equal(actual.UpdatedAt, actual.CreatedAt);
            Assert.Equal(DateTimeKind.Utc, actual.CreatedAt.Kind);
        }
        else
        {
            Assert.Equal(before.CreatedAt, actual.CreatedAt);
            Assert.Equal(before.CreatedAt.Kind, actual.CreatedAt.Kind);
        }
        return actual;
    }

    private static void AssertIdentitySnapshot(CallerSnapshot expected, CallerSnapshot actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(expected.CreatedAt.Kind, actual.CreatedAt.Kind);
        Assert.Equal(expected.UpdatedAt.Kind, actual.UpdatedAt.Kind);
    }

    private async Task<CallerSnapshot> AssertIdentityRowAsync(MutationBoundary boundary, GlobalSetting entity)
    {
        var actual = Assert.Single(await ReadBoundaryRowsAsync(boundary.Schema, boundary.Token), row => row.Id == entity.Id);
        var expected = CallerSnapshot.From(entity).Wire();
        if (entity.CreatedAt == DateTime.MinValue) expected = expected with { CreatedAt = entity.CreatedAt };
        Assert.Equal(expected, actual);
        Assert.Equal(entity.CreatedAt == DateTime.MinValue ? DateTimeKind.Unspecified : DateTimeKind.Utc, actual.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, actual.UpdatedAt.Kind);
        return actual;
    }

    private async Task<string> ReadIdentityCreatedTextAsync(MutationBoundary boundary, int id)
    {
        await using var connection = new NpgsqlConnection(BoundaryManagementConnection());
        await connection.OpenAsync(boundary.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"CreatedAt\"::text FROM \"{boundary.Schema}\".\"GlobalSettings\" WHERE \"Id\"=@id";
        command.Parameters.AddWithValue("id", id);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(boundary.Token));
    }

    private async Task<IdentitySequence> ReadIdentitySequenceAsync(MutationBoundary boundary)
    {
        await using var connection = new NpgsqlConnection(BoundaryManagementConnection());
        await connection.OpenAsync(boundary.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT last_value, is_called FROM \"{boundary.Schema}\".\"GlobalSettings_Id_seq\"";
        await using var reader = await command.ExecuteReaderAsync(boundary.Token);
        Assert.True(await reader.ReadAsync(boundary.Token));
        var sequence = new IdentitySequence(reader.GetInt64(0), reader.GetBoolean(1));
        Assert.False(await reader.ReadAsync(boundary.Token));
        return sequence;
    }

    private sealed record IdentitySequence(long LastValue, bool IsCalled);
}
#endif
