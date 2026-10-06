using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Metrics;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Admin.Services;

[Trait("Category", "Unit")]
[Trait("Component", "MediaLifecycle")]
public sealed class MediaDeletionEngineTests
{
    private readonly Mock<IMediaStorageService> _storage = new();
    private readonly Mock<IMediaDeletionBudgetService> _budget = new();
    private readonly Mock<IMediaRecordRepository> _repository = new();
    private readonly Mock<IMediaCleanupStatusService> _status = new();
    private readonly Mock<IMediaCleanupApprovalService> _approvals = new();
    private readonly Mock<IMediaStorageConfigurationGuard> _storageGuard = new();

    public MediaDeletionEngineTests()
    {
        _storageGuard
            .Setup(guard => guard.ValidateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _storage
            .Setup(storage => storage.DeleteManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                SuccessfulDelete(keys));
        _repository
            .Setup(repository => repository.HardDeleteAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repository
            .Setup(repository => repository.TombstoneAsync(
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _budget
            .Setup(budget => budget.ReserveAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int requested, int _, CancellationToken _) =>
                new MediaDeletionBudgetReservation(requested, requested, requested));
        _approvals
            .Setup(service => service.CreateOrRefreshPendingAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string type, int? groupId, int count, long bytes, DateTime cutoff, CancellationToken _) =>
                new MediaCleanupApproval
                {
                    Id = Guid.NewGuid(),
                    CleanupType = type,
                    VirtualKeyGroupId = groupId,
                    CandidateCount = count,
                    CandidateBytes = bytes,
                    CutoffUtc = cutoff
                });
    }

    [Fact]
    public async Task DeleteAsync_DryRun_ReportsScopeWithoutMutatingStorageOrBudget()
    {
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = true,
            EnableSoftDelete = false,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = false
        });
        var records = CreateRecords(2);

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            records,
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Retention, "manual", "test")));

        result.Should().BeEquivalentTo(new
        {
            FilesDeleted = 0,
            BytesFreed = 0L,
            Failures = 0,
            WouldDeleteCount = 2,
            BytesWouldFree = 300L,
            IsDryRun = true
        });
        _storage.Verify(storage => storage.DeleteManyAsync(
            It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(repository => repository.HardDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _budget.Verify(budget => budget.ReserveAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_ForceOverride_DeletesAndPreservesRecordWhenStorageFails()
    {
        var records = CreateRecords(2);
        _storage
            .Setup(storage => storage.DeleteManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                new MediaBulkDeleteResult
                {
                    Items = keys.Select(key => new MediaDeleteItemResult
                    {
                        StorageKey = key,
                        Deleted = key != records[1].StorageKey,
                        ErrorCode = key == records[1].StorageKey ? "denied" : null
                    }).ToList()
                });
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = true,
            EnableSoftDelete = false,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = false
        });
        var operation = new MediaDeletionOperationContext(
            MediaCleanupTypes.Retention, "manual", "test", Force: true);

        var result = await engine.DeleteAsync(new MediaDeletionRequest(records, operation));

        result.FilesDeleted.Should().Be(1);
        result.BytesFreed.Should().Be(100);
        result.Failures.Should().Be(1);
        result.IsDryRun.Should().BeFalse();
        _repository.Verify(repository => repository.HardDeleteAsync(
            records[0].Id, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(repository => repository.HardDeleteAsync(
            records[1].Id, It.IsAny<CancellationToken>()), Times.Never);
        _budget.Verify(budget => budget.ReserveAsync(
            2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenBudgetWouldBeExceeded_StopsBeforeDeletion()
    {
        _budget
            .Setup(budget => budget.ReserveAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int requested, int _, CancellationToken _) =>
                new MediaDeletionBudgetReservation(requested, 0, 50));
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = false,
            EnableSoftDelete = false,
            MonthlyDeleteBudget = 50,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = false
        });

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            CreateRecords(2),
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Expiration, "scheduled", "test")));

        result.BudgetExhausted.Should().BeTrue();
        result.FilesDeleted.Should().Be(0);
        _storage.Verify(storage => storage.DeleteManyAsync(
            It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_LargeBatch_DryRunAndExplicitForceAreAllowed()
    {
        var records = CreateRecords(2);
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = true,
            EnableSoftDelete = false,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = true,
            LargeBatchThreshold = 1
        });

        var preview = await engine.DeleteAsync(new MediaDeletionRequest(
            records,
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Retention, "manual", "preview")));
        var forced = await engine.DeleteAsync(new MediaDeletionRequest(
            records,
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Retention, "manual", "force", Force: true)));

        preview.WouldDeleteCount.Should().Be(2);
        forced.FilesDeleted.Should().Be(2);
    }

    [Fact]
    public async Task DeleteAsync_LargeScheduledBatch_CreatesPendingApproval()
    {
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = false,
            EnableSoftDelete = false,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = true,
            LargeBatchThreshold = 1
        });

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            CreateRecords(2),
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Retention, "scheduled", "scheduler"),
            GroupId: 42));

        result.StatusOverride.Should().Be("Skipped: pending manual approval");
        _approvals.Verify(service => service.CreateOrRefreshPendingAsync(
            MediaCleanupTypes.Retention,
            42,
            2,
            300,
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _storage.Verify(storage => storage.DeleteManyAsync(
            It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_WhenBudgetHasPartialStride_DeletesGrantedPrefix()
    {
        _budget
            .Setup(budget => budget.ReserveAsync(
                2,
                50,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDeletionBudgetReservation(2, 1, 50));
        var records = CreateRecords(2);
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = false,
            EnableSoftDelete = false,
            MonthlyDeleteBudget = 50,
            MaxBatchSize = 10,
            BudgetReservationStride = 10,
            RequireManualApprovalForLargeBatches = false
        });

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            records,
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Expiration, "scheduled", "test")));

        result.BudgetExhausted.Should().BeTrue();
        result.FilesDeleted.Should().Be(1);
        _repository.Verify(repository => repository.HardDeleteAsync(
            records[0].Id, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(repository => repository.HardDeleteAsync(
            records[1].Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_WhenStorageThrottles_RetriesWithExponentialBackoff()
    {
        var calls = 0;
        _storage
            .Setup(storage => storage.DeleteManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
            {
                calls++;
                return calls == 1
                    ? new MediaBulkDeleteResult
                    {
                        Items = keys.Select(key => new MediaDeleteItemResult
                        {
                            StorageKey = key,
                            IsRetryable = true,
                            ErrorCode = "SlowDown"
                        }).ToList()
                    }
                    : SuccessfulDelete(keys);
            });
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = false,
            EnableSoftDelete = false,
            MaxBatchSize = 1000,
            BudgetReservationStride = 1000,
            DeleteThrottleMaxRetries = 2,
            DeleteThrottleInitialBackoffMs = 0,
            RequireManualApprovalForLargeBatches = false
        });

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            CreateRecords(3),
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Purge, "scheduled", "test"),
            Purge: true));

        result.FilesDeleted.Should().Be(3);
        result.Failures.Should().Be(0);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task DeleteAsync_FiveThousandObjects_UsesFiveBulkStorageCalls()
    {
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = false,
            EnableSoftDelete = false,
            MaxBatchSize = 1000,
            BudgetReservationStride = 1000,
            DelayBetweenBatchesMs = 0,
            RequireManualApprovalForLargeBatches = false
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            CreateRecords(5000),
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Purge, "scheduled", "test"),
            Purge: true));
        stopwatch.Stop();

        result.FilesDeleted.Should().Be(5000);
        _storage.Verify(storage => storage.DeleteManyAsync(
            It.Is<IEnumerable<string>>(keys => keys.Count() == 1000),
            It.IsAny<CancellationToken>()), Times.Exactly(5));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeleteAsync_ReservesBeforeStorageInConfiguredCrashBoundedStrides()
    {
        var reservations = new List<int>();
        var unconsumedReservations = 0;
        _budget
            .Setup(budget => budget.ReserveAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int requested, int _, CancellationToken _) =>
            {
                reservations.Add(requested);
                unconsumedReservations += requested;
                return new MediaDeletionBudgetReservation(requested, requested, requested);
            });
        _storage
            .Setup(storage => storage.DeleteManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
            {
                var keyList = keys.ToList();
                unconsumedReservations.Should().BeGreaterThanOrEqualTo(keyList.Count);
                unconsumedReservations -= keyList.Count;
                return SuccessfulDelete(keyList);
            });
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = false,
            EnableSoftDelete = false,
            MaxBatchSize = 50,
            BudgetReservationStride = 2,
            RequireManualApprovalForLargeBatches = false
        });

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            CreateRecords(5),
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Purge, "scheduled", "test"),
            Purge: true));

        result.FilesDeleted.Should().Be(5);
        reservations.Should().Equal(2, 2, 1);
        unconsumedReservations.Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_ApprovedScope_ExecutesFreshLargerCandidateSet()
    {
        var cutoff = DateTime.UtcNow;
        var approval = new MediaCleanupApproval
        {
            Id = Guid.NewGuid(),
            CleanupType = MediaCleanupTypes.Retention,
            VirtualKeyGroupId = 42,
            Status = MediaCleanupApprovalStatuses.Approved,
            CutoffUtc = cutoff
        };
        _approvals
            .Setup(service => service.GetActiveApprovalAsync(
                MediaCleanupTypes.Retention,
                42,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(approval);
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = false,
            EnableSoftDelete = false,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = true,
            LargeBatchThreshold = 1
        });

        var records = CreateRecords(3);
        records[0].CreatedAt = cutoff.AddMinutes(-2);
        records[1].CreatedAt = cutoff.AddMinutes(-1);
        records[2].CreatedAt = cutoff.AddMinutes(1);

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            records,
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Retention, "scheduled", "scheduler"),
            GroupId: 42));

        result.FilesDeleted.Should().Be(2);
        _approvals.Verify(service => service.RecordExecutionAsync(
            approval.Id,
            It.Is<MediaDeletionEngineResult>(execution => execution.FilesDeleted == 2),
            true,
            It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(repository => repository.HardDeleteAsync(
            records[2].Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_SoftDelete_TombstonesWithoutStorageOrBudget()
    {
        var records = CreateRecords(2);
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            EnableSoftDelete = true,
            DryRunMode = false,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = false
        });

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            records,
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Retention, "scheduled", "test")));

        result.RecordsTombstoned.Should().Be(2);
        result.FilesDeleted.Should().Be(0);
        result.BytesFreed.Should().Be(0);
        _repository.Verify(repository => repository.TombstoneAsync(
            It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _storage.Verify(storage => storage.DeleteManyAsync(
            It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _budget.Verify(budget => budget.ReserveAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_SoftDeleteDryRun_ReportsTombstonesWithoutFreedBytes()
    {
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            EnableSoftDelete = true,
            DryRunMode = true,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = false
        });

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            CreateRecords(2),
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Expiration, "manual", "test")));

        result.WouldTombstoneCount.Should().Be(2);
        result.WouldDeleteCount.Should().Be(0);
        result.BytesWouldFree.Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_Purge_PermanentlyDeletesAndConsumesBudget()
    {
        var records = CreateRecords(2);
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            EnableSoftDelete = true,
            DryRunMode = false,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = false
        });

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            records,
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Purge, "scheduled", "test"),
            Purge: true));

        result.FilesDeleted.Should().Be(2);
        result.BytesFreed.Should().Be(300);
        result.RecordsTombstoned.Should().Be(0);
        _storage.Verify(storage => storage.DeleteManyAsync(
            It.Is<IEnumerable<string>>(keys => keys.Count() == 2),
            It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(repository => repository.HardDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _budget.Verify(budget => budget.ReserveAsync(
            2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_VirtualKeyRemoval_PermanentlyDeletesEvenWithSoftDeleteEnabled()
    {
        var record = CreateRecords(1);
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            EnableSoftDelete = true,
            DryRunMode = false,
            MaxBatchSize = 10,
            RequireManualApprovalForLargeBatches = false
        });

        var result = await engine.DeleteAsync(new MediaDeletionRequest(
            record,
            new MediaDeletionOperationContext(
                MediaCleanupTypes.VirtualKey, "system", "test")));

        result.FilesDeleted.Should().Be(1);
        result.RecordsTombstoned.Should().Be(0);
        _repository.Verify(repository => repository.HardDeleteAsync(
            record[0].Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(MediaCleanupTypes.Expiration, "scheduled", false)]
    [InlineData(MediaCleanupTypes.Retention, "manual", false)]
    [InlineData(MediaCleanupTypes.Reconciliation, "scheduled", false)]
    [InlineData(MediaCleanupTypes.Purge, "scheduled", true)]
    [InlineData(MediaCleanupTypes.VirtualKey, "system", true)]
    public async Task DeleteAsync_EveryPermanentDeletionPath_ReservesBudget(
        string cleanupType,
        string triggeredBy,
        bool purge)
    {
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            EnableSoftDelete = false,
            DryRunMode = false,
            MaxBatchSize = 10,
            BudgetReservationStride = 10,
            RequireManualApprovalForLargeBatches = false
        });

        await engine.DeleteAsync(new MediaDeletionRequest(
            CreateRecords(1),
            new MediaDeletionOperationContext(cleanupType, triggeredBy, "test"),
            Purge: purge));

        _budget.Verify(budget => budget.ReserveAsync(
            1,
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteOperationAsync_RecordsManualTriggerAndOutcome()
    {
        var engine = CreateEngine(new MediaLifecycleOptions());
        var operation = new MediaDeletionOperationContext(
            MediaCleanupTypes.Reconciliation, "manual", "manual:test");

        var result = await engine.ExecuteOperationAsync(
            operation,
            () => Task.FromResult(new MediaDeletionEngineResult(
                FilesDeleted: 3,
                BytesFreed: 900)));

        result.OperationStatus.Should().Be("Completed");
        _status.Verify(status => status.RecordOperationCompletionAsync(
            MediaCleanupTypes.Reconciliation,
            3,
            900,
            It.IsAny<double>(),
            "Completed",
            "manual:test",
            "manual",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteOperationAsync_DryRun_DoesNotIncrementDeletionMetrics()
    {
        var cleanupType = $"dry-run-test-{Guid.NewGuid():N}";
        var filesDeleted = AdminMediaCleanupMetrics.FilesDeleted
            .WithLabels(cleanupType);
        var bytesFreed = AdminMediaCleanupMetrics.BytesFreed
            .WithLabels(cleanupType);
        var dryRunFiles = AdminMediaCleanupMetrics.DryRunFilesMatched
            .WithLabels(cleanupType);
        var dryRunBytes = AdminMediaCleanupMetrics.DryRunBytesMatched
            .WithLabels(cleanupType);
        var filesDeletedBefore = filesDeleted.Value;
        var bytesFreedBefore = bytesFreed.Value;
        var dryRunFilesBefore = dryRunFiles.Value;
        var dryRunBytesBefore = dryRunBytes.Value;
        var engine = CreateEngine(new MediaLifecycleOptions());

        await engine.ExecuteOperationAsync(
            new MediaDeletionOperationContext(cleanupType, "scheduled", "test"),
            () => Task.FromResult(new MediaDeletionEngineResult(
                FilesDeleted: 3,
                BytesFreed: 900,
                WouldDeleteCount: 3,
                BytesWouldFree: 900,
                IsDryRun: true)));

        filesDeleted.Value.Should().Be(filesDeletedBefore);
        bytesFreed.Value.Should().Be(bytesFreedBefore);
        dryRunFiles.Value.Should().Be(dryRunFilesBefore + 3);
        dryRunBytes.Value.Should().Be(dryRunBytesBefore + 900);
    }

    [Fact]
    public async Task ExecuteOperationAsync_FailureAndHighBudget_PublishesOperationalAlerts()
    {
        var eventBus = new Mock<IEventBus>();
        eventBus
            .Setup(bus => bus.PublishAsync(
                It.IsAny<MediaCleanupAlertRaised>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _budget
            .Setup(budget => budget.GetMonthlyDeleteCountAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(95);
        var engine = CreateEngine(
            new MediaLifecycleOptions
            {
                MonthlyDeleteBudget = 100,
                BudgetAlertThresholdPercent = 90
            },
            eventBus.Object);

        await engine.ExecuteOperationAsync(
            new MediaDeletionOperationContext(
                MediaCleanupTypes.Retention,
                "scheduled",
                "leader-1"),
            () => Task.FromResult(new MediaDeletionEngineResult(Failures: 1)));

        eventBus.Verify(bus => bus.PublishAsync(
            It.Is<MediaCleanupAlertRaised>(alert =>
                alert.Kind == MediaCleanupAlertKind.OperationFailure &&
                alert.CleanupType == MediaCleanupTypes.Retention),
            It.IsAny<CancellationToken>()), Times.Once);
        eventBus.Verify(bus => bus.PublishAsync(
            It.Is<MediaCleanupAlertRaised>(alert =>
                alert.Kind == MediaCleanupAlertKind.BudgetThreshold &&
                alert.MonthlyDeleteCount == 95 &&
                alert.MonthlyDeleteBudget == 100 &&
                alert.BudgetUsedPercent == 95),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellationAfterUncooperativeStorage_DoesNotStartAnotherBatchOrReportSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var engine = CreateEngine(new MediaLifecycleOptions
        {
            DryRunMode = false, EnableSoftDelete = false, MaxBatchSize = 1,
            BudgetReservationStride = 1, RequireManualApprovalForLargeBatches = false,
        });
        _storage.Setup(service => service.DeleteManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
            {
                cancellation.Cancel();
                return SuccessfulDelete(keys);
            });
        var operation = new MediaDeletionOperationContext(MediaCleanupTypes.Manual, "manual", "test");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteOperationAsync(operation,
            () => engine.DeleteAsync(new MediaDeletionRequest(CreateRecords(3), operation, Purge: true), cancellation.Token),
            cancellation.Token));
        _storage.Verify(service => service.DeleteManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(repository => repository.HardDeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _status.Verify(service => service.RecordOperationCompletionAsync(It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<long>(), It.IsAny<double>(), "Cancelled", It.IsAny<string>(), It.IsAny<string>(),
            It.Is<CancellationToken>(token => !token.IsCancellationRequested)), Times.Once);
    }

    private MediaDeletionEngine CreateEngine(
        MediaLifecycleOptions options,
        IEventBus? eventBus = null) => new(
        _storage.Object,
        _budget.Object,
        _repository.Object,
        _status.Object,
        _approvals.Object,
        _storageGuard.Object,
        Options.Create(options),
        Mock.Of<ILogger<MediaDeletionEngine>>(),
        eventBus);

    private static MediaBulkDeleteResult SuccessfulDelete(IEnumerable<string> keys) => new()
    {
        Items = keys.Select(key => new MediaDeleteItemResult
        {
            StorageKey = key,
            Deleted = true
        }).ToList()
    };

    private static List<MediaRecord> CreateRecords(int count) =>
        Enumerable.Range(1, count)
            .Select(index => new MediaRecord
            {
                Id = Guid.NewGuid(),
                VirtualKeyId = 1,
                StorageKey = $"media-{index}",
                MediaType = "image",
                SizeBytes = index * 100
            })
            .ToList();
}
