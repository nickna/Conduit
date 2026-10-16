using System.Globalization;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;
using ConduitLLM.SchemaStartupFixtures;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>Migration lifecycle and the ledger-commit/result-marker crash window on real PostgreSQL.</summary>
[Collection("Postgres advisory locks")]
public sealed class FunctionIdempotencyStorePersistenceParityTests(PostgresLockTestContainerFixture postgres)
{
    private const string PreviousMigration = "20261015050045_PairFunctionInverseRelationships";
    private const decimal Cost = 0.12500001m;
    private static readonly DateTime OriginalTime = new(2026, 10, 15, 7, 23, 45, 123, DateTimeKind.Utc);
    private const string Result = """{"id":"preserved-result","actual_cost":0.12500001,"response_json":"{\"output\":\"雪\"}"}""";

    [Fact]
    public async Task UpgradeRollbackAndReupgradeKeepExistingVirtualKeysAndApplyClaimConstraints()
    {
        await using var database = await Database.CreateAsync(postgres.ConnectionString, previous: true);
        Assert.Equal(PreviousMigration, database.Schema.AppliedVersion);
        Assert.False(await database.HasClaimsTableAsync());
        await database.MigrateAsync(ConduitSchemaVersion.Current);
        Assert.True(await database.HasClaimsTableAsync());
        var store = database.Store();
        var claim = await store.ClaimAsync(database.KeyId, new string('a', 64), new string('b', 64), database.Token);
        Assert.True(claim.IsOwner);
        Assert.True(await store.MarkDispatchedAsync(claim, database.Token));
        Assert.True(await store.CompleteAsync(claim, Result, database.Token));
        await using (var corrupt = database.Source.CreateCommand("""
                         UPDATE "FunctionExecutionClaims" SET "State" = 'uncertain'
                         WHERE "ExecutionId" = @executionId
                         """))
        {
            corrupt.Parameters.AddWithValue("executionId", claim.ExecutionId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => corrupt.ExecuteNonQueryAsync(database.Token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        await database.MigrateAsync(PreviousMigration);
        Assert.False(await database.HasClaimsTableAsync());
        Assert.NotNull(await new NpgsqlVirtualKeyRuntimeStore(database.Source).GetByIdAsync(database.KeyId, database.Token));
        await database.MigrateAsync(ConduitSchemaVersion.Current);
        Assert.True(await database.HasClaimsTableAsync());
        Assert.Null(await store.FindAsync(database.KeyId, claim.KeyHash, database.Token));
        Assert.NotNull(await new NpgsqlVirtualKeyRuntimeStore(database.Source).GetByIdAsync(database.KeyId, database.Token));
    }

    [Fact]
    public async Task LedgerCommitBeforeBilledMarkerRetriesOnceAgainstTheOriginalGroupAndHour()
    {
        await using var database = await Database.CreateAsync(postgres.ConnectionString);
        var ledger = new NpgsqlVirtualKeyRuntimeStore(database.Source);
        var interruptedLedger = new CommitThenThrowStore(ledger);
        var interrupted = new NpgsqlFunctionExecutionIdempotencyStore(database.Source, interruptedLedger);
        var claim = await interrupted.ClaimAsync(database.KeyId, new string('c', 64), new string('d', 64), database.Token);
        await using (var originalTime = database.Source.CreateCommand("""
                         UPDATE "FunctionExecutionClaims" SET "CreatedAtUtc" = @createdAt WHERE "ExecutionId" = @executionId;
                         UPDATE "VirtualKeys" SET "VirtualKeyGroupId" = @newGroup WHERE "Id" = @keyId;
                         """))
        {
            originalTime.Parameters.AddWithValue("createdAt", OriginalTime);
            originalTime.Parameters.AddWithValue("executionId", claim.ExecutionId);
            originalTime.Parameters.AddWithValue("newGroup", database.OtherGroupId);
            originalTime.Parameters.AddWithValue("keyId", database.KeyId);
            await originalTime.ExecuteNonQueryAsync(database.Token);
        }
        claim = (await interrupted.FindAsync(database.KeyId, claim.KeyHash, database.Token))!;
        Assert.Equal(OriginalTime, claim.CreatedAtUtc);
        Assert.Equal(database.OriginalGroupId, claim.BillingGroupId);
        Assert.True(await interrupted.MarkDispatchedAsync(claim, database.Token));
        Assert.True(await interrupted.CompleteAsync(claim, Result, database.Token));
        await Assert.ThrowsAsync<IOException>(() => interrupted.SettleBillingAsync(claim, Cost, database.Token));
        Assert.Null((await interrupted.FindAsync(database.KeyId, claim.KeyHash, database.Token))!.BilledAtUtc);
        await AssertLedgerAsync(database, claim);

        // A fresh process has no knowledge of the accepted first debit. PostgreSQL's ledger
        // identity must acknowledge every retry before any caller can mark the claim billed.
        var restarted = database.Store();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => restarted.SettleBillingAsync(
            claim with { BillingGroupId = database.OtherGroupId, CreatedAtUtc = OriginalTime.AddDays(1) }, Cost, database.Token)));
        var completed = (await restarted.FindAsync(database.KeyId, claim.KeyHash, database.Token))!;
        Assert.Equal(FunctionExecutionClaimState.Completed, completed.State);
        Assert.NotNull(completed.BilledAtUtc);
        Assert.Equal(System.Text.Json.JsonDocument.Parse(Result).RootElement.GetProperty("response_json").GetString(),
            System.Text.Json.JsonDocument.Parse(completed.ResultJson!).RootElement.GetProperty("response_json").GetString());
        Assert.False(await restarted.MarkUncertainAsync(claim, database.Token));
        Assert.False(await restarted.CompleteAsync(claim, "{}", database.Token));
        await AssertLedgerAsync(database, claim);
    }

    [Fact]
    public async Task ZeroCostAndUncertainClaimsRetainTheirPermanentReplayProtectionAfterKeyDeletion()
    {
        await using var database = await Database.CreateAsync(postgres.ConnectionString);
        var store = database.Store();
        var completed = await store.ClaimAsync(database.KeyId, new string('e', 64), new string('f', 64), database.Token);
        Assert.True(await store.MarkDispatchedAsync(completed, database.Token));
        Assert.True(await store.CompleteAsync(completed, "{}", database.Token));
        await store.SettleBillingAsync(completed, 0, database.Token);
        Assert.NotNull((await store.FindAsync(database.KeyId, completed.KeyHash, database.Token))!.BilledAtUtc);
        var uncertain = await store.ClaimAsync(database.KeyId, new string('1', 64), new string('2', 64), database.Token);
        Assert.True(await store.MarkDispatchedAsync(uncertain, database.Token));
        Assert.True(await store.MarkUncertainAsync(uncertain, database.Token));
        Assert.False(await store.CompleteAsync(uncertain, "{}", database.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SettleBillingAsync(uncertain, Cost, database.Token));

        await using (var delete = database.Source.CreateCommand("""
                         DELETE FROM "VirtualKeys" WHERE "Id" = @keyId;
                         DELETE FROM "VirtualKeyGroups" WHERE "Id" IN (@originalGroupId, @otherGroupId);
                         """))
        {
            delete.Parameters.AddWithValue("keyId", database.KeyId);
            delete.Parameters.AddWithValue("originalGroupId", database.OriginalGroupId);
            delete.Parameters.AddWithValue("otherGroupId", database.OtherGroupId);
            await delete.ExecuteNonQueryAsync(database.Token);
        }
        foreach (var original in new[] { completed, uncertain })
        {
            var replay = await store.ClaimAsync(database.KeyId, original.KeyHash, original.Fingerprint, database.Token);
            Assert.False(replay.IsOwner);
            Assert.Equal(original.ExecutionId, replay.ExecutionId);
        }
        await using var count = database.Source.CreateCommand("""
            SELECT COUNT(*) FROM "VirtualKeyGroupTransactions"
            """);
        Assert.Equal(0L, await count.ExecuteScalarAsync(database.Token));
    }

    private static async Task AssertLedgerAsync(Database database, FunctionExecutionClaim claim)
    {
        await using var command = database.Source.CreateCommand("""
            SELECT ledger."VirtualKeyGroupId", ledger."Amount", ledger."IdempotencyKey",
                ledger."ReferenceType", ledger."ReferenceId", ledger."BillingWindowStartUtc",
                original_group."Balance", original_group."LifetimeSpent", other_group."Balance",
                (SELECT COUNT(*) FROM "VirtualKeyGroupTransactions")
            FROM "VirtualKeyGroupTransactions" AS ledger
            INNER JOIN "VirtualKeyGroups" AS original_group ON original_group."Id" = @originalGroupId
            INNER JOIN "VirtualKeyGroups" AS other_group ON other_group."Id" = @otherGroupId
            """);
        command.Parameters.AddWithValue("originalGroupId", database.OriginalGroupId);
        command.Parameters.AddWithValue("otherGroupId", database.OtherGroupId);
        await using var reader = await command.ExecuteReaderAsync(database.Token);
        Assert.True(await reader.ReadAsync(database.Token));
        Assert.Equal(database.OriginalGroupId, reader.GetInt32(0));
        Assert.Equal(Cost, reader.GetDecimal(1));
        Assert.Equal($"function-execution:{claim.ExecutionId:N}", reader.GetString(2));
        Assert.Equal((int)VirtualKeyBalanceReferenceType.VirtualKey, reader.GetInt32(3));
        Assert.Equal(database.KeyId.ToString(CultureInfo.InvariantCulture), reader.GetString(4));
        Assert.Equal(new DateTime(2026, 10, 15, 7, 0, 0, DateTimeKind.Utc), reader.GetDateTime(5));
        Assert.Equal(100 - Cost, reader.GetDecimal(6));
        Assert.Equal(Cost, reader.GetDecimal(7));
        Assert.Equal(100, reader.GetDecimal(8));
        Assert.Equal(1, reader.GetInt64(9));
        Assert.False(await reader.ReadAsync(database.Token));
    }

    private sealed class CommitThenThrowStore(IVirtualKeyRuntimeStore inner) : IVirtualKeyRuntimeStore
    {
        private int _interrupted;
        public async Task<VirtualKeyBalanceAdjustmentResult> AdjustBalanceAsync(
            VirtualKeyBalanceAdjustment adjustment, CancellationToken cancellationToken = default)
        {
            var result = await inner.AdjustBalanceAsync(adjustment, cancellationToken);
            if (Interlocked.Exchange(ref _interrupted, 1) == 0)
                throw new IOException("Simulated acknowledgement loss after the ledger committed.");
            return result;
        }
        public Task<VirtualKeyRuntimeRecord?> GetByHashAsync(string keyHash, CancellationToken cancellationToken = default) =>
            inner.GetByHashAsync(keyHash, cancellationToken);
        public Task<VirtualKeyRuntimeRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);
        public Task<IReadOnlyList<string>> GetKeyHashesByGroupIdAsync(int groupId, CancellationToken cancellationToken = default) =>
            inner.GetKeyHashesByGroupIdAsync(groupId, cancellationToken);
        public Task<bool> TouchAsync(int id, DateTime updatedAt, CancellationToken cancellationToken = default) =>
            inner.TouchAsync(id, updatedAt, cancellationToken);
    }

    private sealed class Database(string management, SchemaStartupDatabase schema) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromMinutes(3));
        internal SchemaStartupDatabase Schema => schema;
        internal NpgsqlDataSource Source { get; } = NpgsqlDataSource.Create(schema.ConnectionString);
        internal CancellationToken Token => _deadline.Token;
        internal int KeyId { get; private set; }
        internal int OriginalGroupId { get; private set; }
        internal int OtherGroupId { get; private set; }
        internal NpgsqlFunctionExecutionIdempotencyStore Store() => new(Source, new NpgsqlVirtualKeyRuntimeStore(Source));
        internal ConduitDbContext Context() => new(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(schema.ConnectionString).Options);

        internal static async Task<Database> CreateAsync(string management, bool previous = false)
        {
            var schema = await SchemaStartupDatabase.CreateAsync(management, previousSchema: previous);
            var database = new Database(management, schema);
            try
            {
                if (!previous) await database.MigrateAsync(ConduitSchemaVersion.Current);
                await using var context = database.Context();
                var original = new VirtualKeyGroup { GroupName = "original-claim-group", Balance = 100, LifetimeCreditsAdded = 100 };
                var other = new VirtualKeyGroup { GroupName = "changed-key-group", Balance = 100, LifetimeCreditsAdded = 100 };
                var key = new VirtualKey { KeyName = "claim-owner", KeyHash = Guid.NewGuid().ToString("N"), VirtualKeyGroup = original };
                context.AddRange(key, other);
                await context.SaveChangesAsync(database.Token);
                database.KeyId = key.Id;
                database.OriginalGroupId = original.Id;
                database.OtherGroupId = other.Id;
                return database;
            }
            catch { await database.DisposeAsync(); throw; }
        }

        internal async Task MigrateAsync(string migration)
        {
            await using var context = Context();
            await context.GetService<IMigrator>().MigrateAsync(migration, Token);
        }

        internal async Task<bool> HasClaimsTableAsync()
        {
            await using var command = Source.CreateCommand("""SELECT to_regclass('"FunctionExecutionClaims"') IS NOT NULL""");
            return (bool)(await command.ExecuteScalarAsync(Token))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            _deadline.Dispose();
            await SchemaStartupDatabase.DropAsync(management, schema.Name);
        }
    }
}
