using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Tests.TestInfrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Configuration.Repositories;

[Trait("Category", "Unit")]
[Trait("Component", "MediaLifecycle")]
public sealed class MediaRecordSoftDeleteTests : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = new();

    [Fact]
    public async Task Repository_DefaultQueriesExcludeTombstonesAndRestoreMakesThemVisible()
    {
        var activeId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        await _database.SeedAsync(async context =>
        {
            context.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = 1,
                GroupName = "soft-delete-tests",
                Balance = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            context.VirtualKeys.Add(new VirtualKey
            {
                Id = 1,
                KeyName = "soft-delete-key",
                KeyHash = "soft-delete-hash",
                VirtualKeyGroupId = 1,
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow
            });
            context.MediaRecords.AddRange(
                new MediaRecord
                {
                    Id = activeId,
                    VirtualKeyId = 1,
                    StorageKey = "active-media",
                    MediaType = "image",
                    SizeBytes = 100,
                    CreatedAt = DateTime.UtcNow
                },
                new MediaRecord
                {
                    Id = deletedId,
                    VirtualKeyId = 1,
                    StorageKey = "deleted-media",
                    MediaType = "image",
                    SizeBytes = 200,
                    CreatedAt = DateTime.UtcNow,
                    DeletedAt = DateTime.UtcNow.AddDays(-1)
                });
            await context.SaveChangesAsync();
        });
        var repository = CreateRepository();

        (await repository.GetByStorageKeyAsync("deleted-media")).Should().BeNull();
        (await repository.GetByStorageKeyIncludingDeletedAsync("deleted-media"))
            .Should().NotBeNull();
        (await repository.GetByVirtualKeyIdAsync(1))
            .Should().ContainSingle(item => item.Id == activeId);
        (await repository.GetByVirtualKeyIdAsync(1, includeDeleted: true))
            .Should().HaveCount(2);
        (await repository.GetTotalStorageSizeByVirtualKeyAsync(1)).Should().Be(100);

        (await repository.RestoreAsync(deletedId)).Should().BeTrue();
        (await repository.GetByStorageKeyAsync("deleted-media")).Should().NotBeNull();

        (await repository.TombstoneAsync(activeId, DateTime.UtcNow)).Should().BeTrue();
        (await repository.GetByStorageKeyAsync("active-media")).Should().BeNull();
        (await repository.HardDeleteAsync(activeId)).Should().BeTrue();
        (await repository.GetByStorageKeyIncludingDeletedAsync("active-media")).Should().BeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GetByVirtualKeyIdAsync_SoftDeleteBranchesPreserveReadContract(
        bool includeDeleted,
        bool omitSoftDeleteFilter)
    {
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = NewMedia("older", 1, createdAt);
        var deleted = NewMedia("deleted", 1, createdAt.AddDays(1), deletedAt: createdAt);
        var newer = NewMedia("newer", 1, createdAt.AddDays(2));
        var otherOwner = NewMedia("other-owner", 2, createdAt.AddDays(3));
        var otherDeleted = NewMedia(
            "other-deleted", 2, createdAt.AddDays(4), deletedAt: createdAt);
        await _database.SeedAsync(async context =>
        {
            context.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = 1,
                GroupName = "owned-media-tests",
                Balance = 1,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            });
            context.VirtualKeys.AddRange(
                new VirtualKey
                {
                    Id = 1,
                    KeyName = "owner-one",
                    KeyHash = "owner-one-hash",
                    VirtualKeyGroupId = 1,
                    IsEnabled = true,
                    CreatedAt = createdAt
                },
                new VirtualKey
                {
                    Id = 2,
                    KeyName = "owner-two",
                    KeyHash = "owner-two-hash",
                    VirtualKeyGroupId = 1,
                    IsEnabled = true,
                    CreatedAt = createdAt
                });
            context.MediaRecords.AddRange(older, deleted, newer, otherOwner, otherDeleted);
            await context.SaveChangesAsync();
        });

        var trackedMedia = new List<MediaRecord>();
        var factory = new TestDbContextFactory(() =>
        {
            var context = omitSoftDeleteFilter
                ? new UnfilteredMediaTestContext(_database.Options)
                : _database.CreateContext();
            context.ChangeTracker.Tracked += (_, args) =>
            {
                if (args.Entry.Entity is MediaRecord media)
                    trackedMedia.Add(media);
            };
            return context;
        });
        var repository = new MediaRecordRepository(
            factory,
            NullLogger<MediaRecordRepository>.Instance);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> cancelledRead = async () => await repository.GetByVirtualKeyIdAsync(
            1, includeDeleted, cancellation.Token);
        await cancelledRead.Should().ThrowAsync<OperationCanceledException>();

        var owned = await repository.GetByVirtualKeyIdAsync(1, includeDeleted);
        owned.Select(media => media.Id).Should().Equal(includeDeleted
            ? new[] { newer.Id, deleted.Id, older.Id }
            : new[] { newer.Id, older.Id });
        var otherOwned = await repository.GetByVirtualKeyIdAsync(2, includeDeleted);
        otherOwned.Select(media => media.Id).Should().Equal(includeDeleted
            ? new[] { otherDeleted.Id, otherOwner.Id }
            : new[] { otherOwner.Id });
        (await repository.GetByVirtualKeyIdAsync(999, includeDeleted)).Should().BeEmpty();
        trackedMedia.Should().BeEmpty("owned-media reads must not attach returned entities");
    }

    private static MediaRecord NewMedia(
        string storageKey,
        int virtualKeyId,
        DateTime createdAt,
        DateTime? deletedAt = null) => new()
        {
            Id = Guid.NewGuid(),
            StorageKey = storageKey,
            VirtualKeyId = virtualKeyId,
            MediaType = "image",
            SizeBytes = 100,
            CreatedAt = createdAt,
            DeletedAt = deletedAt
        };

    // A separate context type keeps this model isolated from the normal SQLite model.
    // Omitting its media filter exercises the native-model condition without changing
    // the process-wide compiled-model generation setting during parallel unit tests.
    private sealed class UnfilteredMediaTestContext(DbContextOptions<ConduitDbContext> options)
        : TestConduitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<MediaRecord>().HasQueryFilter(null);
        }
    }

    private MediaRecordRepository CreateRepository() => new(
        _database.CreateDbContextFactory(),
        NullLogger<MediaRecordRepository>.Instance);

    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
