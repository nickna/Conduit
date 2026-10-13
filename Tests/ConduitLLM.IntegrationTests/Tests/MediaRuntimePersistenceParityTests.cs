using System.Data.Common;

using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

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
            await using var efSource = NpgsqlDataSource.Create(efConnection);
            var factory = new TestDbContextFactory(efSource);
            await using (var context = factory.CreateDbContext())
            {
                var repository = new MediaRecordRepository(
                    factory,
                    NullLogger<MediaRecordRepository>.Instance);
                await ExerciseContractAsync(new EfMediaRuntimeStore(repository, context));
                await VerifyRepositoryOwnedReadsAsync(repository, factory);
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

    [Theory]
    [InlineData("storage-key", 1)]
    [InlineData("quota", 1)]
    [InlineData("quota", 2)]
    [InlineData("group-quota", 1)]
    [InlineData("group-quota", 2)]
    public async Task EfStoreQueries_ObserveCancellationAtCommandExecutionAndKeepBorrowedContextUsable(
        string operation, int cancelOnReader)
    {
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelReaderInterceptor(cancellation, cancelOnReader);
        await WithEfStoreAsync(async (store, context, repository, source) =>
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                switch (operation)
                {
                    case "storage-key":
                        await store.GetByStorageKeyAsync("missing", cancellationToken: cancellation.Token);
                        break;
                    case "quota":
                        await store.GetQuotaSnapshotAsync(1, cancellation.Token);
                        break;
                    case "group-quota":
                        await store.GetGroupQuotaUsagesAsync(1, cancellation.Token);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation));
                }
            });

            Assert.True(interceptor.WasTriggered);
            if (cancelOnReader > 1)
                Assert.True(interceptor.CompletedReaderCount > 0);
            Assert.Equal(cancellation.Token, interceptor.ObservedToken);
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Equal(2, await context.VirtualKeys.CountAsync());
        }, interceptor);
    }

    [Fact]
    public async Task EfStoreReads_DoNotTrackEntitiesOrDisposeTheBorrowedContext()
    {
        ConduitDbContext? borrowedContext = null;
        await WithEfStoreAsync(async (store, context, repository, source) =>
        {
            borrowedContext = context;
            var active = NewMedia("borrowed-active", 1, "image", 100, "provider-a");
            var deleted = NewMedia("borrowed-deleted", 1, "image", 200, "provider-a");
            deleted.DeletedAt = deleted.CreatedAt;
            await store.CreateAsync(active);
            await store.CreateAsync(deleted);

            Assert.NotNull(await store.GetByStorageKeyAsync(active.StorageKey));
            Assert.Null(await store.GetByStorageKeyAsync(deleted.StorageKey));
            Assert.NotNull(await store.GetByStorageKeyAsync(deleted.StorageKey, includeDeleted: true));
            Assert.NotNull(await store.GetQuotaSnapshotAsync(1));
            Assert.NotNull(await store.GetQuotaSnapshotAsync(2));
            Assert.Equal(2, (await store.GetGroupQuotaUsagesAsync()).Count);
            Assert.Single(await store.GetGroupQuotaUsagesAsync(1));
            Assert.Empty(context.ChangeTracker.Entries());

            Assert.Null(await store.GetByStorageKeyAsync(" "));
            Assert.Null(await store.GetByStorageKeyAsync(null!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => store.CreateAsync(null!));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateAsync(NewMedia(active.StorageKey, 2, "image", 900, "wrong-owner")));
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Equal(1, await context.MediaRecords.CountAsync());
        });

        Assert.NotNull(borrowedContext);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await borrowedContext!.VirtualKeys.CountAsync());
    }

    [Fact]
    public async Task ScopedRepositoryRegistration_ResolvesStoreUsingTheSameBorrowedContext()
    {
        await WithEfStoreAsync(async (unusedStore, unusedContext, repository, source) =>
        {
            var factory = new TestDbContextFactory(source);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IDbContextFactory<ConduitDbContext>>(factory);
            services.AddScoped<ConduitDbContext>(_ => factory.CreateDbContext());
            services.AddRepositories();
            await using var provider = services.BuildServiceProvider();
            ConduitDbContext? scopedContext = null;
            await using (var scope = provider.CreateAsyncScope())
            {
                scopedContext = scope.ServiceProvider.GetRequiredService<ConduitDbContext>();
                Assert.Same(scopedContext,
                    scope.ServiceProvider.GetRequiredService<IConfigurationDbContext>());
                var store = Assert.IsType<EfMediaRuntimeStore>(
                    scope.ServiceProvider.GetRequiredService<IMediaRuntimeStore>());

                await using (var transaction = await scopedContext.Database.BeginTransactionAsync())
                {
                    await scopedContext.Database.ExecuteSqlRawAsync("""
                        UPDATE "MediaRetentionPolicies" SET "MaxStorageSizeBytes" = 777 WHERE "Id" = 1;
                        INSERT INTO "MediaRecords" (
                            "Id", "StorageKey", "VirtualKeyId", "MediaType", "SizeBytes", "CreatedAt", "AccessCount")
                        VALUES ('14400000-0000-0000-0000-000000000001', 'scoped-uncommitted', 1, 'image', 77, now(), 0);
                        """);

                    Assert.NotNull(await store.GetByStorageKeyAsync("scoped-uncommitted"));
                    Assert.Equal(new MediaRuntimeQuotaSnapshot(1, 77, 1, 777, 2, 0),
                        await store.GetQuotaSnapshotAsync(1));
                    Assert.Equal(new MediaRuntimeGroupQuotaUsage(
                            1, "default-group", 1, "default-policy", 77, 1, 777, 2, 0, true, 7),
                        Assert.Single(await store.GetGroupQuotaUsagesAsync(1)));
                    Assert.Same(transaction, scopedContext.Database.CurrentTransaction);
                    Assert.Empty(scopedContext.ChangeTracker.Entries());

                    await transaction.RollbackAsync();
                }
                Assert.Null(await store.GetByStorageKeyAsync("scoped-uncommitted"));
                Assert.Equal(new MediaRuntimeQuotaSnapshot(1, 0, 0, 1000, 2, 0),
                    await store.GetQuotaSnapshotAsync(1));
                Assert.Equal(2, await scopedContext.VirtualKeys.CountAsync());
            }
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await scopedContext!.VirtualKeys.CountAsync());
        });
    }

    [Fact]
    public async Task EfQuotaReads_ResolvePolicyFallbacksAndProjectCompleteGroupUsage()
    {
        await WithEfStoreAsync(async (store, context, repository, source) =>
        {
            await ExecuteFixtureSqlAsync(source, """
                INSERT INTO "MediaRetentionPolicies" (
                    "Id", "Name", "PositiveBalanceRetentionDays", "ZeroBalanceRetentionDays",
                    "NegativeBalanceRetentionDays", "SoftDeleteGracePeriodDays",
                    "RespectRecentAccess", "RecentAccessWindowDays", "IsDefault",
                    "MaxStorageSizeBytes", "MaxFileCount", "QuotaExceededBehavior",
                    "CreatedAt", "UpdatedAt", "IsActive")
                VALUES
                    (0, 'inactive-default', 60, 14, 3, 7, false, 1, true,
                        1, 1, 1, now(), now(), false),
                    (3, 'inactive-assignment', 60, 14, 3, 7, false, 2, false,
                        10, 1, 1, now(), now(), false),
                    (4, 'later-default', 60, 14, 3, 7, false, 2, true,
                        9000, 9, 1, now(), now(), true);
                INSERT INTO "VirtualKeyGroups" ("Id", "GroupName", "MediaRetentionPolicyId")
                VALUES (3, 'inactive-group', 3), (4, 'missing-policy-group', 999), (5, 'empty-group', NULL);
                INSERT INTO "VirtualKeys" ("Id", "VirtualKeyGroupId")
                VALUES (3, 3), (4, 4), (5, 5), (6, 1);
                """);
            await store.CreateAsync(NewMedia("boundary-active", 1, "image", 100, "provider-a"));
            var deleted = NewMedia("boundary-deleted", 6, "image", 200, "provider-a");
            deleted.DeletedAt = deleted.CreatedAt;
            await store.CreateAsync(deleted);
            var unknownSize = NewMedia("boundary-unknown-size", 6, "image", 0, "provider-a");
            unknownSize.SizeBytes = null;
            await store.CreateAsync(unknownSize);
            await store.CreateAsync(NewMedia("boundary-other-group", 2, "video", 900, "provider-b"));

            var sharedGroup = new MediaRuntimeQuotaSnapshot(1, 300, 3, 1000, 2, 0);
            Assert.Equal(sharedGroup, await store.GetQuotaSnapshotAsync(1));
            Assert.Equal(sharedGroup, await store.GetQuotaSnapshotAsync(6));
            Assert.Equal(new MediaRuntimeQuotaSnapshot(2, 900, 1, null, null, 1),
                await store.GetQuotaSnapshotAsync(2));
            foreach (var groupId in new[] { 3, 4, 5 })
                Assert.Equal(new MediaRuntimeQuotaSnapshot(groupId, 0, 0, 1000, 2, 0),
                    await store.GetQuotaSnapshotAsync(groupId));
            Assert.Null(await store.GetQuotaSnapshotAsync(999));

            var expected = new[]
            {
                new MediaRuntimeGroupQuotaUsage(1, "default-group", 1, "default-policy", 300, 3, 1000, 2, 0, true, 7),
                new MediaRuntimeGroupQuotaUsage(2, "assigned-group", 2, "unlimited-policy", 900, 1, null, null, 1, false, 3),
                new MediaRuntimeGroupQuotaUsage(3, "inactive-group", 1, "default-policy", 0, 0, 1000, 2, 0, true, 7),
                new MediaRuntimeGroupQuotaUsage(4, "missing-policy-group", 1, "default-policy", 0, 0, 1000, 2, 0, true, 7),
                new MediaRuntimeGroupQuotaUsage(5, "empty-group", 1, "default-policy", 0, 0, 1000, 2, 0, true, 7)
            };
            Assert.Equal(expected, (await store.GetGroupQuotaUsagesAsync()).OrderBy(row => row.VirtualKeyGroupId));
            Assert.Equal(expected[0], Assert.Single(await store.GetGroupQuotaUsagesAsync(1)));
            Assert.Empty(await store.GetGroupQuotaUsagesAsync(999));
            Assert.Empty(context.ChangeTracker.Entries());
        });
    }

    [Fact]
    public async Task EfQuotaReads_UseContractDefaultsWhenNoActivePolicyExists()
    {
        await WithEfStoreAsync(async (store, context, repository, source) =>
        {
            await ExecuteFixtureSqlAsync(source,
                "UPDATE \"MediaRetentionPolicies\" SET \"IsActive\" = false;");
            Assert.Equal(new MediaRuntimeQuotaSnapshot(1, 0, 0, null, null, 0),
                await store.GetQuotaSnapshotAsync(1));
            Assert.Equal(new MediaRuntimeQuotaSnapshot(2, 0, 0, null, null, 0),
                await store.GetQuotaSnapshotAsync(2));
            Assert.Equal(new[]
            {
                new MediaRuntimeGroupQuotaUsage(1, "default-group", null, null, 0, 0, null, null, 0, true, 7),
                new MediaRuntimeGroupQuotaUsage(2, "assigned-group", null, null, 0, 0, null, null, 0, true, 7)
            }, (await store.GetGroupQuotaUsagesAsync()).OrderBy(row => row.VirtualKeyGroupId));
            Assert.Empty(context.ChangeTracker.Entries());
        });
    }

    [Fact]
    public async Task EfMutations_PropagateGeneratedIdentityAndPreserveDeletedReplayOwnership()
    {
        await WithEfStoreAsync(async (store, context, repository, source) =>
        {
            var created = NewMedia("generated-identity", 1, "image", 120, "original-provider");
            created.Id = Guid.Empty;
            created.CreatedAt = default;
            var id = await store.CreateAsync(created);
            Assert.NotEqual(Guid.Empty, id);
            Assert.Equal(id, created.Id);
            Assert.NotEqual(default, created.CreatedAt);
            var persisted = await ReadStoredMediaAsync(source, created.StorageKey);
            Assert.Equal(id, persisted.Id);
            Assert.InRange(Math.Abs((persisted.CreatedAt - created.CreatedAt).Ticks), 0L, 10L);
            Assert.Equal((long?)120, persisted.SizeBytes);
            Assert.Equal("original-provider", persisted.Provider);

            Assert.True(await store.UpdateAccessStatsAsync(id));
            persisted = await ReadStoredMediaAsync(source, created.StorageKey);
            Assert.Equal(1, persisted.AccessCount);
            Assert.NotNull(persisted.LastAccessedAt);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.UpdateAccessStatsAsync(id, cancellation.Token));
            }
            Assert.Equal(1, (await ReadStoredMediaAsync(source, created.StorageKey)).AccessCount);
            Assert.False(await store.UpdateAccessStatsAsync(Guid.NewGuid()));

            var deleted = NewMedia("deleted-replay", 1, "image", 150, "durable-provider");
            deleted.DeletedAt = deleted.CreatedAt;
            await store.CreateAsync(deleted);
            var beforeReplay = await ReadStoredMediaAsync(source, deleted.StorageKey);
            var replay = NewMedia(deleted.StorageKey, 1, "video", 999, "replay-provider");
            Assert.Equal(deleted.Id, await store.CreateAsync(replay));
            Assert.Equal(deleted.Id, replay.Id);
            Assert.Equal(beforeReplay.CreatedAt, replay.CreatedAt);
            Assert.Equal(beforeReplay, await ReadStoredMediaAsync(source, deleted.StorageKey));
            Assert.Null(await store.GetByStorageKeyAsync(deleted.StorageKey));
            Assert.NotNull(await store.GetByStorageKeyAsync(deleted.StorageKey, includeDeleted: true));

            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateAsync(NewMedia(deleted.StorageKey, 2, "image", 500, "wrong-owner")));
            Assert.Contains("different virtual key", conflict.Message);
            Assert.Equal(beforeReplay, await ReadStoredMediaAsync(source, deleted.StorageKey));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EfCreate_HandlesACompetingDurableCompletionWithoutOverwritingItsOwner(
        bool conflictingOwner)
    {
        await WithEfStoreAsync(async (unusedStore, context, repository, source) =>
        {
            var requested = NewMedia("competing-completion", 1, "image", 900, "requested-provider");
            var winner = new MediaRecord
            {
                Id = Guid.NewGuid(),
                StorageKey = requested.StorageKey,
                VirtualKeyId = conflictingOwner ? 2 : 1,
                MediaType = "image",
                SizeBytes = 17,
                Provider = "winning-provider",
                CreatedAt = requested.CreatedAt.AddMinutes(-1)
            };
            var boundary = new Mock<IMediaRecordRepository>(MockBehavior.Strict);
            boundary.Setup(item => item.CreateAsync(It.IsAny<MediaRecord>(), It.IsAny<CancellationToken>()))
                .Returns((MediaRecord entity, CancellationToken token) =>
                    InsertWinnerBeforeRequestedCreateAsync(repository, winner, entity, token));
            var store = new EfMediaRuntimeStore(boundary.Object, context);

            if (conflictingOwner)
            {
                var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(requested));
                Assert.Contains("different virtual key", conflict.Message);
            }
            else
            {
                Assert.Equal(winner.Id, await store.CreateAsync(requested));
                Assert.Equal(winner.Id, requested.Id);
                Assert.Equal(winner.CreatedAt, requested.CreatedAt);
            }
            var persisted = await ReadStoredMediaAsync(source, winner.StorageKey);
            Assert.Equal(winner.Id, persisted.Id);
            Assert.Equal((long?)17, persisted.SizeBytes);
            Assert.Equal("winning-provider", persisted.Provider);
            Assert.Equal(winner.VirtualKeyId, persisted.VirtualKeyId);
        });
    }

    [Fact]
    public async Task EfCreate_PropagatesRepositoryFailureWhenNoDurableWinnerExists()
    {
        await WithEfStoreAsync(async (unusedStore, context, repository, source) =>
        {
            var expected = new DbUpdateException("repository write failed without a competing completion");
            var boundary = new Mock<IMediaRecordRepository>(MockBehavior.Strict);
            boundary.Setup(item => item.CreateAsync(It.IsAny<MediaRecord>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(expected);
            var store = new EfMediaRuntimeStore(boundary.Object, context);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() =>
                store.CreateAsync(NewMedia("failed-create", 1, "image", 100, "provider-a")));
            Assert.Same(expected, failure);
            Assert.Null(await store.GetByStorageKeyAsync("failed-create", includeDeleted: true));
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Equal(2, await context.VirtualKeys.CountAsync());
        });
    }

    private async Task WithEfStoreAsync(
        Func<EfMediaRuntimeStore, ConduitDbContext, MediaRecordRepository, NpgsqlDataSource, Task> scenario,
        DbCommandInterceptor? interceptor = null)
    {
        var schema = $"media_runtime_coverage_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var factory = new TestDbContextFactory(source);
            var options = new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(source);
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            await using var context = new ConduitDbContext(options.Options);
            var repository = new MediaRecordRepository(factory, NullLogger<MediaRecordRepository>.Instance);
            await scenario(new EfMediaRuntimeStore(repository, context), context, repository, source);
        }
        finally
        {
            await DropSchemaAsync(schema);
        }
    }

    private static async Task ExecuteFixtureSqlAsync(NpgsqlDataSource source, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> InsertWinnerBeforeRequestedCreateAsync(
        MediaRecordRepository repository, MediaRecord winner, MediaRecord requested, CancellationToken token)
    {
        // The adapter's initial lookup already missed. A separate repository context
        // commits the competing completion before the original write reaches the unique key.
        await repository.CreateAsync(winner, token);
        return await repository.CreateAsync(requested, token);
    }

    private static async Task<StoredMediaWitness> ReadStoredMediaAsync(
        NpgsqlDataSource source, string storageKey)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "VirtualKeyId", "CreatedAt", "SizeBytes", "Provider", "DeletedAt", "AccessCount", "LastAccessedAt"
            FROM "MediaRecords" WHERE "StorageKey" = @storageKey;
            """;
        command.Parameters.AddWithValue("storageKey", storageKey);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new StoredMediaWitness(
            reader.GetGuid(0), reader.GetInt32(1), reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5), reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7));
    }

    private sealed record StoredMediaWitness(
        Guid Id, int VirtualKeyId, DateTime CreatedAt, long? SizeBytes, string? Provider,
        DateTime? DeletedAt, int AccessCount, DateTime? LastAccessedAt);

    private sealed class CancelReaderInterceptor(
        CancellationTokenSource cancellation, int cancelOnReader) : DbCommandInterceptor
    {
        private int _readerCount;
        private int _completedReaderCount;
        private int _triggered;
        public bool WasTriggered => Volatile.Read(ref _triggered) != 0;
        public int CompletedReaderCount => Volatile.Read(ref _completedReaderCount);
        public CancellationToken ObservedToken { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readerCount) == cancelOnReader)
            {
                ObservedToken = cancellationToken;
                Volatile.Write(ref _triggered, 1);
                cancellation.Cancel();
            }
            // Let the provider observe the token rather than throwing from the interceptor.
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _completedReaderCount);
            return ValueTask.FromResult(result);
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
        deleted.CreatedAt = active.CreatedAt.AddMinutes(5);
        deleted.DeletedAt = DateTime.UtcNow;
        await store.CreateAsync(deleted);

        var otherGroup = NewMedia("other-group", 2, "video", 300, "provider-b");
        otherGroup.CreatedAt = active.CreatedAt.AddMinutes(15);
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

        var newer = NewMedia("newer-active", 1, "image", 50, "provider-a");
        newer.CreatedAt = active.CreatedAt.AddMinutes(10);
        await store.CreateAsync(newer);
        var otherDeleted = NewMedia("other-deleted", 2, "video", 400, "provider-b");
        otherDeleted.CreatedAt = active.CreatedAt.AddMinutes(20);
        otherDeleted.DeletedAt = DateTime.UtcNow;
        await store.CreateAsync(otherDeleted);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> cancelledRead = async () =>
            await store.GetByVirtualKeyIdAsync(1, cancellation.Token);
        await cancelledRead.Should().ThrowAsync<OperationCanceledException>();

        (await store.GetByVirtualKeyIdAsync(1)).Select(record => record.Id)
            .Should().Equal(newer.Id, active.Id);
        (await store.GetByVirtualKeyIdAsync(2)).Select(record => record.Id)
            .Should().Equal(otherGroup.Id);
        (await store.GetByVirtualKeyIdAsync(999)).Should().BeEmpty();
    }

    private static async Task VerifyRepositoryOwnedReadsAsync(
        MediaRecordRepository repository,
        TestDbContextFactory factory)
    {
        factory.TrackedMedia.Clear();
        foreach (var includeDeleted in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Func<Task> cancelledRead = async () => await repository.GetByVirtualKeyIdAsync(
                1, includeDeleted, cancellation.Token);
            await cancelledRead.Should().ThrowAsync<OperationCanceledException>();

            var owned = await repository.GetByVirtualKeyIdAsync(1, includeDeleted);
            owned.Select(record => record.StorageKey).Should().Equal(includeDeleted
                ? new[] { "newer-active", "deleted", "active" }
                : new[] { "newer-active", "active" });
            var otherOwned = await repository.GetByVirtualKeyIdAsync(2, includeDeleted);
            otherOwned.Select(record => record.StorageKey).Should().Equal(includeDeleted
                ? new[] { "other-deleted", "other-group" }
                : new[] { "other-group" });
            (await repository.GetByVirtualKeyIdAsync(999, includeDeleted)).Should().BeEmpty();
        }
        factory.TrackedMedia.Should().BeEmpty(
            "owned-media reads must not attach returned entities");
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

    private sealed class TestDbContextFactory(NpgsqlDataSource source) :
        IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(source)
                .Options;

        public List<MediaRecord> TrackedMedia { get; } = [];

        public ConduitDbContext CreateDbContext()
        {
            var context = new ConduitDbContext(_options);
            context.ChangeTracker.Tracked += (_, args) =>
            {
                if (args.Entry.Entity is MediaRecord media)
                    TrackedMedia.Add(media);
            };
            return context;
        }

        public Task<ConduitDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
