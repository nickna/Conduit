using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Tests.TestInfrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Admin.Services
{
    /// <summary>
    /// Unit tests for MediaCleanupService.
    /// Tests the unified media cleanup background service including scheduling,
    /// distributed locking, retention evaluation, and deletion logic.
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Component", "MediaLifecycle")]
    public class MediaCleanupServiceTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly Mock<IDistributedLockProvider> _mockLockService;
        private readonly Mock<IMediaStorageService> _mockStorageService;
        private readonly Mock<IMediaDeletionBudgetService> _mockBudgetService;
        private readonly Mock<IMediaRecordRepository> _mockMediaRepository;
        private readonly Mock<IMediaCleanupStatusService> _mockStatusService;
        private readonly Mock<IMediaCleanupApprovalService> _mockApprovalService;
        private readonly Mock<IMediaStorageConfigurationGuard> _mockStorageGuard;
        private readonly Mock<ILogger<MediaCleanupService>> _mockLogger;
        private readonly Mock<IDistributedLockOwnership> _mockLock;
        private readonly MutableOptions<MediaLifecycleOptions> _engineOptions;
        private readonly ConduitDbContext _context;
        private readonly SqliteTestDatabase _database;

        public MediaCleanupServiceTests()
        {
            _database = new SqliteTestDatabase();
            _context = _database.CreateContext();

            _mockLockService = new Mock<IDistributedLockProvider>();
            _mockStorageService = new Mock<IMediaStorageService>();
            _mockBudgetService = new Mock<IMediaDeletionBudgetService>();
            _mockMediaRepository = new Mock<IMediaRecordRepository>();
            _mockStatusService = new Mock<IMediaCleanupStatusService>();
            _mockApprovalService = new Mock<IMediaCleanupApprovalService>();
            _mockStorageGuard = new Mock<IMediaStorageConfigurationGuard>();
            _mockLogger = new Mock<ILogger<MediaCleanupService>>();
            _engineOptions = new MutableOptions<MediaLifecycleOptions>(
                new MediaLifecycleOptions());

            // Set up lock mock
            _mockLock = new Mock<IDistributedLockOwnership>();

            // Default budget service setup - within budget
            _mockBudgetService
                .Setup(x => x.ReserveAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int requested, int _, CancellationToken _) =>
                    new MediaDeletionBudgetReservation(requested, requested, requested));

            // Default storage service setup - successful deletes
            _mockStorageService
                .Setup(x => x.DeleteManyAsync(
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                    SuccessfulDelete(keys));
            _mockStorageService
                .Setup(x => x.ListObjectsAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaStorageObjectPage());

            // Default repository setup - successful deletes
            _mockMediaRepository
                .Setup(x => x.HardDeleteAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _mockMediaRepository
                .Setup(x => x.TombstoneAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _mockStatusService
                .Setup(x => x.GetSimpleRetentionOverrideAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((int?)null);
            _mockStorageGuard
                .Setup(x => x.ValidateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // Set up service provider for scope factory
            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationDbContext>(_context);
            services.AddSingleton(_mockStorageService.Object);
            services.AddSingleton(_mockBudgetService.Object);
            services.AddSingleton(_mockMediaRepository.Object);
            services.AddSingleton(_mockStatusService.Object);
            services.AddSingleton(_mockApprovalService.Object);
            services.AddSingleton(_mockStorageGuard.Object);
            services.AddSingleton<IOptions<MediaLifecycleOptions>>(_engineOptions);
            services.AddSingleton(Mock.Of<ILogger<MediaDeletionEngine>>());
            services.AddSingleton(Mock.Of<ILogger<MediaReconciliationService>>());
            services.AddSingleton(Mock.Of<ILogger<MediaQuotaService>>());
            services.AddScoped<IMediaQuotaService, MediaQuotaService>();
            services.AddScoped<IMediaDeletionEngine, MediaDeletionEngine>();
            services.AddScoped<IMediaReconciliationService, MediaReconciliationService>();
            _serviceProvider = services.BuildServiceProvider();
        }

        private MediaCleanupService CreateService(MediaLifecycleOptions options)
        {
            _engineOptions.Value = options;
            return new MediaCleanupService(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                _mockLockService.Object,
                Options.Create(options),
                _mockLogger.Object);
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_WhenStorageGuardBlocks_DoesNotAcquireLock()
        {
            var storageGuard = new Mock<IMediaStorageConfigurationGuard>();
            storageGuard
                .Setup(guard => guard.ValidateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var service = new MediaCleanupService(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                _mockLockService.Object,
                Options.Create(CreateExecutionOptions()),
                _mockLogger.Object,
                storageGuard.Object);

            await service.RunScheduledCleanupAsync(CancellationToken.None);

            _mockLockService.Verify(lockService => lockService.TryAcquireAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        private void SeedTestGroup(int groupId, decimal balance = 100m)
        {
            _context.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = groupId,
                GroupName = $"Test Group {groupId}",
                Balance = balance,
                LifetimeCreditsAdded = 100.00m,
                LifetimeSpent = 0m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            _context.SaveChanges();
        }

        private void SeedDefaultRetentionPolicy()
        {
            _context.MediaRetentionPolicies.Add(new MediaRetentionPolicy
            {
                Id = 1,
                Name = "Default Policy",
                Description = "Default retention policy",
                PositiveBalanceRetentionDays = 60,
                ZeroBalanceRetentionDays = 30,
                NegativeBalanceRetentionDays = 7,
                RespectRecentAccess = false,
                RecentAccessWindowDays = 7,
                IsDefault = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            _context.SaveChanges();
        }

        private void SeedVirtualKey(int keyId, int groupId)
        {
            _context.VirtualKeys.Add(new VirtualKey
            {
                Id = keyId,
                KeyName = $"Test Key {keyId}",
                KeyHash = $"hash{keyId}",
                VirtualKeyGroupId = groupId,
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow
            });
            _context.SaveChanges();
        }

        private void SeedMediaRecords(int virtualKeyId, int count, int daysOld)
        {
            for (int i = 0; i < count; i++)
            {
                _context.MediaRecords.Add(new MediaRecord
                {
                    Id = Guid.NewGuid(),
                    VirtualKeyId = virtualKeyId,
                    StorageKey = $"storage-key-{virtualKeyId}-{i}",
                    MediaType = "image/png",
                    SizeBytes = 1024,
                    Provider = "test-provider",
                    Prompt = "test prompt",
                    CreatedAt = DateTime.UtcNow.AddDays(-daysOld)
                });
            }
            _context.SaveChanges();
        }

        #region Service Disabled Tests

        [Fact]
        public async Task ExecuteAsync_WhenDisabled_DoesNotAcquireLock()
        {
            // Arrange
            var options = new MediaLifecycleOptions { Enabled = false };
            var service = CreateService(options);

            using var cts = new CancellationTokenSource();

            // Act
            var executeTask = service.StartAsync(cts.Token);
            await Task.Delay(100);
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);

            // Assert
            _mockLockService.Verify(
                x => x.TryAcquireAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ExecuteAsync_WhenDisabled_LogsDisabledMessage()
        {
            // Arrange
            var options = new MediaLifecycleOptions { Enabled = false };
            var service = CreateService(options);

            using var cts = new CancellationTokenSource();

            // Act
            await service.StartAsync(cts.Token);
            await Task.Delay(100);
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) =>
                        o.ToString()!.Contains("disabled")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        #endregion

        #region Lock Acquisition Tests

        [Fact]
        public async Task ExecuteAsync_WhenEnabled_LogsStartupMessage()
        {
            // Arrange
            var options = new MediaLifecycleOptions
            {
                Enabled = true,
                ScheduleIntervalMinutes = 60
            };

            _mockLockService
                .Setup(x => x.TryAcquireAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockLock.Object);

            var service = CreateService(options);
            using var cts = new CancellationTokenSource();

            // Act
            await service.StartAsync(cts.Token);
            await Task.Delay(200);
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) =>
                        o.ToString()!.Contains("starting")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task ExecuteAsync_WhenLockNotAcquired_DoesNotProcessCleanup()
        {
            // Arrange
            var options = new MediaLifecycleOptions { Enabled = true };

            _mockLockService
                .Setup(x => x.TryAcquireAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IDistributedLockOwnership?)null);

            SeedTestGroup(1);
            SeedDefaultRetentionPolicy();
            SeedVirtualKey(1, 1);
            SeedMediaRecords(1, 5, 100); // Old media that should be deleted

            var service = CreateService(options);

            // Act
            await service.RunScheduledCleanupAsync(CancellationToken.None);

            // Assert - no storage deletions when lock not acquired
            _mockStorageService.Verify(
                x => x.DeleteManyAsync(
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_TwoInstances_OnlyLeaderDeletes()
        {
            var options = CreateExecutionOptions();
            options.EnableReconciliation = false;
            options.EnableRetentionCleanup = false;
            var lockService = new ConduitLLM.Tests.Helpers.TestDistributedLockProvider();
            var deleteStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowDelete = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _engineOptions.Value = options;

            SeedTestGroup(1);
            SeedVirtualKey(1, 1);
            var expired = new MediaRecord
            {
                Id = Guid.NewGuid(),
                VirtualKeyId = 1,
                StorageKey = "leader-only-media",
                MediaType = "image",
                SizeBytes = 1024,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
            };
            _context.MediaRecords.Add(expired);
            await _context.SaveChangesAsync();
            _mockStorageService
                .Setup(storage => storage.DeleteManyAsync(
                    It.Is<IEnumerable<string>>(keys => keys.Contains(expired.StorageKey)),
                    It.IsAny<CancellationToken>()))
                .Returns(async (IEnumerable<string> keys, CancellationToken _) =>
                {
                    deleteStarted.TrySetResult();
                    await allowDelete.Task;
                    return SuccessfulDelete(keys);
                });

            var first = new MediaCleanupService(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                lockService,
                Options.Create(options),
                _mockLogger.Object);
            var second = new MediaCleanupService(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                lockService,
                Options.Create(options),
                _mockLogger.Object);

            var firstRun = first.RunScheduledCleanupAsync(CancellationToken.None);
            await deleteStarted.Task;
            await second.RunScheduledCleanupAsync(CancellationToken.None);
            allowDelete.TrySetResult();
            await firstRun;

            _mockStorageService.Verify(
                storage => storage.DeleteManyAsync(
                    It.Is<IEnumerable<string>>(keys => keys.Contains(expired.StorageKey)),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_CleansExpiredMediaWithinDistributedLock()
        {
            var options = CreateExecutionOptions();
            options.EnableReconciliation = false;
            options.EnableRetentionCleanup = false;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedVirtualKey(1, 1);
            var expired = new MediaRecord
            {
                Id = Guid.NewGuid(),
                VirtualKeyId = 1,
                StorageKey = "expired-media",
                MediaType = "image",
                SizeBytes = 2048,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
            };
            _context.MediaRecords.Add(expired);
            await _context.SaveChangesAsync();

            var service = CreateService(options);
            await service.RunScheduledCleanupAsync(CancellationToken.None);

            _mockLockService.Verify(x => x.TryAcquireAsync(
                "media:cleanup:leader", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
            VerifyBulkDelete("expired-media", Times.Once());
            _mockMediaRepository.Verify(x => x.HardDeleteAsync(
                expired.Id, It.IsAny<CancellationToken>()), Times.Once);
            _mockBudgetService.Verify(x => x.ReserveAsync(
                1, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
            VerifyOperationStatus(MediaCleanupTypes.Expiration, "Completed");
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_ResolvesSimpleRetentionOverrideOncePerRun()
        {
            var options = CreateExecutionOptions();
            options.EnableExpirationCleanup = false;
            options.EnableReconciliation = false;
            options.EnableQuotaCleanup = false;
            options.EnableRetentionCleanup = true;
            ArrangeLockAcquired();
            SeedTestGroup(1);
            SeedTestGroup(2);
            SeedDefaultRetentionPolicy();
            _mockStatusService
                .Setup(service => service.GetSimpleRetentionOverrideAsync(
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(14);

            await CreateService(options).RunScheduledCleanupAsync(CancellationToken.None);

            _mockStatusService.Verify(service =>
                service.GetSimpleRetentionOverrideAsync(
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_PaginatesExpiredMediaAndReportsRunCap()
        {
            var options = CreateExecutionOptions();
            options.EnableReconciliation = false;
            options.EnableQuotaCleanup = false;
            options.EnableRetentionCleanup = false;
            options.CleanupPageSize = 2;
            options.MaxRecordsPerRun = 3;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedVirtualKey(1, 1);
            var now = DateTime.UtcNow;
            for (var index = 0; index < 5; index++)
            {
                _context.MediaRecords.Add(new MediaRecord
                {
                    Id = Guid.NewGuid(),
                    VirtualKeyId = 1,
                    StorageKey = $"paged-expired-{index}",
                    MediaType = "image",
                    SizeBytes = 100,
                    CreatedAt = now.AddMinutes(-10 + index),
                    ExpiresAt = now.AddMinutes(-1)
                });
            }
            await _context.SaveChangesAsync();
            var deletedKeys = new List<string>();
            _mockStorageService
                .Setup(storage => storage.DeleteManyAsync(
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback((IEnumerable<string> keys, CancellationToken _) =>
                    deletedKeys.AddRange(keys))
                .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                    SuccessfulDelete(keys));

            await CreateService(options).RunScheduledCleanupAsync(CancellationToken.None);

            deletedKeys.Should().BeEquivalentTo(
                "paged-expired-0",
                "paged-expired-1",
                "paged-expired-2");
            _mockStorageService.Verify(storage => storage.DeleteManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
            VerifyOperationStatus(MediaCleanupTypes.Expiration, "record cap reached");
            _mockStatusService.Verify(status => status.RecordRunCompletionAsync(
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<double>(),
                It.Is<string>(value =>
                    value.Contains("record cap reached", StringComparison.OrdinalIgnoreCase) &&
                    value.Contains("3/3", StringComparison.Ordinal)),
                It.IsAny<string>(),
                "scheduled",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_CountsFullEligibilityBeforePagedApproval()
        {
            var options = CreateExecutionOptions();
            options.EnableReconciliation = false;
            options.EnableQuotaCleanup = false;
            options.EnableRetentionCleanup = false;
            options.CleanupPageSize = 2;
            options.MaxRecordsPerRun = 10;
            options.RequireManualApprovalForLargeBatches = true;
            options.LargeBatchThreshold = 3;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedVirtualKey(1, 1);
            for (var index = 0; index < 5; index++)
            {
                _context.MediaRecords.Add(new MediaRecord
                {
                    Id = Guid.NewGuid(),
                    VirtualKeyId = 1,
                    StorageKey = $"approval-expired-{index}",
                    MediaType = "image",
                    SizeBytes = 100,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-index - 1),
                    ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
                });
            }
            await _context.SaveChangesAsync();

            await CreateService(options).RunScheduledCleanupAsync(CancellationToken.None);

            _mockApprovalService.Verify(approval =>
                approval.CreateOrRefreshPendingAsync(
                    MediaCleanupTypes.Expiration,
                    null,
                    5,
                    500,
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            _mockStorageService.Verify(storage => storage.DeleteManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_PurgesOnlyTombstonesPastGracePeriod()
        {
            var options = CreateExecutionOptions();
            options.EnableSoftDelete = true;
            options.SoftDeleteGracePeriodDays = 3;
            options.EnableExpirationCleanup = false;
            options.EnableReconciliation = false;
            options.EnableRetentionCleanup = false;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedVirtualKey(1, 1);
            var expiredTombstone = new MediaRecord
            {
                Id = Guid.NewGuid(),
                VirtualKeyId = 1,
                StorageKey = "expired-tombstone",
                MediaType = "image",
                SizeBytes = 2048,
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                DeletedAt = DateTime.UtcNow.AddDays(-4)
            };
            var recoverableTombstone = new MediaRecord
            {
                Id = Guid.NewGuid(),
                VirtualKeyId = 1,
                StorageKey = "recoverable-tombstone",
                MediaType = "image",
                SizeBytes = 1024,
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                DeletedAt = DateTime.UtcNow.AddDays(-2)
            };
            _context.MediaRecords.AddRange(expiredTombstone, recoverableTombstone);
            await _context.SaveChangesAsync();

            var service = CreateService(options);
            await service.RunScheduledCleanupAsync(CancellationToken.None);

            _mockStorageService.Verify(
                storage => storage.DeleteManyAsync(
                    It.Is<IEnumerable<string>>(keys => keys.Contains(expiredTombstone.StorageKey)),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            _mockStorageService.Verify(
                storage => storage.DeleteManyAsync(
                    It.Is<IEnumerable<string>>(keys => keys.Contains(recoverableTombstone.StorageKey)),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            _mockMediaRepository.Verify(repository => repository.HardDeleteAsync(
                expiredTombstone.Id,
                It.IsAny<CancellationToken>()), Times.Once);
            _mockBudgetService.Verify(service => service.ReserveAsync(
                1,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()), Times.Once);
            VerifyOperationStatus(MediaCleanupTypes.Purge, "Completed");
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_PaginatesPurgeCandidates()
        {
            var options = CreateExecutionOptions();
            options.EnableSoftDelete = true;
            options.SoftDeleteGracePeriodDays = 3;
            options.EnableExpirationCleanup = false;
            options.EnableReconciliation = false;
            options.EnableQuotaCleanup = false;
            options.EnableRetentionCleanup = false;
            options.CleanupPageSize = 1;
            options.MaxRecordsPerRun = 10;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedDefaultRetentionPolicy();
            SeedVirtualKey(1, 1);
            for (var index = 0; index < 2; index++)
            {
                _context.MediaRecords.Add(new MediaRecord
                {
                    Id = Guid.NewGuid(),
                    VirtualKeyId = 1,
                    StorageKey = $"paged-purge-{index}",
                    MediaType = "image",
                    SizeBytes = 100,
                    CreatedAt = DateTime.UtcNow.AddDays(-10).AddMinutes(index),
                    DeletedAt = DateTime.UtcNow.AddDays(-100)
                });
            }
            await _context.SaveChangesAsync();

            await CreateService(options).RunScheduledCleanupAsync(CancellationToken.None);

            VerifyBulkDelete("paged-purge-0", Times.Once());
            VerifyBulkDelete("paged-purge-1", Times.Once());
            _mockStorageService.Verify(storage => storage.DeleteManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_ReconcilesOldUntrackedStorageObject()
        {
            var options = CreateExecutionOptions();
            options.EnableExpirationCleanup = false;
            options.EnableRetentionCleanup = false;
            ArrangeLockAcquired();

            _mockStorageService
                .Setup(x => x.ListObjectsAsync(
                    null,
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaStorageObjectPage
                {
                    Objects =
                    [
                        new MediaStorageObject
                        {
                            StorageKey = "untracked-media",
                            SizeBytes = 1024,
                            LastModifiedUtc = DateTime.UtcNow.AddDays(-7)
                        }
                    ]
                });

            var service = CreateService(options);
            await service.RunScheduledCleanupAsync(CancellationToken.None);

            VerifyBulkDelete("untracked-media", Times.Once());
            _mockMediaRepository.Verify(x => x.HardDeleteAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
            VerifyOperationStatus(MediaCleanupTypes.Reconciliation, "Completed");
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_AppliesRetentionPolicies()
        {
            var options = CreateExecutionOptions();
            options.EnableExpirationCleanup = false;
            options.EnableReconciliation = false;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedDefaultRetentionPolicy();
            SeedVirtualKey(1, 1);
            SeedMediaRecords(1, 1, 100);

            var service = CreateService(options);
            await service.RunScheduledCleanupAsync(CancellationToken.None);

            VerifyBulkDelete("storage-key-1-0", Times.Once());
            VerifyOperationStatus(MediaCleanupTypes.Retention, "Completed");
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_EvictsOldestMediaUntilGroupIsUnderQuota()
        {
            var options = CreateExecutionOptions();
            options.EnableExpirationCleanup = false;
            options.EnableReconciliation = false;
            options.EnableRetentionCleanup = false;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedDefaultRetentionPolicy();
            var policy = await _context.MediaRetentionPolicies.SingleAsync();
            policy.MaxFileCount = 2;
            policy.RespectRecentAccess = false;
            SeedVirtualKey(1, 1);
            var oldest = CreateMediaRecord("quota-oldest", 1);
            oldest.CreatedAt = DateTime.UtcNow.AddDays(-3);
            var middle = CreateMediaRecord("quota-middle", 1);
            middle.CreatedAt = DateTime.UtcNow.AddDays(-2);
            var newest = CreateMediaRecord("quota-newest", 1);
            newest.CreatedAt = DateTime.UtcNow.AddDays(-1);
            _context.MediaRecords.AddRange(oldest, middle, newest);
            await _context.SaveChangesAsync();

            await CreateService(options).RunScheduledCleanupAsync(CancellationToken.None);

            _mockStorageService.Verify(
                storage => storage.DeleteManyAsync(
                    It.Is<IEnumerable<string>>(keys => keys.Contains(oldest.StorageKey)),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            _mockStorageService.Verify(
                storage => storage.DeleteManyAsync(
                    It.Is<IEnumerable<string>>(keys => keys.Contains(middle.StorageKey)),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            _mockStorageService.Verify(
                storage => storage.DeleteManyAsync(
                    It.Is<IEnumerable<string>>(keys => keys.Contains(newest.StorageKey)),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            VerifyOperationStatus(MediaCleanupTypes.Quota, "Completed");
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_QuotaEvictionRespectsRecentAccess()
        {
            var options = CreateExecutionOptions();
            options.EnableExpirationCleanup = false;
            options.EnableReconciliation = false;
            options.EnableRetentionCleanup = false;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedDefaultRetentionPolicy();
            var policy = await _context.MediaRetentionPolicies.SingleAsync();
            policy.MaxFileCount = 1;
            policy.RespectRecentAccess = true;
            policy.RecentAccessWindowDays = 7;
            SeedVirtualKey(1, 1);
            var protectedOldest = CreateMediaRecord("quota-recent", 1);
            protectedOldest.CreatedAt = DateTime.UtcNow.AddDays(-10);
            protectedOldest.LastAccessedAt = DateTime.UtcNow.AddHours(-1);
            var eligible = CreateMediaRecord("quota-eligible", 1);
            eligible.CreatedAt = DateTime.UtcNow.AddDays(-5);
            _context.MediaRecords.AddRange(protectedOldest, eligible);
            await _context.SaveChangesAsync();

            await CreateService(options).RunScheduledCleanupAsync(CancellationToken.None);

            _mockStorageService.Verify(
                storage => storage.DeleteManyAsync(
                    It.Is<IEnumerable<string>>(keys => keys.Contains(protectedOldest.StorageKey)),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            _mockStorageService.Verify(
                storage => storage.DeleteManyAsync(
                    It.Is<IEnumerable<string>>(keys => keys.Contains(eligible.StorageKey)),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_QuotaPagingHonorsSharedRunCap()
        {
            var options = CreateExecutionOptions();
            options.EnableExpirationCleanup = false;
            options.EnableReconciliation = false;
            options.EnableRetentionCleanup = false;
            options.CleanupPageSize = 2;
            options.MaxRecordsPerRun = 3;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedDefaultRetentionPolicy();
            var policy = await _context.MediaRetentionPolicies.SingleAsync();
            policy.MaxFileCount = 1;
            policy.RespectRecentAccess = false;
            SeedVirtualKey(1, 1);
            var now = DateTime.UtcNow;
            for (var index = 0; index < 5; index++)
            {
                _context.MediaRecords.Add(new MediaRecord
                {
                    Id = Guid.NewGuid(),
                    VirtualKeyId = 1,
                    StorageKey = $"quota-paged-{index}",
                    MediaType = "image",
                    SizeBytes = 100,
                    CreatedAt = now.AddMinutes(-10 + index)
                });
            }
            await _context.SaveChangesAsync();
            var deletedKeys = new List<string>();
            _mockStorageService
                .Setup(storage => storage.DeleteManyAsync(
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback((IEnumerable<string> keys, CancellationToken _) =>
                    deletedKeys.AddRange(keys))
                .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                    SuccessfulDelete(keys));

            await CreateService(options).RunScheduledCleanupAsync(CancellationToken.None);

            deletedKeys.Should().BeEquivalentTo(
                "quota-paged-0",
                "quota-paged-1",
                "quota-paged-2");
            VerifyOperationStatus(MediaCleanupTypes.Quota, "record cap reached");
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_StopsPhaseWhenDeletionBudgetWouldBeExceeded()
        {
            var options = CreateExecutionOptions();
            options.EnableReconciliation = false;
            options.EnableRetentionCleanup = false;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedVirtualKey(1, 1);
            _context.MediaRecords.Add(new MediaRecord
            {
                Id = Guid.NewGuid(),
                VirtualKeyId = 1,
                StorageKey = "over-budget-media",
                MediaType = "image",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
            });
            await _context.SaveChangesAsync();

            _mockBudgetService
                .Setup(x => x.ReserveAsync(
                    1,
                    options.MonthlyDeleteBudget,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaDeletionBudgetReservation(
                    1,
                    0,
                    options.MonthlyDeleteBudget));
            _mockBudgetService
                .Setup(x => x.GetMonthlyDeleteCountAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(options.MonthlyDeleteBudget);

            var service = CreateService(options);
            await service.RunScheduledCleanupAsync(CancellationToken.None);

            _mockStorageService.Verify(x => x.DeleteManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()), Times.Never);
            VerifyOperationStatus(MediaCleanupTypes.Expiration, "budget");
        }

        [Fact]
        public async Task RunScheduledCleanupAsync_ContinuesAfterPhaseDeletionFailure()
        {
            var options = CreateExecutionOptions();
            options.EnableRetentionCleanup = false;
            ArrangeLockAcquired();

            SeedTestGroup(1);
            SeedVirtualKey(1, 1);
            _context.MediaRecords.Add(new MediaRecord
            {
                Id = Guid.NewGuid(),
                VirtualKeyId = 1,
                StorageKey = "failing-expired-media",
                MediaType = "image",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
            });
            await _context.SaveChangesAsync();

            _mockStorageService
                .Setup(x => x.ListObjectsAsync(
                    null,
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaStorageObjectPage
                {
                    Objects =
                    [
                        new MediaStorageObject
                        {
                            StorageKey = "healthy-untracked-media",
                            SizeBytes = 1024,
                            LastModifiedUtc = DateTime.UtcNow.AddDays(-7)
                        }
                    ]
                });
            _mockStorageService
                .Setup(x => x.DeleteManyAsync(
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                    new MediaBulkDeleteResult
                    {
                        Items = keys.Select(key => new MediaDeleteItemResult
                        {
                            StorageKey = key,
                            Deleted = key != "failing-expired-media",
                            ErrorCode = key == "failing-expired-media"
                                ? "storage_unavailable"
                                : null
                        }).ToList()
                    });

            var service = CreateService(options);
            await service.RunScheduledCleanupAsync(CancellationToken.None);

            VerifyBulkDelete("healthy-untracked-media", Times.Once());
            VerifyOperationStatus(MediaCleanupTypes.Expiration, "errors");
            VerifyOperationStatus(MediaCleanupTypes.Reconciliation, "Completed");
        }

        #endregion

        #region Configuration Tests

        [Fact]
        public void IsSchedulerEnabled_WithEnabledTrue_ReturnsTrue()
        {
            // Arrange
            var options = new MediaLifecycleOptions { Enabled = true };

            // Assert
            options.IsSchedulerEnabled.Should().BeTrue();
        }

        [Fact]
        public void IsSchedulerEnabled_WithEnabledFalse_ReturnsFalse()
        {
            // Arrange
            var options = new MediaLifecycleOptions { Enabled = false };

            // Assert
            options.IsSchedulerEnabled.Should().BeFalse();
        }

        [Fact]
        public void DryRunMode_DefaultsToTrue()
        {
            // Arrange
            var options = new MediaLifecycleOptions();

            // Assert
            options.DryRunMode.Should().BeTrue();
        }

        [Fact]
        public void DryRunMode_CanBeSetToFalse()
        {
            // Arrange
            var options = new MediaLifecycleOptions { DryRunMode = false };

            // Assert
            options.DryRunMode.Should().BeFalse();
        }

        [Fact]
        public void MonthlyDeleteBudget_DefaultsTo500000()
        {
            // Arrange
            var options = new MediaLifecycleOptions();

            // Assert
            options.MonthlyDeleteBudget.Should().Be(500_000);
        }

        [Fact]
        public void MaxBatchSize_DefaultsToS3DeleteObjectsLimit()
        {
            // Arrange
            var options = new MediaLifecycleOptions();

            // Assert
            options.MaxBatchSize.Should().Be(1000);
        }

        [Fact]
        public void CleanupQueryLimits_HaveBoundedDefaults()
        {
            var options = new MediaLifecycleOptions();

            options.CleanupPageSize.Should().Be(1000);
            options.MaxRecordsPerRun.Should().Be(10_000);
        }

        [Fact]
        public void ScheduleIntervalMinutes_DefaultsTo60()
        {
            // Arrange
            var options = new MediaLifecycleOptions();

            // Assert
            options.ScheduleIntervalMinutes.Should().Be(60);
        }

        #endregion

        #region Test Group Filtering Tests

        [Fact]
        public void TestVirtualKeyGroups_WhenSet_FiltersGroups()
        {
            // Arrange
            var options = new MediaLifecycleOptions
            {
                Enabled = true,
                TestVirtualKeyGroups = new List<int> { 1, 3 }
            };

            // Assert
            options.TestVirtualKeyGroups.Should().Contain(1);
            options.TestVirtualKeyGroups.Should().Contain(3);
            options.TestVirtualKeyGroups.Should().HaveCount(2);
        }

        #endregion

        #region Stop/Shutdown Tests

        [Fact]
        public async Task StopAsync_LogsShutdownMessage()
        {
            // Arrange
            var options = new MediaLifecycleOptions { Enabled = false };
            var service = CreateService(options);

            // Act
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) =>
                        o.ToString()!.Contains("stop")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        #endregion

        #region Budget Integration Tests

        [Fact]
        public void BudgetService_IsIntegrated_InService()
        {
            // This test verifies that the budget service is properly injected
            // The actual budget checking is tested in the budget service tests
            var options = new MediaLifecycleOptions
            {
                Enabled = true,
                MonthlyDeleteBudget = 500_000
            };

            var service = CreateService(options);
            service.Should().NotBeNull();
        }

        #endregion

        #region Retention Policy Tests

        [Fact]
        public void RetentionDays_CalculatedByBalance_Positive()
        {
            // This tests the retention calculation logic
            // Positive balance = longer retention
            var policy = new MediaRetentionPolicy
            {
                PositiveBalanceRetentionDays = 60,
                ZeroBalanceRetentionDays = 30,
                NegativeBalanceRetentionDays = 7
            };

            decimal balance = 100m;
            var retentionDays = balance switch
            {
                > 0 => policy.PositiveBalanceRetentionDays,
                0 => policy.ZeroBalanceRetentionDays,
                < 0 => policy.NegativeBalanceRetentionDays
            };

            retentionDays.Should().Be(60);
        }

        [Fact]
        public void RetentionDays_CalculatedByBalance_Zero()
        {
            var policy = new MediaRetentionPolicy
            {
                PositiveBalanceRetentionDays = 60,
                ZeroBalanceRetentionDays = 30,
                NegativeBalanceRetentionDays = 7
            };

            decimal balance = 0m;
            var retentionDays = balance switch
            {
                > 0 => policy.PositiveBalanceRetentionDays,
                0 => policy.ZeroBalanceRetentionDays,
                < 0 => policy.NegativeBalanceRetentionDays
            };

            retentionDays.Should().Be(30);
        }

        [Fact]
        public void RetentionDays_CalculatedByBalance_Negative()
        {
            var policy = new MediaRetentionPolicy
            {
                PositiveBalanceRetentionDays = 60,
                ZeroBalanceRetentionDays = 30,
                NegativeBalanceRetentionDays = 7
            };

            decimal balance = -50m;
            var retentionDays = balance switch
            {
                > 0 => policy.PositiveBalanceRetentionDays,
                0 => policy.ZeroBalanceRetentionDays,
                < 0 => policy.NegativeBalanceRetentionDays
            };

            retentionDays.Should().Be(7);
        }

        #endregion

        private MediaLifecycleOptions CreateExecutionOptions() => new()
        {
            Enabled = true,
            EnableSoftDelete = false,
            DryRunMode = false,
            DelayBetweenBatchesMs = 0,
            MaxBatchSize = 50
        };

        private void ArrangeLockAcquired()
        {
            _mockLockService
                .Setup(x => x.TryAcquireAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockLock.Object);
        }

        private static MediaRecord CreateMediaRecord(string storageKey, int virtualKeyId) => new()
        {
            Id = Guid.NewGuid(),
            VirtualKeyId = virtualKeyId,
            StorageKey = storageKey,
            MediaType = "image",
            SizeBytes = 1024,
            CreatedAt = DateTime.UtcNow
        };

        private static MediaBulkDeleteResult SuccessfulDelete(IEnumerable<string> keys) => new()
        {
            Items = keys.Select(key => new MediaDeleteItemResult
            {
                StorageKey = key,
                Deleted = true
            }).ToList()
        };

        private void VerifyBulkDelete(string storageKey, Times times)
        {
            _mockStorageService.Verify(storage => storage.DeleteManyAsync(
                It.Is<IEnumerable<string>>(keys => keys.Contains(storageKey)),
                It.IsAny<CancellationToken>()), times);
        }

        private void VerifyOperationStatus(string cleanupType, string expectedStatusFragment)
        {
            _mockStatusService.Verify(x => x.RecordOperationCompletionAsync(
                cleanupType,
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<double>(),
                It.Is<string>(status => status.Contains(expectedStatusFragment, StringComparison.OrdinalIgnoreCase)),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ScheduledCleanup_BackendFailure_FailsClosed()
        {
            _mockLockService.Setup(service => service.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("isolated database outage"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(CreateExecutionOptions())
                .RunScheduledCleanupAsync(CancellationToken.None));
            _mockStorageService.Verify(service => service.DeleteManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ScheduledCleanup_DetectedLoss_DoesNotBeginProtectedWork()
        {
            using var lost = new CancellationTokenSource();
            lost.Cancel();
            _mockLock.SetupGet(handle => handle.HandleLostToken).Returns(lost.Token);
            _mockLockService.Setup(service => service.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockLock.Object);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(CreateExecutionOptions())
                .RunScheduledCleanupAsync(CancellationToken.None));
            _mockStorageService.Verify(service => service.DeleteManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockLock.Verify(handle => handle.DisposeAsync(), Times.Once);
        }

        public void Dispose()
        {
            _context?.Dispose();
            _serviceProvider?.Dispose();
            _database.Dispose();
        }

        private sealed class MutableOptions<T>(T value) : IOptions<T>
            where T : class
        {
            public T Value { get; set; } = value;
        }
    }
}
