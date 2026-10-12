using System.Globalization;
using System.Text.RegularExpressions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Npgsql;

using StackExchange.Redis;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class LegacyLedgerReplayPersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    private const decimal OriginalAmount = 0.12500001m;
    private static readonly DateTime BillingWindow = new(2026, 8, 27, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CreatedAt = new(2026, 8, 27, 21, 15, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedLegacyReplay_RetainsClaimUntilApprovalThenAcknowledgesWithoutSecondDebitAsync(bool typedNpgsql)
    {
        await using var schema = await CreateSchemaAsync(seedLegacy: true);
        await using var dataSource = NpgsqlDataSource.Create(schema.ConnectionString);
        var store = CreateStore(schema, dataSource, typedNpgsql);
        var adjustment = LegacyAdjustment(schema);
        var originalLedger = await ReadLedgerAsync(schema);
        var originalBalance = await ReadBalanceAsync(schema);
        Assert.Equal(0.125000m, originalLedger.Amount);
        Assert.Null(originalLedger.OriginalAmount);

        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        RedisKey claim = $"processing_spend_window_units:group:{schema.GroupId}:window:2026082721:claim:{schema.ClaimId}";
        RedisKey usage = $"processing_key_usage_window_units:group:{schema.GroupId}:window:2026082721:key:1:claim:{schema.ClaimId}";
        RedisKey total = $"pending_spend_window_total_units:group:{schema.GroupId}";
        var originalUnits = 12_500_001L;
        await database.StringSetAsync([new(claim, originalUnits), new(usage, originalUnits), new(total, originalUnits)]);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var redisFactory = new RedisConnectionFactory(
            Microsoft.Extensions.Options.Options.Create(new CacheOptions { RedisConnectionString = fixture.RedisConnectionString }),
            NullLogger<RedisConnectionFactory>.Instance);
        await using var batch = new BatchSpendUpdateService(
            services.GetRequiredService<IServiceScopeFactory>(), redisFactory,
            Microsoft.Extensions.Options.Options.Create(new BatchSpendingOptions { FlushIntervalSeconds = 3600, MinimumIntervalSeconds = 1, MaximumIntervalSeconds = 7200 }),
            NullLogger<BatchSpendUpdateService>.Instance, Mock.Of<IBillingAlertingService>(), runtimeStore: store);

        try
        {
            await Assert.ThrowsAsync<VirtualKeyBalanceConflictException>(() => batch.FlushPendingUpdatesAsync());
            Assert.Equal(originalUnits, (long)await database.StringGetAsync(claim));
            Assert.Equal(originalUnits, (long)await database.StringGetAsync(usage));
            Assert.Equal(originalUnits, (long)await database.StringGetAsync(total));
            Assert.Equal(originalLedger, await ReadLedgerAsync(schema));
            Assert.Equal(originalBalance, await ReadBalanceAsync(schema));

            await ApproveAsync(schema);
            var approved = await ReadLedgerAsync(schema);
            Assert.Equal(OriginalAmount, approved.OriginalAmount);
            Assert.Equal("integration-operator", approved.VerifiedBy);
            Assert.Equal($"archived-claim:{schema.ClaimId}", approved.Evidence);
            Assert.NotNull(approved.VerifiedAt);
            Assert.Equal(originalLedger.Amount, approved.Amount);
            Assert.Equal(originalLedger.BalanceAfter, approved.BalanceAfter);

            // A different operation in the same six-decimal bucket remains a conflict.
            await Assert.ThrowsAsync<VirtualKeyBalanceConflictException>(() => store.AdjustBalanceAsync(
                adjustment with { Amount = -0.12500002m }));
            await database.StringSetAsync(claim, 12_500_002L);
            await Assert.ThrowsAsync<VirtualKeyBalanceConflictException>(() => batch.FlushPendingUpdatesAsync());
            Assert.Equal(12_500_002L, (long)await database.StringGetAsync(claim));
            Assert.Equal(approved, await ReadLedgerAsync(schema));
            await database.StringSetAsync(claim, originalUnits);

            var replay = await store.AdjustBalanceAsync(adjustment);
            Assert.False(replay.Applied);
            Assert.Equal(originalBalance.Balance, replay.NewBalance);
            Assert.Equal(1, await batch.FlushPendingUpdatesAsync());
            Assert.False(await database.KeyExistsAsync(claim));
            Assert.False(await database.KeyExistsAsync(usage));
            Assert.False(await database.KeyExistsAsync(total));
            Assert.Equal(0, await batch.FlushPendingUpdatesAsync());
            Assert.Equal(approved, await ReadLedgerAsync(schema));
            Assert.Equal(originalBalance, await ReadBalanceAsync(schema));
            Assert.Equal(1, await CountLedgerAsync(schema));
        }
        finally
        {
            await database.KeyDeleteAsync([claim, usage, total]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewExactEightDecimalDebit_ConcurrentReplayAppliesOnceAndRejectsDifferentIdentityAsync(bool typedNpgsql)
    {
        await using var schema = await CreateSchemaAsync(seedLegacy: false);
        await using var dataSource = NpgsqlDataSource.Create(schema.ConnectionString);
        var store = CreateStore(schema, dataSource, typedNpgsql);
        var adjustment = LegacyAdjustment(schema);

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => store.AdjustBalanceAsync(adjustment)));
        Assert.Single(results, result => result.Applied);
        var originalLedger = await ReadLedgerAsync(schema);
        var originalBalance = await ReadBalanceAsync(schema);
        Assert.Equal(OriginalAmount, originalLedger.Amount);
        Assert.Equal(OriginalAmount, originalLedger.OriginalAmount);
        Assert.Null(originalLedger.VerifiedBy);
        Assert.Null(originalLedger.Evidence);
        Assert.Null(originalLedger.VerifiedAt);
        Assert.Equal(100m - OriginalAmount, originalBalance.Balance);
        Assert.Equal(OriginalAmount, originalBalance.LifetimeSpent);

        var otherBalance = await ReadBalanceAsync(schema, schema.GroupId + 1);
        VirtualKeyBalanceAdjustment[] conflicts =
        [
            adjustment with { Amount = -0.12500002m },
            adjustment with { Amount = OriginalAmount },
            adjustment with { GroupId = schema.GroupId + 1 },
            adjustment with { ReferenceType = VirtualKeyBalanceReferenceType.VirtualKey },
            adjustment with { ReferenceId = "different-claim" },
            adjustment with { BillingWindowStartUtc = BillingWindow.AddHours(1) }
        ];
        foreach (var conflict in conflicts)
        {
            await Assert.ThrowsAsync<VirtualKeyBalanceConflictException>(() => store.AdjustBalanceAsync(conflict));
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AdjustBalanceAsync(
            adjustment with { Amount = -0.125000011m, IdempotencyKey = "batch-spend:too-precise" }));
        Assert.Equal(originalLedger, await ReadLedgerAsync(schema));
        Assert.Equal(originalBalance, await ReadBalanceAsync(schema));
        Assert.Equal(1, await CountLedgerAsync(schema));
        Assert.Equal(otherBalance, await ReadBalanceAsync(schema, schema.GroupId + 1));
    }

    [Theory]
    [InlineData(false, "0.12500001", "0.125000")]
    [InlineData(true, "0.12500001", "0.125000")]
    [InlineData(false, "0.12500050", "0.125001")]
    [InlineData(true, "0.12500050", "0.125001")]
    public async Task NewDebit_SixDecimalLedgerRecordsExactOriginalAndRejectsSameBucketReplayAsync(
        bool typedNpgsql, string amountText, string storedAmountText)
    {
        var amount = decimal.Parse(amountText, CultureInfo.InvariantCulture);
        var storedAmount = decimal.Parse(storedAmountText, CultureInfo.InvariantCulture);
        await using var schema = await CreateSchemaAsync(seedLegacy: false, widenLedger: false);
        await using var dataSource = NpgsqlDataSource.Create(schema.ConnectionString);
        var store = CreateStore(schema, dataSource, typedNpgsql);
        var adjustment = LegacyAdjustment(schema) with { Amount = -amount };

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => store.AdjustBalanceAsync(adjustment)));
        Assert.Single(results, result => result.Applied);
        var ledger = await ReadLedgerAsync(schema);
        var balance = await ReadBalanceAsync(schema);
        Assert.Equal(storedAmount, ledger.Amount);
        Assert.Equal(amount, ledger.OriginalAmount);
        Assert.Equal(Math.Round(100m - amount, 6, MidpointRounding.AwayFromZero), ledger.BalanceAfter);
        Assert.Null(ledger.VerifiedBy);
        Assert.Null(ledger.Evidence);
        Assert.Null(ledger.VerifiedAt);
        Assert.Equal(100m - amount, balance.Balance);
        Assert.Equal(amount, balance.LifetimeSpent);

        var replay = await store.AdjustBalanceAsync(adjustment);
        Assert.False(replay.Applied);
        Assert.Equal(balance.Balance, replay.NewBalance);
        Assert.Equal(storedAmount, Math.Round(amount + 0.00000001m, 6, MidpointRounding.AwayFromZero));
        await Assert.ThrowsAsync<VirtualKeyBalanceConflictException>(() => store.AdjustBalanceAsync(
            adjustment with { Amount = -(amount + 0.00000001m) }));
        Assert.Equal(ledger, await ReadLedgerAsync(schema));
        Assert.Equal(balance, await ReadBalanceAsync(schema));
        Assert.Equal(1, await CountLedgerAsync(schema));
    }
    [Fact]
    public async Task RecoveryScript_WrongSnapshotOrEvidenceCannotApproveOrOverwriteLedgerAsync()
    {
        await using var schema = await CreateSchemaAsync(seedLegacy: true);
        var originalLedger = await ReadLedgerAsync(schema);
        var originalBalance = await ReadBalanceAsync(schema);
        (string Name, string Value)[] invalidApprovals =
        [
            ("ledger_id", "999999999"),
            ("idempotency_key", "batch-spend:different-claim"),
            ("group_id", (schema.GroupId + 1).ToString(CultureInfo.InvariantCulture)),
            ("transaction_type", "1"),
            ("reference_type", "2"),
            ("reference_id", "different-claim"),
            ("reference_id", "\t\r\n"),
            ("billing_window_start_utc", BillingWindow.AddHours(1).ToString("O")),
            ("created_at_utc", CreatedAt.AddSeconds(1).ToString("O")),
            ("balance_after", "99"),
            ("stored_amount", "0.124999"),
            ("original_amount", "0.12500000"),
            ("original_amount", "0.12500201"),
            ("original_amount", "0.125000011"),
            ("original_amount", "-0.12500001"),
            ("original_amount", "0"),
            ("original_amount", "1000000000000"),
            ("verified_by", " "),
            ("verified_by", "\t\r\n"),
            ("verified_by", new string('x', 101)),
            ("evidence_reference", " "),
            ("evidence_reference", "\t\r\n"),
            ("evidence_reference", new string('x', 501))
        ];
        foreach (var invalid in invalidApprovals)
        {
            await Assert.ThrowsAsync<PostgresException>(() => ApproveAsync(schema, invalid.Name, invalid.Value));
            Assert.Equal(originalLedger, await ReadLedgerAsync(schema));
            Assert.Equal(originalBalance, await ReadBalanceAsync(schema));
        }

        await ApproveAsync(schema);
        var approved = await ReadLedgerAsync(schema);
        await ApproveAsync(schema); // A retry preserves the recorded actor, evidence and timestamp.
        Assert.Equal(approved, await ReadLedgerAsync(schema));
        foreach (var change in new[]
        {
            ("original_amount", "0.12500002"),
            ("verified_by", "different-operator"),
            ("evidence_reference", "different-evidence")
        })
        {
            await Assert.ThrowsAsync<PostgresException>(() => ApproveAsync(schema, change.Item1, change.Item2));
            Assert.Equal(approved, await ReadLedgerAsync(schema));
        }
        Assert.Equal(originalBalance, await ReadBalanceAsync(schema));
    }

    [Fact]
    public async Task RecoveryScript_IncompleteExistingAuditOrDeletedLedgerRequiresInvestigationAsync()
    {
        await using var schema = await CreateSchemaAsync(seedLegacy: true);
        var originalBalance = await ReadBalanceAsync(schema);
        foreach (var mutation in new[]
        {
            "\"OriginalAmountVerifiedBy\" = 'unverified-operator'",
            "\"OriginalAmountEvidence\" = 'incomplete-evidence'",
            "\"OriginalAmountVerifiedAtUtc\" = CURRENT_TIMESTAMP",
            "\"IsDeleted\" = true",
            "\"DeletedAt\" = CURRENT_TIMESTAMP"
        })
        {
            await ExecuteAsync(schema.ConnectionString, $"UPDATE \"VirtualKeyGroupTransactions\" SET {mutation}");
            var before = await ReadLedgerAsync(schema);
            await Assert.ThrowsAsync<PostgresException>(() => ApproveAsync(schema));
            Assert.Equal(before, await ReadLedgerAsync(schema));
            Assert.Equal(originalBalance, await ReadBalanceAsync(schema));
            await ExecuteAsync(schema.ConnectionString, """
                UPDATE "VirtualKeyGroupTransactions" SET "OriginalAmountVerifiedBy" = NULL,
                    "OriginalAmountEvidence" = NULL, "OriginalAmountVerifiedAtUtc" = NULL,
                    "IsDeleted" = false, "DeletedAt" = NULL
                """);
        }
    }

    [Fact]
    public async Task RecoveryScript_ConcurrentDifferentApprovals_RecordOneImmutableEvidenceSetAsync()
    {
        await using var schema = await CreateSchemaAsync(seedLegacy: true);
        var originalBalance = await ReadBalanceAsync(schema);
        var originalLedger = await ReadLedgerAsync(schema);
        async Task<(string Actor, PostgresException? Error)> ApproveForActorAsync(string actor)
        {
            try
            {
                await ApproveAsync(schema, "verified_by", actor);
                return (actor, null);
            }
            catch (PostgresException error)
            {
                return (actor, error);
            }
        }
        var results = await Task.WhenAll(new[] { "operator-one", "operator-two" }
            .Select(ApproveForActorAsync));

        var winner = Assert.Single(results, result => result.Error is null);
        Assert.Single(results, result => result.Error is PostgresException);
        var approved = await ReadLedgerAsync(schema);
        Assert.Equal(winner.Actor, approved.VerifiedBy);
        Assert.Equal(OriginalAmount, approved.OriginalAmount);
        Assert.Equal($"archived-claim:{schema.ClaimId}", approved.Evidence);
        Assert.NotNull(approved.VerifiedAt);
        Assert.Equal(originalLedger.Amount, approved.Amount);
        Assert.Equal(originalLedger.BalanceAfter, approved.BalanceAfter);
        Assert.Equal(originalBalance, await ReadBalanceAsync(schema));
        await ApproveAsync(schema, "verified_by", winner.Actor);
        Assert.Equal(approved, await ReadLedgerAsync(schema));
    }
    private static VirtualKeyBalanceAdjustment LegacyAdjustment(TestSchema schema) => new(
        schema.GroupId, -OriginalAmount, "original claim", "System",
        VirtualKeyBalanceReferenceType.System, schema.ClaimId, $"batch-spend:{schema.ClaimId}", BillingWindow);

    private static IVirtualKeyRuntimeStore CreateStore(TestSchema schema, NpgsqlDataSource source, bool typedNpgsql) =>
        typedNpgsql ? new NpgsqlVirtualKeyRuntimeStore(source) : new EfVirtualKeyRuntimeStore(new TestDbContextFactory(schema.ConnectionString));

    private static async Task ApproveAsync(TestSchema schema, string? changedVariable = null, string? changedValue = null)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "approve-legacy-ledger-replay.sql"));
        var values = new Dictionary<string, string>
        {
            ["ledger_id"] = "1", ["idempotency_key"] = $"batch-spend:{schema.ClaimId}",
            ["group_id"] = schema.GroupId.ToString(CultureInfo.InvariantCulture),
            ["transaction_type"] = "2", ["reference_type"] = "3", ["reference_id"] = schema.ClaimId,
            ["billing_window_start_utc"] = BillingWindow.ToString("O"), ["created_at_utc"] = CreatedAt.ToString("O"),
            ["balance_after"] = "99.875000", ["stored_amount"] = "0.125000",
            ["original_amount"] = "0.12500001", ["verified_by"] = "integration-operator",
            ["evidence_reference"] = $"archived-claim:{schema.ClaimId}"
        };
        if (changedVariable is not null) values[changedVariable] = changedValue!;
        // Exercise the shipped SQL verbatim, adapting only psql's quoted input variables
        // to bound PostgreSQL parameters before its DO block executes.
        script = Regex.Replace(script, @":'(?<name>[a-z_]+)'", match => "@" + match.Groups["name"].Value);
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(script, connection);
        foreach (var value in values) command.Parameters.AddWithValue(value.Key, value.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<TestSchema> CreateSchemaAsync(bool seedLegacy, bool widenLedger = true)
    {
        var name = $"legacy_replay_{Guid.NewGuid():N}";
        var groupId = Random.Shared.Next(1_000_000, 2_000_000);
        var claimId = Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = name }.ConnectionString;
        await ExecuteAsync(fixture.ConnectionString, $"CREATE SCHEMA \"{name}\"");
        var schema = new TestSchema(name, connectionString, fixture.ConnectionString, groupId, claimId);
        try
        {
            await ExecuteAsync(connectionString, """
                CREATE TABLE "VirtualKeyGroups" (
                    "Id" integer PRIMARY KEY, "ExternalGroupId" varchar(100), "GroupName" varchar(100) NOT NULL,
                    "Balance" numeric(20,8) NOT NULL, "LifetimeCreditsAdded" numeric(20,8) NOT NULL,
                    "LifetimeSpent" numeric(20,8) NOT NULL, "CreatedAt" timestamptz NOT NULL,
                    "UpdatedAt" timestamptz NOT NULL, "MediaRetentionPolicyId" integer,
                    "RateLimitRpm" integer, "RateLimitRpd" integer, "RateLimitTpm" integer,
                    "MaxParallelRequests" integer, "RowVersion" bytea
                );
                CREATE TABLE "VirtualKeys" (
                    "Id" integer PRIMARY KEY, "VirtualKeyGroupId" integer NOT NULL,
                    "KeyHash" varchar(128) NOT NULL
                );
                CREATE TABLE "VirtualKeyGroupTransactions" (
                    "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    "VirtualKeyGroupId" integer NOT NULL REFERENCES "VirtualKeyGroups" ("Id"),
                    "TransactionType" integer NOT NULL, "Amount" numeric(18,6) NOT NULL,
                    "BalanceAfter" numeric(18,6) NOT NULL, "ReferenceType" integer NOT NULL,
                    "ReferenceId" varchar(100), "Description" varchar(500), "InitiatedBy" varchar(50) NOT NULL,
                    "InitiatedByUserId" varchar(100), "IdempotencyKey" varchar(100),
                    "BillingWindowStartUtc" timestamptz, "CreatedAt" timestamptz NOT NULL,
                    "IsDeleted" boolean NOT NULL, "DeletedAt" timestamptz
                );
                CREATE UNIQUE INDEX "IX_LegacyReplay_IdempotencyKey" ON "VirtualKeyGroupTransactions" ("IdempotencyKey")
                    WHERE "IdempotencyKey" IS NOT NULL;
                """);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO "VirtualKeyGroups" (
                    "Id", "GroupName", "Balance", "LifetimeCreditsAdded", "LifetimeSpent", "CreatedAt", "UpdatedAt")
                VALUES (@groupId, 'legacy replay', @balance, 100, @spent, @createdAt, @createdAt),
                    (@otherGroupId, 'different group', 100, 100, 0, @createdAt, @createdAt);
                INSERT INTO "VirtualKeys" ("Id", "VirtualKeyGroupId", "KeyHash") VALUES (1, @groupId, 'legacy-replay-key');
                """;
            command.Parameters.AddWithValue("groupId", groupId);
            command.Parameters.AddWithValue("otherGroupId", groupId + 1);
            command.Parameters.AddWithValue("balance", seedLegacy ? 100m - OriginalAmount : 100m);
            command.Parameters.AddWithValue("spent", seedLegacy ? OriginalAmount : 0m);
            command.Parameters.AddWithValue("createdAt", CreatedAt);
            await command.ExecuteNonQueryAsync();
            if (seedLegacy)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO "VirtualKeyGroupTransactions" (
                        "VirtualKeyGroupId", "TransactionType", "Amount", "BalanceAfter", "ReferenceType", "ReferenceId",
                        "InitiatedBy", "IdempotencyKey", "BillingWindowStartUtc", "CreatedAt", "IsDeleted")
                    VALUES (@groupId, 2, @amount, @balance, 3, @claimId, 'System', @idempotencyKey, @window, @createdAt, false)
                    """;
                command.Parameters.AddWithValue("groupId", groupId);
                command.Parameters.AddWithValue("amount", OriginalAmount);
                command.Parameters.AddWithValue("balance", 100m - OriginalAmount);
                command.Parameters.AddWithValue("claimId", claimId);
                command.Parameters.AddWithValue("idempotencyKey", $"batch-spend:{claimId}");
                command.Parameters.AddWithValue("window", BillingWindow);
                command.Parameters.AddWithValue("createdAt", CreatedAt);
                await command.ExecuteNonQueryAsync();
            }
            // Exact original evidence is additive to the existing six-decimal ledger.
            await ExecuteAsync(connectionString, """
                ALTER TABLE "VirtualKeyGroupTransactions"
                    ADD COLUMN "OriginalAmount" numeric(20,8), ADD COLUMN "OriginalAmountVerifiedBy" varchar(100),
                    ADD COLUMN "OriginalAmountEvidence" varchar(500), ADD COLUMN "OriginalAmountVerifiedAtUtc" timestamptz
                """);
            if (widenLedger)
            {
                // Widening preserves the rounded historical value and does not recover
                // the exact original amount. New-schema writes use eight decimal places.
                await ExecuteAsync(connectionString, """
                    ALTER TABLE "VirtualKeyGroupTransactions"
                        ALTER COLUMN "Amount" TYPE numeric(20,8), ALTER COLUMN "BalanceAfter" TYPE numeric(20,8)
                    """);
            }
            return schema;
        }
        catch
        {
            await schema.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<LedgerSnapshot> ReadLedgerAsync(TestSchema schema)
    {
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "Amount", "BalanceAfter", "OriginalAmount", "OriginalAmountVerifiedBy",
                   "OriginalAmountEvidence", "OriginalAmountVerifiedAtUtc", "IsDeleted", "DeletedAt"
            FROM "VirtualKeyGroupTransactions"
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var snapshot = new LedgerSnapshot(reader.GetDecimal(0), reader.GetDecimal(1),
            reader.IsDBNull(2) ? null : reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetDateTime(7));
        Assert.False(await reader.ReadAsync());
        return snapshot;
    }

    private static async Task<BalanceSnapshot> ReadBalanceAsync(TestSchema schema, int? groupId = null)
    {
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "Balance", "LifetimeSpent", "LifetimeCreditsAdded", "UpdatedAt" FROM "VirtualKeyGroups" WHERE "Id" = @groupId
            """, connection);
        command.Parameters.AddWithValue("groupId", groupId ?? schema.GroupId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new BalanceSnapshot(reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDateTime(3));
    }

    private static async Task<long> CountLedgerAsync(TestSchema schema)
    {
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"VirtualKeyGroupTransactions\"", connection);
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing ledger count"));
    }

    private sealed record LedgerSnapshot(decimal Amount, decimal BalanceAfter, decimal? OriginalAmount,
        string? VerifiedBy, string? Evidence, DateTime? VerifiedAt, bool IsDeleted, DateTime? DeletedAt);
    private sealed record BalanceSnapshot(decimal Balance, decimal LifetimeSpent, decimal LifetimeCreditsAdded, DateTime UpdatedAt);

    private sealed record TestSchema(string Name, string ConnectionString, string RootConnectionString, int GroupId, string ClaimId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Clear only the implicit pool belonging to this GUID-owned schema;
            // the explicit data source and test operations have already finished.
            using (var ownedPool = new NpgsqlConnection(ConnectionString))
                NpgsqlConnection.ClearPool(ownedPool);
            await ExecuteAsync(RootConnectionString, $"DROP SCHEMA IF EXISTS \"{Name}\" CASCADE");
        }
    }

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(connectionString).Options);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
