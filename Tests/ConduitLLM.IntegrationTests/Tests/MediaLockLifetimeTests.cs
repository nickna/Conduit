using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
[Trait("Category", "Integration")]
[Trait("Component", "DistributedLock")]
public sealed class MediaLockLifetimeTests(PostgresLockTestContainerFixture fixture)
{
    private PostgresDistributedLockProvider Provider() => new(fixture.ConnectionString,
        NullLogger<PostgresDistributedLockProvider>.Instance);

    [Fact]
    public async Task ActualOwnershipLoss_CancelsDeletion_StopsFurtherBatches_RecordsCancelled()
    {
        var holder = await Provider().TryAcquireAsync(MediaCleanupLock.Key);
        Assert.NotNull(holder);
        using var workCancellation = holder.CreateOperationCancellation(CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new Mock<IMediaStorageService>();
        storage.Setup(service => service.DeleteManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> _, CancellationToken token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new MediaBulkDeleteResult();
            });
        var budget = new Mock<IMediaDeletionBudgetService>();
        budget.Setup(service => service.ReserveAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int requested, int _, CancellationToken _) => new MediaDeletionBudgetReservation(requested, requested, requested));
        var guard = new Mock<IMediaStorageConfigurationGuard>();
        guard.Setup(service => service.ValidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var status = new Mock<IMediaCleanupStatusService>();
        var engine = new MediaDeletionEngine(storage.Object, budget.Object, Mock.Of<IMediaRecordRepository>(),
            status.Object, Mock.Of<IMediaCleanupApprovalService>(), guard.Object, Options.Create(new MediaLifecycleOptions
            {
                DryRunMode = false, EnableSoftDelete = false, MaxBatchSize = 1, BudgetReservationStride = 1,
                RequireManualApprovalForLargeBatches = false,
            }), NullLogger<MediaDeletionEngine>.Instance);
        var operation = new MediaDeletionOperationContext(MediaCleanupTypes.Manual, "manual", "test-loss");
        var records = Enumerable.Range(0, 3).Select(i => new MediaRecord
        {
            Id = Guid.NewGuid(), VirtualKeyId = 1, StorageKey = $"isolated-mock-{i}", MediaType = "image",
        }).ToArray();
        var work = engine.ExecuteOperationAsync(operation,
            () => engine.DeleteAsync(new MediaDeletionRequest(records, operation, Purge: true), workCancellation.Token),
            workCancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await Provider().TryAcquireAsync(MediaCleanupLock.Key));
            await LockTestSession.TerminateAsync(fixture.ConnectionString, MediaCleanupLock.Key);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(holder.HandleLostToken.IsCancellationRequested);
            storage.Verify(service => service.DeleteManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
            status.Verify(service => service.RecordOperationCompletionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long>(),
                It.IsAny<double>(), "Cancelled", It.IsAny<string>(), It.IsAny<string>(),
                It.Is<CancellationToken>(token => !token.IsCancellationRequested)), Times.Once);
            await using var successor = await Provider().TryAcquireAsync(MediaCleanupLock.Key);
            Assert.NotNull(successor);
        }
        finally
        {
            workCancellation.Cancel();
            try { await work; } catch (OperationCanceledException) { }
            try { await holder.DisposeAsync(); }
            catch (InvalidOperationException) when (holder.HandleLostToken.IsCancellationRequested) { }
        }
    }
}
