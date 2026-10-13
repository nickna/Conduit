using System.Net;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class BatchSpendLifecycleTests
{
    [Fact]
    public async Task ConstructorAndRepeatedDisposal_DoNotStartFlushes()
    {
        using var state = new State(interval: 1);
        state.Batch.QueueFallbackUpdate(41, 1m);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await state.Batch.DisposeAsync();
        state.Batch.Dispose();
        await state.Batch.DisposeAsync();
        state.Server.VerifyNoOtherCalls();
        state.Store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FollowerHost_PeriodicProducerRemainsLiveAndStoppedPhaseDrainsLateWriter()
    {
        using var state = new State(interval: 1);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Batch.SpendUpdatesCompleted += (_, _) => { persisted.TrySetResult(); return Task.CompletedTask; };
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            // Reverse Stop order stops the batch producer before this writer. Stopped
            // must still drain its spend after every hosted Stop has finished.
            services.AddSingleton<IHostedService>(new LateWriter(state.Batch));
            services.AddSingleton<IHostedService>(new BatchSpendLifecycleHostedService(state.Batch));
        }).Build();
        await host.StartAsync();
        Assert.False(state.Batch.IsHealthy);
        state.Batch.QueueFallbackUpdate(41, 1m);
        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.StopAsync();
        Assert.Equal(2, state.Adjustments.Count);
        Assert.Equal(new[] { -1m, -7m }, state.Adjustments.Select(item => item.Amount));
        Assert.Equal(2, state.Adjustments.Select(item => item.IdempotencyKey).Distinct().Count());
        await state.Batch.DrainAsync(CancellationToken.None);
        Assert.Equal(2, state.Adjustments.Count);
        Assert.Throws<InvalidOperationException>(() => state.Batch.QueueFallbackUpdate(41, 1m));
    }

    [Fact]
    public async Task LeadershipLossAndReacquisition_LeaveFollowerProducerAndAdmissionOpen()
    {
        using var state = new State(interval: 1);
        await state.Batch.StartLocalProducerAsync(CancellationToken.None);
        using var leader = new BatchSpendLeaderWorker(state.Batch);
        await leader.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Batch.IsHealthy);
        await leader.StopAsync(CancellationToken.None);
        Assert.False(state.Batch.IsHealthy);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Batch.SpendUpdatesCompleted += (_, _) => { persisted.TrySetResult(); return Task.CompletedTask; };
        state.Batch.QueueFallbackUpdate(41, 3m);
        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await leader.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Batch.IsHealthy);
        await leader.StopAsync(CancellationToken.None);
        await state.Batch.StopLocalProducerAsync(CancellationToken.None);
        await state.Batch.DrainAsync(CancellationToken.None);
        Assert.Equal(-3m, Assert.Single(state.Adjustments).Amount);
    }

    [Fact]
    public async Task Flush_AwaitsEverySubscriber_AndStopJoinsEarlierPendingCallback()
    {
        using var state = new State();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = 0;
        state.Batch.SpendUpdatesCompleted += async (_, _) => { entered.TrySetResult(); await release.Task; };
        state.Batch.SpendUpdatesCompleted += (_, _) => { second++; return Task.CompletedTask; };
        state.Batch.QueueFallbackUpdate(41, 1m);
        var flush = state.Batch.FlushPendingUpdatesAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = state.Batch.StopLocalProducerAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        Assert.False(flush.IsCompleted);
        release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
        Assert.Equal(0, second);
        await state.Batch.DrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Flush_InvokesAndAwaitsBothSubscribersDuringHealthyWork()
    {
        using var state = new State();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Batch.SpendUpdatesCompleted += async (_, _) => { firstEntered.TrySetResult(); await firstRelease.Task; };
        state.Batch.SpendUpdatesCompleted += async (_, _) => { secondEntered.TrySetResult(); await secondRelease.Task; };
        state.Batch.QueueFallbackUpdate(41, 1m);
        var flush = state.Batch.FlushPendingUpdatesAsync();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(flush.IsCompleted);
        firstRelease.TrySetResult();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(flush.IsCompleted);
        secondRelease.TrySetResult();
        Assert.Equal(1, await flush);
    }

    [Fact]
    public async Task DisposeBeforeStop_StillJoinsAcceptedCallbackWithoutStartingFinalFlush()
    {
        using var state = new State();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Batch.SpendUpdatesCompleted += async (_, _) => { entered.TrySetResult(); await release.Task; };
        state.Batch.QueueFallbackUpdate(41, 1m);
        var flush = state.Batch.FlushPendingUpdatesAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        state.Batch.Dispose();
        var stopping = state.Batch.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        var readsBeforeRelease = state.Server.Invocations.Count;
        release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
        Assert.Equal(readsBeforeRelease, state.Server.Invocations.Count);
        Assert.Single(state.Adjustments);
    }

    [Fact]
    public async Task CanceledStop_JoinsUncancellableRedisRead_ThenStartsNoDebitOrAcknowledgement()
    {
        using var state = new State();
        RedisKey claim = "processing_spend:group:13:claim:held-read";
        state.Server.Setup(server => server.Keys(It.IsAny<int>(), (RedisValue)"processing_spend:group:*",
            It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>())).Returns([claim]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Database.Setup(db => db.StringGetAsync(claim, It.IsAny<CommandFlags>()))
            .Callback(() => entered.TrySetResult()).Returns(release.Task);
        var flush = state.Batch.FlushPendingUpdatesAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var stopping = state.Batch.StopLocalProducerAsync(canceled.Token);
        Assert.False(stopping.IsCompleted);
        release.TrySetResult("1");
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.Batch.DrainAsync(canceled.Token));
        Assert.Empty(state.Adjustments);
        state.Database.Verify(db => db.KeyDeleteAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()), Times.Never);
        state.Database.Verify(db => db.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Never);
        var callsBeforeDispose = state.Database.Invocations.Count;
        await state.Batch.DisposeAsync();
        Assert.Equal(callsBeforeDispose, state.Database.Invocations.Count);
    }

    [Fact]
    public async Task ProducerStop_CancelsLeaderFlushNormallyBeforeWrapperStop()
    {
        using var state = new State();
        RedisKey claim = "processing_spend:group:13:claim:leader-read";
        state.Server.Setup(server => server.Keys(It.IsAny<int>(), (RedisValue)"processing_spend:group:*",
            It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>())).Returns([claim]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Database.Setup(db => db.StringGetAsync(claim, It.IsAny<CommandFlags>()))
            .Callback(() => entered.TrySetResult()).Returns(release.Task);
        using var leader = new BatchSpendLeaderWorker(state.Batch);
        await leader.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = state.Batch.StopLocalProducerAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        release.TrySetResult("1");
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        await leader.StopAsync(CancellationToken.None);
        Assert.Equal(0, state.Logger.Errors);
        Assert.Empty(state.Adjustments);
    }

    [Fact]
    public async Task FinalDrain_ClosesAdmissionAndJoinsPreviouslyAcceptedQueueWrite()
    {
        using var state = new State();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<VirtualKeyRuntimeRecord?>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Store.Setup(store => store.GetByIdAsync(41, It.IsAny<CancellationToken>()))
            .Callback(() => entered.TrySetResult()).Returns(release.Task);
        var queued = state.Batch.QueueSpendUpdateAsync(41, 3m);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var drain = state.Batch.DrainAsync(CancellationToken.None);
        Assert.False(drain.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => { _ = state.Batch.QueueSpendUpdateAsync(41, 7m); });
        release.TrySetResult(new VirtualKeyRuntimeRecord { Id = 41, VirtualKeyGroupId = 13,
            Group = new VirtualKeyGroupRuntimeRecord { Id = 13 } });
        await queued;
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        state.Database.Verify(db => db.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task FallbackUncertainCommitCancellation_ReplaysSameIdentityAndOriginalHour()
    {
        using var state = new State();
        using var canceled = new CancellationTokenSource();
        var billedAt = new DateTime(2026, 7, 25, 23, 59, 59, DateTimeKind.Utc);
        var attempts = new List<VirtualKeyBalanceAdjustment>();
        var identities = new HashSet<string>();
        state.Store.Setup(store => store.AdjustBalanceAsync(It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()))
            .Returns((VirtualKeyBalanceAdjustment item, CancellationToken _) =>
            {
                attempts.Add(item);
                var applied = identities.Add(item.IdempotencyKey!);
                if (attempts.Count == 1) canceled.Cancel();
                return Task.FromResult(new VirtualKeyBalanceAdjustmentResult(97m, 3m, applied));
            });
        state.Batch.QueueFallbackUpdate(41, 3m, billedAt);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.Batch.FlushPendingUpdatesAsync(canceled.Token));
        Assert.Equal(1, await state.Batch.FlushPendingUpdatesAsync());
        Assert.Equal(2, attempts.Count);
        Assert.Single(identities);
        Assert.All(attempts, item => Assert.Equal(billedAt.Date.AddHours(23), item.BillingWindowStartUtc));
        Assert.StartsWith("fallback-spend:", attempts[0].IdempotencyKey);
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private sealed class LateWriter(BatchSpendUpdateService batch) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            batch.QueueFallbackUpdate(41, 7m);
            return Task.CompletedTask;
        }
    }

    private sealed class State : IDisposable
    {
        public Mock<IServer> Server { get; } = new();
        public Mock<IDatabase> Database { get; } = new();
        public Mock<IVirtualKeyRuntimeStore> Store { get; } = new();
        public List<VirtualKeyBalanceAdjustment> Adjustments { get; } = [];
        public CountingLogger Logger { get; } = new();
        public BatchSpendUpdateService Batch { get; }
        public State(int interval = 3600)
        {
            var connection = new Mock<IConnectionMultiplexer>();
            connection.Setup(redis => redis.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Database.Object);
            connection.Setup(redis => redis.GetEndPoints(It.IsAny<bool>())).Returns([new DnsEndPoint("localhost", 6379)]);
            connection.Setup(redis => redis.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>())).Returns(Server.Object);
            Server.Setup(server => server.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>())).Returns([]);
            Store.Setup(store => store.GetByIdAsync(41, It.IsAny<CancellationToken>())).ReturnsAsync(new VirtualKeyRuntimeRecord
            {
                Id = 41, VirtualKeyGroupId = 13, KeyHash = "key-41", Group = new VirtualKeyGroupRuntimeRecord { Id = 13 }
            });
            Store.Setup(store => store.AdjustBalanceAsync(It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()))
                .Callback<VirtualKeyBalanceAdjustment, CancellationToken>((item, _) => Adjustments.Add(item))
                .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(90m, 10m, true));
            Batch = new BatchSpendUpdateService(Mock.Of<IServiceScopeFactory>(), new TestRedisConnectionFactory(connection.Object),
                Microsoft.Extensions.Options.Options.Create(new BatchSpendingOptions { FlushIntervalSeconds = interval }),
                Logger, Mock.Of<IBillingAlertingService>(), runtimeStore: Store.Object);
        }
        public void Dispose() => Batch.Dispose();
    }

    private sealed class CountingLogger : ILogger<BatchSpendUpdateService>
    {
        public int Errors { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors++;
        }
    }
}
