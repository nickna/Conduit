using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Tests.TestInfrastructure;

using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Core.Services;

[Trait("Category", "Unit")]
[Trait("Component", "MediaLifecycle")]
public sealed class MediaQuotaServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly ConduitDbContext _context;
    private readonly MediaQuotaService _service;

    public MediaQuotaServiceTests()
    {
        _context = _database.CreateContext();
        _service = new MediaQuotaService(
            new EfMediaRuntimeStore(Mock.Of<IMediaRecordRepository>(), _context),
            NullLogger<MediaQuotaService>.Instance);
    }

    [Fact]
    public async Task EnsureCanStoreAsync_RejectsBeforeProspectiveWriteExceedsQuota()
    {
        SeedGroup(
            maxBytes: 2_000,
            maxFiles: 2,
            behavior: MediaQuotaExceededBehavior.Reject);
        SeedMedia("existing", 1_500);
        await _context.SaveChangesAsync();

        var act = () => _service.EnsureCanStoreAsync(1, 600);

        await act.Should().ThrowAsync<RateLimitExceededException>()
            .WithMessage("*Media quota exceeded*storage 2,100/2,000 bytes*");
    }

    [Fact]
    public async Task EnsureCanStoreAsync_AllowAndEvictPermitsProspectiveWrite()
    {
        SeedGroup(
            maxBytes: 1_000,
            maxFiles: 1,
            behavior: MediaQuotaExceededBehavior.AllowAndEvict);
        SeedMedia("existing", 1_000);
        await _context.SaveChangesAsync();

        var act = () => _service.EnsureCanStoreAsync(1, 500);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetGroupUsagesAsync_AggregatesAcrossKeysAndIncludesTombstones()
    {
        SeedGroup(
            maxBytes: 2_000,
            maxFiles: 2,
            behavior: MediaQuotaExceededBehavior.Reject);
        _context.VirtualKeys.Add(new VirtualKey
        {
            Id = 2,
            KeyName = "second",
            KeyHash = "second-hash",
            VirtualKeyGroupId = 1
        });
        SeedMedia("active", 800, virtualKeyId: 1);
        SeedMedia("recoverable", 1_300, virtualKeyId: 2, deletedAt: DateTime.UtcNow);
        await _context.SaveChangesAsync();

        var usage = (await _service.GetGroupUsagesAsync(1)).Single();

        usage.TotalFiles.Should().Be(2);
        usage.TotalSizeBytes.Should().Be(2_100);
        usage.IsOverQuota.Should().BeTrue();
        usage.StorageUsagePercent.Should().BeApproximately(105, 0.001);
    }

    [Fact]
    public async Task InMemoryStorage_ConsultsQuotaBeforePersistingObject()
    {
        var guard = new Mock<IMediaQuotaGuard>();
        guard.Setup(service => service.EnsureCanStoreAsync(
                "1",
                4,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RateLimitExceededException("quota"));
        var storage = new InMemoryMediaStorageService(
            NullLogger<InMemoryMediaStorageService>.Instance,
            quotaGuard: guard.Object);
        await using var content = new MemoryStream([1, 2, 3, 4]);

        var act = () => storage.StoreAsync(content, new MediaMetadata
        {
            ContentType = "image/png",
            MediaType = MediaType.Image,
            CreatedBy = "1"
        });

        await act.Should().ThrowAsync<RateLimitExceededException>();
        (await storage.ListObjectsAsync()).Objects.Should().BeEmpty();
    }

    private void SeedGroup(
        long? maxBytes,
        int? maxFiles,
        MediaQuotaExceededBehavior behavior)
    {
        var policy = new MediaRetentionPolicy
        {
            Id = 1,
            Name = "quota",
            IsDefault = true,
            IsActive = true,
            MaxStorageSizeBytes = maxBytes,
            MaxFileCount = maxFiles,
            QuotaExceededBehavior = behavior
        };
        var group = new VirtualKeyGroup
        {
            Id = 1,
            GroupName = "group",
            MediaRetentionPolicyId = 1
        };
        _context.MediaRetentionPolicies.Add(policy);
        _context.VirtualKeyGroups.Add(group);
        _context.VirtualKeys.Add(new VirtualKey
        {
            Id = 1,
            KeyName = "first",
            KeyHash = "first-hash",
            VirtualKeyGroupId = 1
        });
    }

    private void SeedMedia(
        string storageKey,
        long sizeBytes,
        int virtualKeyId = 1,
        DateTime? deletedAt = null)
    {
        _context.MediaRecords.Add(new MediaRecord
        {
            Id = Guid.NewGuid(),
            StorageKey = storageKey,
            VirtualKeyId = virtualKeyId,
            MediaType = "image",
            SizeBytes = sizeBytes,
            CreatedAt = DateTime.UtcNow,
            DeletedAt = deletedAt
        });
    }

    public void Dispose()
    {
        _context.Dispose();
        _database.Dispose();
    }
}
