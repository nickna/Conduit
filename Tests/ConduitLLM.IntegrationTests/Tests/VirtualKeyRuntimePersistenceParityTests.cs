using AwesomeAssertions;

using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;

using Npgsql;
using NpgsqlTypes;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// Runs the Gateway virtual-key runtime contract against EF and typed Npgsql.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class VirtualKeyRuntimePersistenceParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;

    public VirtualKeyRuntimePersistenceParityTests(PostgresLockTestContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EfAndTypedNpgsqlImplementTheSameRuntimeContract()
    {
        var efSchema = $"vk_runtime_ef_{Guid.NewGuid():N}";
        var npgsqlSchema = $"vk_runtime_np_{Guid.NewGuid():N}";

        try
        {
            var efIds = await CreateAndSeedSchemaAsync(efSchema);
            var npgsqlIds = await CreateAndSeedSchemaAsync(npgsqlSchema);

            await ExerciseContractAsync(
                new EfVirtualKeyRuntimeStore(new TestDbContextFactory(WithSearchPath(efSchema))),
                efIds);

            await using var dataSource = NpgsqlDataSource.Create(WithSearchPath(npgsqlSchema));
            await ExerciseContractAsync(new NpgsqlVirtualKeyRuntimeStore(dataSource), npgsqlIds);
        }
        finally
        {
            await DropSchemaAsync(efSchema);
            await DropSchemaAsync(npgsqlSchema);
        }
    }

    private static async Task ExerciseContractAsync(
        IVirtualKeyRuntimeStore store,
        SeedIds ids)
    {
        (await store.GetByHashAsync("missing")).Should().BeNull();
        (await store.GetByIdAsync(-1)).Should().BeNull();

        var key = await store.GetByHashAsync("sha256:test-key");
        key.Should().NotBeNull();
        key!.Id.Should().Be(ids.KeyId);
        key.KeyName.Should().Be("runtime key");
        key.Description.Should().BeNull();
        key.IsEnabled.Should().BeTrue();
        key.AllowedModels.Should().Be("gpt-5,claude-sonnet");
        key.Metadata.Should().Be("{\"tenant\":\"acme\"}");
        using (var modelLimits = JsonDocument.Parse(key.ModelRateLimits!))
        {
            modelLimits.RootElement.GetProperty("gpt-5").GetProperty("rpm").GetInt32()
                .Should().Be(5);
        }
        key.RateLimitRpm.Should().Be(120);
        key.RateLimitRpd.Should().BeNull();
        key.RateLimitTpm.Should().Be(50_000);
        key.MaxParallelRequests.Should().Be(4);
        key.RateLimitPriority.Should().Be(2);
        key.RowVersion.Should().Equal([1, 2, 3]);
        key.Group.Id.Should().Be(ids.GroupId);
        key.Group.GroupName.Should().Be("runtime group");
        key.Group.ExternalGroupId.Should().BeNull();
        key.Group.Balance.Should().Be(100m);
        key.Group.LifetimeCreditsAdded.Should().Be(100m);
        key.Group.RateLimitRpm.Should().Be(1_000);
        key.Group.RateLimitRpd.Should().Be(10_000);
        key.Group.RateLimitTpm.Should().BeNull();
        key.Group.MaxParallelRequests.Should().Be(20);

        (await store.GetByIdAsync(ids.KeyId))!.KeyHash.Should().Be("sha256:test-key");
        (await store.GetKeyHashesByGroupIdAsync(ids.GroupId)).Should()
            .Equal("sha256:test-key");
        (await store.GetKeyHashesByGroupIdAsync(-1)).Should().BeEmpty();
        var touchedAt = DateTime.UtcNow.AddMinutes(1);
        (await store.TouchAsync(ids.KeyId, touchedAt)).Should().BeTrue();
        (await store.TouchAsync(-1, touchedAt)).Should().BeFalse();
        (await store.GetByIdAsync(ids.KeyId))!.UpdatedAt.Should().BeCloseTo(
            touchedAt,
            TimeSpan.FromMilliseconds(1));

        var debit = await store.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
            ids.GroupId,
            -2.5m,
            "request charge",
            "Gateway",
            VirtualKeyBalanceReferenceType.VirtualKey,
            ids.KeyId.ToString(),
            BillingWindowStartUtc: new DateTime(2026, 8, 27, 20, 0, 0, DateTimeKind.Utc)));
        debit.Should().Be(new VirtualKeyBalanceAdjustmentResult(97.5m, 2.5m, true));

        var idempotent = new VirtualKeyBalanceAdjustment(
            ids.GroupId,
            -1.25m,
            "idempotent charge",
            "Gateway",
            VirtualKeyBalanceReferenceType.VirtualKey,
            ids.KeyId.ToString(),
            "spend:request-1",
            new DateTime(2026, 8, 27, 21, 0, 0, DateTimeKind.Utc));
        (await store.AdjustBalanceAsync(idempotent)).Should()
            .Be(new VirtualKeyBalanceAdjustmentResult(96.25m, 3.75m, true));
        (await store.AdjustBalanceAsync(idempotent)).Should()
            .Be(new VirtualKeyBalanceAdjustmentResult(96.25m, 3.75m, false));

        var conflict = idempotent with { Amount = -9m };
        await Assert.ThrowsAsync<VirtualKeyBalanceConflictException>(
            () => store.AdjustBalanceAsync(conflict));

        var concurrent = Enumerable.Range(0, 8)
            .Select(index => store.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
                ids.GroupId,
                -0.5m,
                $"concurrent {index}",
                "Gateway",
                VirtualKeyBalanceReferenceType.VirtualKey,
                ids.KeyId.ToString())))
            .ToArray();
        var concurrentResults = await Task.WhenAll(concurrent);
        concurrentResults.Should().OnlyContain(result => result.Applied);
        concurrentResults.Select(result => result.NewBalance).Distinct().Should().HaveCount(8);

        var sameDelivery = new VirtualKeyBalanceAdjustment(
            ids.GroupId,
            -0.75m,
            "redelivered",
            "Gateway",
            VirtualKeyBalanceReferenceType.VirtualKey,
            ids.KeyId.ToString(),
            "spend:concurrent-delivery");
        var deliveryResults = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => store.AdjustBalanceAsync(sameDelivery)));
        deliveryResults.Count(result => result.Applied).Should().Be(1);

        var credit = await store.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
            ids.GroupId,
            10m,
            null,
            null,
            VirtualKeyBalanceReferenceType.Manual));
        credit.NewBalance.Should().Be(101.5m);
        credit.LifetimeSpent.Should().Be(8.5m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
                999_999,
                -1m,
                null,
                null,
                VirtualKeyBalanceReferenceType.System)));
    }

    private async Task<SeedIds> CreateAndSeedSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."VirtualKeyGroups" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "ExternalGroupId" character varying(100) NULL,
                "GroupName" character varying(100) NOT NULL,
                "Balance" numeric(19,8) NOT NULL,
                "LifetimeCreditsAdded" numeric(19,8) NOT NULL,
                "LifetimeSpent" numeric(19,8) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "MediaRetentionPolicyId" integer NULL,
                "RateLimitRpm" integer NULL,
                "RateLimitRpd" integer NULL,
                "RateLimitTpm" integer NULL,
                "MaxParallelRequests" integer NULL,
                "RowVersion" bytea NULL
            );
            CREATE TABLE "{schema}"."VirtualKeys" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "KeyName" character varying(100) NOT NULL,
                "KeyHash" character varying(128) NOT NULL,
                "Description" character varying(500) NULL,
                "IsEnabled" boolean NOT NULL,
                "VirtualKeyGroupId" integer NOT NULL REFERENCES "{schema}"."VirtualKeyGroups" ("Id") ON DELETE RESTRICT,
                "ExpiresAt" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "Metadata" text NULL,
                "AllowedModels" text NULL,
                "RateLimitRpm" integer NULL,
                "RateLimitRpd" integer NULL,
                "RateLimitTpm" integer NULL,
                "MaxParallelRequests" integer NULL,
                "RateLimitPriority" integer NULL,
                "ModelRateLimits" jsonb NULL,
                "RowVersion" bytea NULL
            );
            CREATE UNIQUE INDEX "IX_VK_KeyHash" ON "{schema}"."VirtualKeys" ("KeyHash");
            CREATE TABLE "{schema}"."VirtualKeyGroupTransactions" (
                "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "VirtualKeyGroupId" integer NOT NULL REFERENCES "{schema}"."VirtualKeyGroups" ("Id") ON DELETE CASCADE,
                "TransactionType" integer NOT NULL,
                "Amount" numeric(18,6) NOT NULL,
                "BalanceAfter" numeric(18,6) NOT NULL,
                "ReferenceType" integer NOT NULL,
                "ReferenceId" character varying(100) NULL,
                "Description" character varying(500) NULL,
                "InitiatedBy" character varying(50) NOT NULL,
                "InitiatedByUserId" character varying(100) NULL,
                "IdempotencyKey" character varying(100) NULL,
                "BillingWindowStartUtc" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "IsDeleted" boolean NOT NULL,
                "DeletedAt" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX "IX_VKGT_IdempotencyKey"
                ON "{schema}"."VirtualKeyGroupTransactions" ("IdempotencyKey")
                WHERE "IdempotencyKey" IS NOT NULL;

            INSERT INTO "{schema}"."VirtualKeyGroups" (
                "ExternalGroupId", "GroupName", "Balance", "LifetimeCreditsAdded",
                "LifetimeSpent", "CreatedAt", "UpdatedAt", "MediaRetentionPolicyId",
                "RateLimitRpm", "RateLimitRpd", "RateLimitTpm", "MaxParallelRequests",
                "RowVersion")
            VALUES (
                NULL, 'runtime group', 100, 100, 0, now(), now(), NULL,
                1000, 10000, NULL, 20, decode('0908', 'hex'))
            RETURNING "Id";
            """;
        var groupId = Convert.ToInt32(await command.ExecuteScalarAsync());

        command.Parameters.Clear();
        command.CommandText = $"""
            INSERT INTO "{schema}"."VirtualKeys" (
                "KeyName", "KeyHash", "Description", "IsEnabled", "VirtualKeyGroupId",
                "ExpiresAt", "CreatedAt", "UpdatedAt", "Metadata", "AllowedModels",
                "RateLimitRpm", "RateLimitRpd", "RateLimitTpm", "MaxParallelRequests",
                "RateLimitPriority", "ModelRateLimits", "RowVersion")
            VALUES (
                'runtime key', 'sha256:test-key', NULL, true, @groupId,
                now() + interval '1 day', now(), now(), @metadata,
                'gpt-5,claude-sonnet', 120, NULL, 50000, 4, 2,
                @modelRateLimits, decode('010203', 'hex'))
            RETURNING "Id";
            """;
        command.Parameters.AddWithValue("groupId", groupId);
        command.Parameters.AddWithValue(
            "metadata",
            NpgsqlDbType.Text,
            """{"tenant":"acme"}""");
        command.Parameters.AddWithValue(
            "modelRateLimits",
            NpgsqlDbType.Jsonb,
            """{"gpt-5":{"rpm":5}}""");
        var keyId = Convert.ToInt32(await command.ExecuteScalarAsync());
        return new SeedIds(groupId, keyId);
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

    private sealed record SeedIds(int GroupId, int KeyId);

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
