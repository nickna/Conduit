using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Core.Services;

public class LeaderElectionShutdownTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HostedShutdown_JoinsIssuedRenewalBeforeRelease_AndSkipsNewIoAfterDeadline(bool failedRenewal, bool deadline)
    {
        var entered = Signal();
        var reply = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = 0;
        var database = new Mock<IDatabase>();
        database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] _, RedisValue[] _, CommandFlags _) =>
            {
                if (script.Contains("INCR")) return Task.FromResult(Result(1));
                if (script.Contains("DEL")) { Interlocked.Increment(ref released); return Task.FromResult(Result(1)); }
                entered.TrySetResult();
                return reply.Task;
            });
        var service = Election(database.Object);
        Assert.True((await service.TryAcquireLeadershipAsync("ledger")).Acquired);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var host = new CancellationTokenSource();
        if (deadline) host.Cancel();
        var first = service.ShutdownAsync(host.Token);
        var second = service.ShutdownAsync(host.Token);
        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        Assert.Equal(0, released);
        if (failedRenewal) reply.SetException(new InvalidOperationException("renew failed after cancellation"));
        else reply.SetResult(Result(1));
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(deadline ? 0 : 1, released);
        database.Verify(value => value.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never());
        service.Dispose();
        await service.DisposeAsync();
        await service.ShutdownAsync();
        Assert.Equal(deadline ? 0 : 1, released);
        database.Verify(value => value.ScriptEvaluate(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Never());
    }

    [Fact]
    public async Task ReplacedGeneration_RemainsJoinedWhenShutdownRacesItsBlockedRenewal()
    {
        var entered = Signal();
        var reply = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var database = new Mock<IDatabase>();
        database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] _, RedisValue[] _, CommandFlags _) =>
            {
                if (script.Contains("INCR") || script.Contains("DEL")) return Task.FromResult(Result(1));
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                return reply.Task;
            });
        using var service = Election(database.Object);
        await service.TryAcquireLeadershipAsync("ledger");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = service.StartLeadershipMaintenanceAsync("ledger");
        Assert.False(replacement.IsCompleted);
        var shutdown = service.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        reply.SetResult(Result(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SelfAnnouncedLoss_RetiresWithoutSelfAwait_ThenReacquiresAndStops()
    {
        var firstRenewal = Signal();
        var acquisitions = 0;
        var database = new Mock<IDatabase>();
        database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] _, RedisValue[] _, CommandFlags _) =>
            {
                if (script.Contains("INCR")) return Task.FromResult(Result(Interlocked.Increment(ref acquisitions)));
                if (script.Contains("DEL")) return Task.FromResult(Result(1));
                firstRenewal.TrySetResult();
                return Task.FromResult(Result(0));
            });
        using var service = Election(database.Object);
        await service.TryAcquireLeadershipAsync("ledger");
        await firstRenewal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopLeadershipMaintenanceAsync("ledger").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await service.TryAcquireLeadershipAsync("ledger")).FencingToken);
        await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeBeforeStop_CancelsLocally_ThenStopJoinsWithoutRedisCleanup()
    {
        var entered = Signal();
        var reply = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = new Mock<IDatabase>();
        database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] _, RedisValue[] _, CommandFlags _) =>
            {
                if (script.Contains("INCR")) return Task.FromResult(Result(1));
                entered.TrySetResult();
                return reply.Task;
            });
        var service = Election(database.Object);
        await service.TryAcquireLeadershipAsync("ledger");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        var stop = service.ShutdownAsync();
        Assert.False(stop.IsCompleted);
        reply.SetResult(Result(1));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync();
        await service.ShutdownAsync();
        database.Verify(value => value.ScriptEvaluateAsync(It.Is<string>(script => script.Contains("DEL")),
            It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Never());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BaseHostedStop_JoinsLeaderWorkBeforeLeaseRelease_AndDisposalIsLocal(bool deadline, bool disposeFirst)
    {
        var entered = Signal();
        var work = Signal();
        var election = new Mock<ILeaderElectionService>();
        election.Setup(value => value.IsLeaderAsync("shutdown-test", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        election.Setup(value => value.TryAcquireLeadershipAsync("shutdown-test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LeadershipAcquisitionResult.Success(7));
        using var service = new BlockingLeader(election.Object, entered, work);
        await service.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var host = new CancellationTokenSource();
        if (deadline) host.Cancel();
        if (disposeFirst) service.Dispose();
        var stop = service.StopAsync(host.Token);
        Assert.Same(stop, service.StopAsync(host.Token));
        Assert.False(stop.IsCompleted);
        election.Verify(value => value.ReleaseLeadershipAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        work.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync();
        service.Dispose();
        election.Verify(value => value.ReleaseLeadershipAsync("shutdown-test", It.IsAny<CancellationToken>()),
            deadline || disposeFirst ? Times.Never() : Times.Once());
    }

    private sealed class BlockingLeader(ILeaderElectionService election, TaskCompletionSource entered, TaskCompletionSource work)
        : LeaderElectedBackgroundService(election, NullLogger.Instance, "shutdown-test")
    {
        protected override async Task ExecuteLeaderWorkAsync(CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await work.Task;
        }
    }

    private static RedisLeaderElectionService Election(IDatabase database)
    {
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database);
        return new RedisLeaderElectionService(redis.Object, NullLogger<RedisLeaderElectionService>.Instance, TimeSpan.FromMilliseconds(10));
    }

    private static RedisResult Result(long value) => RedisResult.Create((RedisValue)value);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
