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
/// Runs the Gateway media runtime contract against EF and typed Npgsql.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class MediaRuntimePersistenceParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;

    public MediaRuntimePersistenceParityTests(PostgresLockTestContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EfAndTypedNpgsqlImplementTheSameRuntimeContract()
    {
        var efSchema = $"media_runtime_ef_{Guid.NewGuid():N}";
        var npgsqlSchema = $"media_runtime_np_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(efSchema);
            await CreateSchemaAsync(npgsqlSchema);

            var efConnection = WithSearchPath(efSchema);
            var factory = new TestDbContextFactory(efConnection);
            await using (var context = factory.CreateDbContext())
            {
                var repository = new MediaRecordRepository(
                    factory,
                    NullLogger<MediaRecordRepository>.Instance);
                await ExerciseContractAsync(new EfMediaRuntimeStore(repository, context));
            }

            await using var dataSource = NpgsqlDataSource.Create(WithSearchPath(npgsqlSchema));
            await ExerciseContractAsync(new NpgsqlMediaRuntimeStore(dataSource));
        }
        finally
        {
            await DropSchemaAsync(efSchema);
            await DropSchemaAsync(npgsqlSchema);
        }
    }

    private static async Task ExerciseContractAsync(IMediaRuntimeStore store)
    {
        (await store.GetByStorageKeyAsync("missing")).Should().BeNull();

        var active = NewMedia("active", 1, "image", 100, "provider-a");
        (await store.CreateAsync(active)).Should().Be(active.Id);
        active.CreatedAt.Should().NotBe(default);

        var replay = NewMedia("active", 1, "image", 999, "replayed-provider");
        (await store.CreateAsync(replay)).Should().Be(active.Id);
        replay.Id.Should().Be(active.Id);
        replay.CreatedAt.Should().Be(active.CreatedAt);

        var conflictingOwner = NewMedia("active", 2, "image", 999, "provider-b");
        Func<Task> createForConflictingOwner = async () =>
            await store.CreateAsync(conflictingOwner);
        await createForConflictingOwner.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*different virtual key*");

        var deleted = NewMedia("deleted", 1, "image", 200, "provider-a");
        deleted.DeletedAt = DateTime.UtcNow;
        await store.CreateAsync(deleted);

        var otherGroup = NewMedia("other-group", 2, "video", 300, "provider-b");
        await store.CreateAsync(otherGroup);

        var read = await store.GetByStorageKeyAsync("active");
        read.Should().BeEquivalentTo(active);
        (await store.GetByStorageKeyAsync("deleted")).Should().BeNull();
        (await store.GetByStorageKeyAsync("deleted", includeDeleted: true))!
            .DeletedAt.Should().NotBeNull();

        var owned = await store.GetByVirtualKeyIdAsync(1);
        owned.Should().ContainSingle(record => record.StorageKey == "active");
        (await store.UpdateAccessStatsAsync(active.Id)).Should().BeTrue();
        (await store.UpdateAccessStatsAsync(Guid.NewGuid())).Should().BeFalse();
        (await store.GetByStorageKeyAsync("active"))!.AccessCount.Should().Be(1);
        (await store.GetByStorageKeyAsync("active"))!.LastAccessedAt.Should().NotBeNull();

        var aggregate = await store.GetAggregateStorageStatsAsync();
        aggregate.TotalFiles.Should().Be(2);
        aggregate.TotalSizeBytes.Should().Be(400);
        aggregate.ByProvider.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["provider-a"] = 100,
            ["provider-b"] = 300
        });
        aggregate.ByMediaType.Should().BeEquivalentTo(
            [
                new MediaRuntimeTypeAggregate("image", 1, 100),
                new MediaRuntimeTypeAggregate("video", 1, 300)
            ],
            options => options.WithStrictOrdering());
        aggregate.TopVirtualKeys.Should().BeEquivalentTo(
            [
                new MediaRuntimeVirtualKeyAggregate(2, 300),
                new MediaRuntimeVirtualKeyAggregate(1, 100)
            ],
            options => options.WithStrictOrdering());

        var groupAggregate = await store.GetAggregateStorageStatsAsync(1);
        groupAggregate.TotalFiles.Should().Be(1);
        groupAggregate.TotalSizeBytes.Should().Be(100);

        var quota = await store.GetQuotaSnapshotAsync(1);
        quota.Should().NotBeNull();
        quota!.VirtualKeyGroupId.Should().Be(1);
        quota.TotalFiles.Should().Be(2);
        quota.TotalSizeBytes.Should().Be(300);
        quota.MaxStorageSizeBytes.Should().Be(1_000);
        quota.MaxFileCount.Should().Be(2);
        quota.QuotaExceededBehavior.Should().Be(0);

        var unlimitedAssignedPolicy = await store.GetQuotaSnapshotAsync(2);
        unlimitedAssignedPolicy.Should().NotBeNull();
        unlimitedAssignedPolicy!.MaxStorageSizeBytes.Should().BeNull();
        unlimitedAssignedPolicy.MaxFileCount.Should().BeNull();
        unlimitedAssignedPolicy.TotalFiles.Should().Be(1);
        unlimitedAssignedPolicy.TotalSizeBytes.Should().Be(300);
        (await store.GetQuotaSnapshotAsync(999)).Should().BeNull();

        var usage = (await store.GetGroupQuotaUsagesAsync(1)).Should().ContainSingle().Which;
        usage.VirtualKeyGroupName.Should().Be("default-group");
        usage.MediaRetentionPolicyId.Should().Be(1);
        usage.TotalFiles.Should().Be(2);
        usage.TotalSizeBytes.Should().Be(300);
        usage.MaxStorageSizeBytes.Should().Be(1_000);

        var allUsage = await store.GetGroupQuotaUsagesAsync();
        allUsage.Should().HaveCount(2);
        allUsage.Single(row => row.VirtualKeyGroupId == 2)
            .MaxStorageSizeBytes.Should().BeNull();
    }

    private static MediaRuntimeRecord NewMedia(
        string storageKey,
        int virtualKeyId,
        string mediaType,
        long sizeBytes,
        string provider)
    {
        var createdAt = DateTime.UtcNow;
        createdAt = createdAt.AddTicks(-(createdAt.Ticks % 10));
        return new()
        {
            Id = Guid.NewGuid(),
            StorageKey = storageKey,
            VirtualKeyId = virtualKeyId,
            MediaType = mediaType,
            ContentType = mediaType == "image" ? "image/png" : "video/mp4",
            SizeBytes = sizeBytes,
            ContentHash = new string('a', 64),
            Provider = provider,
            Model = "native-media-model",
            Prompt = "parity",
            StorageUrl = $"https://storage/{storageKey}",
            PublicUrl = $"https://cdn/{storageKey}",
            ExpiresAt = createdAt.AddDays(30),
            CreatedAt = createdAt,
            AccessCount = 0
        };
    }

    private async Task CreateSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."VirtualKeyGroups" (
                "Id" integer PRIMARY KEY,
                "GroupName" character varying(100) NOT NULL,
                "MediaRetentionPolicyId" integer NULL
            );
            CREATE TABLE "{schema}"."VirtualKeys" (
                "Id" integer PRIMARY KEY,
                "VirtualKeyGroupId" integer NOT NULL
            );
            CREATE TABLE "{schema}"."MediaRetentionPolicies" (
                "Id" integer PRIMARY KEY,
                "Name" character varying(100) NOT NULL,
                "Description" character varying(500) NULL,
                "PositiveBalanceRetentionDays" integer NOT NULL,
                "ZeroBalanceRetentionDays" integer NOT NULL,
                "NegativeBalanceRetentionDays" integer NOT NULL,
                "SoftDeleteGracePeriodDays" integer NOT NULL,
                "RespectRecentAccess" boolean NOT NULL,
                "RecentAccessWindowDays" integer NOT NULL,
                "IsDefault" boolean NOT NULL,
                "MaxStorageSizeBytes" bigint NULL,
                "MaxFileCount" integer NULL,
                "QuotaExceededBehavior" integer NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "IsActive" boolean NOT NULL
            );
            CREATE TABLE "{schema}"."MediaRecords" (
                "Id" uuid PRIMARY KEY,
                "StorageKey" character varying(500) NOT NULL UNIQUE,
                "VirtualKeyId" integer NOT NULL,
                "MediaType" character varying(50) NOT NULL,
                "ContentType" character varying(100) NULL,
                "SizeBytes" bigint NULL,
                "ContentHash" character varying(64) NULL,
                "Provider" character varying(50) NULL,
                "Model" character varying(100) NULL,
                "Prompt" text NULL,
                "StorageUrl" text NULL,
                "PublicUrl" text NULL,
                "ExpiresAt" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "LastAccessedAt" timestamp with time zone NULL,
                "AccessCount" integer NOT NULL,
                "DeletedAt" timestamp with time zone NULL
            );
            INSERT INTO "{schema}"."MediaRetentionPolicies" (
                "Id", "Name", "PositiveBalanceRetentionDays", "ZeroBalanceRetentionDays",
                "NegativeBalanceRetentionDays", "SoftDeleteGracePeriodDays",
                "RespectRecentAccess", "RecentAccessWindowDays", "IsDefault",
                "MaxStorageSizeBytes", "MaxFileCount", "QuotaExceededBehavior",
                "CreatedAt", "UpdatedAt", "IsActive")
            VALUES
                (1, 'default-policy', 60, 14, 3, 7, true, 7, true,
                    1000, 2, 0, now(), now(), true),
                (2, 'unlimited-policy', 60, 14, 3, 7, false, 3, false,
                    NULL, NULL, 1, now(), now(), true);
            INSERT INTO "{schema}"."VirtualKeyGroups" ("Id", "GroupName", "MediaRetentionPolicyId")
            VALUES (1, 'default-group', NULL), (2, 'assigned-group', 2);
            INSERT INTO "{schema}"."VirtualKeys" ("Id", "VirtualKeyGroupId")
            VALUES (1, 1), (2, 2);
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
