using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Configuration.Repositories;

public sealed class AsyncTaskRepositoryLifecycleTests : IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = new();
    private AsyncTaskRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _database.SeedAsync(DurableLifecycleTestData.SeedRequiredGraphAsync);
        _repository = new AsyncTaskRepository(
            _database.CreateDbContextFactory(),
            NullLogger<AsyncTaskRepository>.Instance);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task ExtendLease_RequiresOwnerAndUnexpiredLease()
    {
        var now = DateTime.UtcNow;
        var active = DurableLifecycleTestData.NewAsyncTask("active", now.AddMinutes(-2), state: 1);
        active.LeasedBy = "owner";
        active.LeaseExpiryTime = now.AddMinutes(10);
        var expired = DurableLifecycleTestData.NewAsyncTask("expired", now.AddMinutes(-3), state: 1);
        expired.LeasedBy = "owner";
        expired.LeaseExpiryTime = now.AddMinutes(-10);
        await SeedTasksAsync(active, expired);

        Assert.False(await _repository.ExtendLeaseAsync(
            "active", "other", TimeSpan.FromMinutes(20)));
        Assert.True(await _repository.ExtendLeaseAsync(
            "active", "owner", TimeSpan.FromMinutes(20)));
        Assert.False(await _repository.ExtendLeaseAsync(
            "expired", "owner", TimeSpan.FromMinutes(20)));

        await using var verification = _database.CreateContext();
        var durable = await verification.AsyncTasks.AsNoTracking()
            .OrderBy(t => t.Id)
            .ToListAsync();
        Assert.Equal("owner", durable.Single(t => t.Id == "active").LeasedBy);
        Assert.True(durable.Single(t => t.Id == "active").LeaseExpiryTime > now.AddMinutes(19));
        Assert.Equal("owner", durable.Single(t => t.Id == "expired").LeasedBy);
    }

    [Fact]
    public async Task ProviderPhases_RequireOwningWorkerAndPersistProviderData()
    {
        var task = DurableLifecycleTestData.NewAsyncTask(
            "provider-phase", DateTime.UtcNow.AddMinutes(-1), state: 1);
        task.LeasedBy = "owner";
        task.LeaseExpiryTime = DateTime.UtcNow.AddMinutes(10);
        await SeedTasksAsync(task);

        Assert.False(await _repository.MarkProviderInvocationStartedAsync(
            "provider-phase", "other"));
        Assert.True(await _repository.MarkProviderInvocationStartedAsync(
            "provider-phase", "owner"));
        Assert.False(await _repository.MarkProviderInvocationCompletedAsync(
            "provider-phase", "other", "wrong-operation"));
        Assert.True(await _repository.MarkProviderInvocationCompletedAsync(
            "provider-phase", "owner", "provider-operation-42"));

        await using var verification = _database.CreateContext();
        var durable = await verification.AsyncTasks.AsNoTracking().SingleAsync();
        Assert.NotNull(durable.ProviderInvocationStartedAt);
        Assert.NotNull(durable.ProviderInvocationCompletedAt);
        Assert.Equal("provider-operation-42", durable.ProviderOperationId);
    }

    [Fact]
    public async Task TryClaimTaskAsync_ReturnsOutcomeForEveryLifecycleCategory()
    {
        var now = DateTime.UtcNow;
        var terminal = DurableLifecycleTestData.NewAsyncTask("terminal", now, state: 2);
        var claimed = DurableLifecycleTestData.NewAsyncTask("claimed", now, state: 1);
        claimed.LeasedBy = "worker";
        claimed.LeaseExpiryTime = now.AddMinutes(10);
        var indeterminate = DurableLifecycleTestData.NewAsyncTask("indeterminate", now, state: 6);
        await SeedTasksAsync(terminal, claimed, indeterminate);

        Assert.Equal(AsyncTaskClaimResult.Missing,
            await _repository.TryClaimTaskAsync("missing", "new-worker", TimeSpan.FromMinutes(5)));
        Assert.Equal(AsyncTaskClaimResult.Terminal,
            await _repository.TryClaimTaskAsync("terminal", "new-worker", TimeSpan.FromMinutes(5)));
        Assert.Equal(AsyncTaskClaimResult.AlreadyClaimed,
            await _repository.TryClaimTaskAsync("claimed", "new-worker", TimeSpan.FromMinutes(5)));
        Assert.Equal(AsyncTaskClaimResult.Indeterminate,
            await _repository.TryClaimTaskAsync("indeterminate", "new-worker", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task IndeterminateResolution_PreparesIdempotentRetryOrRecordsNoChargeFailure()
    {
        var now = DateTime.UtcNow;
        var retry = NewIndeterminateTask("retry", now);
        var terminal = NewIndeterminateTask("terminal", now);
        await SeedTasksAsync(retry, terminal);

        var prepared = await _repository.PrepareIndeterminateTaskRetryAsync(
            "retry", "dispatch-1", "provider confirmed no work");
        var redelivered = await _repository.PrepareIndeterminateTaskRetryAsync(
            "retry", "dispatch-1", "provider confirmed no work");
        Assert.Equal(IndeterminateTaskRetryPreparationStatus.Prepared, prepared.Status);
        Assert.Equal(IndeterminateTaskRetryPreparationStatus.AlreadyPrepared, redelivered.Status);
        Assert.True(await _repository.FailIndeterminateTaskWithoutChargeAsync(
            "terminal", "provider failed", providerOperationId: "final-id"));

        await using var verification = _database.CreateContext();
        var rows = await verification.AsyncTasks.AsNoTracking().ToListAsync();
        var retryRow = rows.Single(t => t.Id == "retry");
        Assert.Equal(0, retryRow.State);
        Assert.True(retryRow.IsRetryable);
        Assert.Equal(3, retryRow.RetryCount);
        Assert.Null(retryRow.LeasedBy);
        Assert.Null(retryRow.LeaseExpiryTime);
        Assert.Null(retryRow.ProviderInvocationStartedAt);
        Assert.Null(retryRow.ProviderInvocationCompletedAt);
        Assert.Null(retryRow.ProviderOperationId);
        Assert.Equal("dispatch-1", retryRow.RetryDispatchId);
        Assert.Null(retryRow.CompletedAt);
        Assert.Null(retryRow.NextRetryAt);

        var terminalRow = rows.Single(t => t.Id == "terminal");
        Assert.Equal(3, terminalRow.State);
        Assert.False(terminalRow.IsRetryable);
        Assert.Equal("final-id", terminalRow.ProviderOperationId);
        Assert.NotNull(terminalRow.CompletedAt);
        Assert.Equal("provider failed", terminalRow.Error);
    }

    [Fact]
    public async Task GetByStateAsync_FiltersOrdersAndPaginates()
    {
        var now = DateTime.UtcNow;
        var oldest = NewIndeterminateTask("oldest", now.AddMinutes(-3));
        var newest = NewIndeterminateTask("newest", now.AddMinutes(-1));
        var middle = NewIndeterminateTask("middle", now.AddMinutes(-2));
        var failed = DurableLifecycleTestData.NewAsyncTask("failed", now, state: 3);
        await SeedTasksAsync(oldest, newest, middle, failed);

        var (tasks, totalCount) = await _repository.GetByStateAsync(6, page: 2, pageSize: 1);

        Assert.Equal(3, totalCount);
        Assert.Equal(new[] { "middle" }, tasks.Select(task => task.Id));
    }

    [Fact]
    public async Task ArchiveCleanupAndBulkDelete_OnlyAffectEligibleRows()
    {
        var now = DateTime.UtcNow;
        var oldCompleted = DurableLifecycleTestData.NewAsyncTask("old-completed", now.AddDays(-10), state: 2);
        oldCompleted.CompletedAt = now.AddDays(-5);
        var recentCompleted = DurableLifecycleTestData.NewAsyncTask("recent-completed", now.AddDays(-2), state: 2);
        recentCompleted.CompletedAt = now.AddHours(-2);
        var oldProcessing = DurableLifecycleTestData.NewAsyncTask("old-processing", now.AddDays(-10), state: 1);
        oldProcessing.CompletedAt = now.AddDays(-5);
        var cleanup = DurableLifecycleTestData.NewAsyncTask("cleanup", now.AddDays(-20), state: 2);
        cleanup.CompletedAt = now.AddDays(-20);
        cleanup.IsArchived = true;
        cleanup.ArchivedAt = now.AddDays(-10);
        var recentArchive = DurableLifecycleTestData.NewAsyncTask("recent-archive", now.AddDays(-2), state: 2);
        recentArchive.CompletedAt = now.AddDays(-2);
        recentArchive.IsArchived = true;
        recentArchive.ArchivedAt = now.AddHours(-2);
        await SeedTasksAsync(oldCompleted, recentCompleted, oldProcessing, cleanup, recentArchive);

        Assert.Equal(1, await _repository.ArchiveOldTasksAsync(TimeSpan.FromDays(1)));
        var cleanupRows = await _repository.GetTasksForCleanupAsync(
            TimeSpan.FromDays(1), limit: 10);
        Assert.Equal(new[] { "cleanup" }, cleanupRows.Select(t => t.Id));
        Assert.Equal(1, await _repository.BulkDeleteAsync(
            cleanupRows.Select(t => t.Id).Append("missing")));

        await using var verification = _database.CreateContext();
        var rows = await verification.AsyncTasks.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(rows, t => t.Id == "cleanup");
        Assert.True(rows.Single(t => t.Id == "old-completed").IsArchived);
        Assert.False(rows.Single(t => t.Id == "recent-completed").IsArchived);
        Assert.False(rows.Single(t => t.Id == "old-processing").IsArchived);
        Assert.True(rows.Single(t => t.Id == "recent-archive").IsArchived);
    }

    [Fact]
    public async Task ArchiveOldTasks_ExpiresStaleActiveRowsButPreservesIndeterminateWork()
    {
        var now = DateTime.UtcNow;
        var pending = DurableLifecycleTestData.NewAsyncTask(
            "stale-pending", now.AddDays(-10), state: 0);
        var processing = DurableLifecycleTestData.NewAsyncTask(
            "stale-processing", now.AddDays(-10), state: 1);
        processing.LeaseExpiryTime = now.AddDays(-2);
        var uncertain = DurableLifecycleTestData.NewAsyncTask(
            "provider-outcome-unknown", now.AddDays(-10), state: 1);
        uncertain.ProviderInvocationStartedAt = now.AddDays(-9);
        uncertain.LeaseExpiryTime = now.AddDays(-2);
        var indeterminate = DurableLifecycleTestData.NewAsyncTask(
            "indeterminate", now.AddDays(-10), state: 6);
        await SeedTasksAsync(pending, processing, uncertain, indeterminate);

        var archived = await _repository.ArchiveOldTasksAsync(
            TimeSpan.FromDays(30),
            TimeSpan.FromDays(7));

        Assert.Equal(2, archived);
        await using var verification = _database.CreateContext();
        var rows = await verification.AsyncTasks.AsNoTracking().ToListAsync();
        Assert.All(
            rows.Where(task => task.Id is "stale-pending" or "stale-processing"),
            task =>
            {
                Assert.True(task.IsArchived);
                Assert.Equal(5, task.State);
            });
        Assert.False(rows.Single(task => task.Id == "provider-outcome-unknown").IsArchived);
        Assert.False(rows.Single(task => task.Id == "indeterminate").IsArchived);
    }

    private async Task SeedTasksAsync(params AsyncTask[] tasks)
    {
        await _database.SeedAsync(async context =>
        {
            context.AsyncTasks.AddRange(tasks);
            await context.SaveChangesAsync();
        });
    }

    private static AsyncTask NewIndeterminateTask(string id, DateTime now)
    {
        var task = DurableLifecycleTestData.NewAsyncTask(id, now.AddMinutes(-10), state: 6);
        task.RetryCount = 2;
        task.LeasedBy = "dead-worker";
        task.LeaseExpiryTime = now.AddMinutes(-5);
        task.ProviderInvocationStartedAt = now.AddMinutes(-8);
        task.ProviderInvocationCompletedAt = now.AddMinutes(-7);
        task.ProviderOperationId = "original-id";
        task.CompletedAt = now.AddMinutes(-5);
        task.NextRetryAt = now.AddMinutes(10);
        return task;
    }

}
