using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConduitLLM.Tests.Configuration.Repositories;

public sealed class AsyncTaskRepositoryClaimTests : IDisposable
{
    private readonly SqliteTestDatabase _database;
    private readonly DbContextOptions<ConduitDbContext> _options;
    private readonly AsyncTaskRepository _repository;

    public AsyncTaskRepositoryClaimTests()
    {
        _database = new SqliteTestDatabase();
        _options = _database.Options;
        using var context = _database.CreateContext();
        context.VirtualKeyGroups.Add(new VirtualKeyGroup
        {
            Id = 1,
            GroupName = "Async task test group"
        });
        context.VirtualKeys.Add(new VirtualKey
        {
            Id = 1,
            VirtualKeyGroupId = 1,
            KeyName = "Async task test key",
            KeyHash = "async-task-test-key",
            IsEnabled = true
        });
        context.AsyncTasks.Add(new AsyncTask
        {
            Id = "media-claim-1",
            Type = "image_generation",
            State = 0,
            VirtualKeyId = 1
        });
        context.SaveChanges();

        var factory = new Mock<IDbContextFactory<ConduitDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _database.CreateContext());
        _repository = new AsyncTaskRepository(
            factory.Object, Mock.Of<ILogger<AsyncTaskRepository>>());
    }

    [Fact]
    public async Task ExpiredOwner_CannotStartProviderOrRenewLease_AfterReplacementOnlyNewOwnerCanStart()
    {
        Assert.Equal(AsyncTaskClaimResult.Claimed,
            await _repository.TryClaimTaskAsync("media-claim-1", "old", TimeSpan.FromMinutes(-1)));
        Assert.False(await _repository.MarkProviderInvocationStartedAsync("media-claim-1", "old"));
        Assert.False(await _repository.ExtendLeaseAsync("media-claim-1", "old", TimeSpan.FromMinutes(15)));
        Assert.Equal(AsyncTaskClaimResult.Claimed,
            await _repository.TryClaimTaskAsync("media-claim-1", "new", TimeSpan.FromMinutes(15)));
        Assert.False(await _repository.MarkProviderInvocationStartedAsync("media-claim-1", "old"));
        Assert.True(await _repository.MarkProviderInvocationStartedAsync("media-claim-1", "new"));
        Assert.False(await _repository.MarkProviderInvocationStartedAsync("media-claim-1", "new"));
        Assert.False(await _repository.MarkProviderInvocationCompletedAsync("media-claim-1", "old"));
    }

    [Fact]
    public async Task PendingWithProviderMarker_CannotBeClaimedAgain()
    {
        using (var context = new ConduitDbContext(_options))
        {
            var task = await context.AsyncTasks.SingleAsync();
            task.ProviderInvocationStartedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
        Assert.Equal(AsyncTaskClaimResult.Indeterminate,
            await _repository.TryClaimTaskAsync("media-claim-1", "new", TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task TryClaimTaskAsync_ConcurrentClaims_HasSingleWinner()
    {
        var claims = await Task.WhenAll(
            _repository.TryClaimTaskAsync("media-claim-1", "worker-1", TimeSpan.FromMinutes(15)),
            _repository.TryClaimTaskAsync("media-claim-1", "worker-2", TimeSpan.FromMinutes(15)),
            _repository.TryClaimTaskAsync("media-claim-1", "worker-3", TimeSpan.FromMinutes(15)));

        Assert.Single(claims, result => result == AsyncTaskClaimResult.Claimed);
        Assert.Equal(2, claims.Count(result => result == AsyncTaskClaimResult.AlreadyClaimed));
        using var context = new ConduitDbContext(_options);
        var task = await context.AsyncTasks.AsNoTracking().SingleAsync();
        Assert.Equal(1, task.State);
        Assert.Contains(task.LeasedBy, new[] { "worker-1", "worker-2", "worker-3" });
    }

    [Fact]
    public async Task TryClaimTaskAsync_ExpiredPostProviderLease_BecomesIndeterminate()
    {
        using (var context = new ConduitDbContext(_options))
        {
            var task = await context.AsyncTasks.SingleAsync();
            task.State = 1;
            task.LeasedBy = "dead-worker";
            task.LeaseExpiryTime = DateTime.UtcNow.AddMinutes(-1);
            task.ProviderInvocationStartedAt = DateTime.UtcNow.AddMinutes(-2);
            await context.SaveChangesAsync();
        }

        var result = await _repository.TryClaimTaskAsync(
            "media-claim-1", "replacement", TimeSpan.FromMinutes(15));

        Assert.Equal(AsyncTaskClaimResult.Indeterminate, result);
        using var verification = new ConduitDbContext(_options);
        Assert.Equal(6, (await verification.AsyncTasks.AsNoTracking().SingleAsync()).State);
    }

    public void Dispose() => _database.Dispose();
}
