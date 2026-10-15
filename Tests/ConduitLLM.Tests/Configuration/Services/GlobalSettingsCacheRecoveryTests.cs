#nullable enable

using System.Collections;
using System.Threading.Channels;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using StackExchange.Redis;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class GlobalSettingsCacheRecoveryTests
{
    [Fact]
    public async Task FailedInitialLoad_AutomaticallyRecoversStoredNondefaultValuesWithoutManualReload()
    {
        await using var fixture = new Fixture();
        fixture.Load = (attempt, _) => attempt == 1
            ? Task.FromException<IReadOnlyList<GlobalSetting>>(new InvalidOperationException("database not ready"))
            : Task.FromResult(Settings(17, false));

        await fixture.Service.StartAsync(CancellationToken.None);
        Assert.Equal(5, await fixture.Service.GetMaxAgenticIterationsAsync());
        Assert.True(await fixture.Service.GetDefaultAgenticModeEnabledAsync());
        var delay = await fixture.Clock.NextDelayAsync();
        fixture.Clock.Advance(delay);

        await AssertSettingsEventuallyAsync(fixture.Service, 17, false);
        Assert.Equal(2, fixture.ListCalls);
        Assert.Equal(0, fixture.KeyCalls);
    }

    [Fact]
    public async Task RecoveryBackoff_IncreasesToThirtySecondsAndRemainsCancellable()
    {
        await using var fixture = new Fixture();
        fixture.Load = (_, _) => Task.FromException<IReadOnlyList<GlobalSetting>>(new InvalidOperationException("database unavailable"));
        await fixture.Service.StartAsync(CancellationToken.None);

        var expectedDelays = new[] { 1, 2, 4, 8, 16, 30, 30 };
        for (var index = 0; index < expectedDelays.Length; index++)
        {
            var delay = await fixture.Clock.NextDelayAsync();
            Assert.Equal(TimeSpan.FromSeconds(expectedDelays[index]), delay);
            fixture.Clock.Advance(delay);
            var expectedCalls = index + 2;
            await EventuallyAsync(() => Task.FromResult(fixture.ListCalls == expectedCalls));
        }

        await fixture.Service.StopAsync(CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(8, fixture.ListCalls);
        Assert.Equal(0, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
    }

    [Fact]
    public async Task RedisSubscriptionFailure_DoesNotPreventInitialDatabaseLoading()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var subscriber = new Mock<ISubscriber>();
        redis.Setup(value => value.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        subscriber.Setup(value => value.SubscribeAsync(It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
        await using var fixture = new Fixture(redis.Object);

        await fixture.Service.StartAsync(CancellationToken.None);

        Assert.Equal(17, await fixture.Service.GetMaxAgenticIterationsAsync());
        Assert.False(await fixture.Service.GetDefaultAgenticModeEnabledAsync());
        Assert.Equal(1, fixture.ListCalls);
        subscriber.Verify(value => value.SubscribeAsync(It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task StartupCancellation_DuringRepositoryLoadPropagatesWithoutSchedulingRecovery()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        fixture.Load = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Settings();
        };
        var starting = fixture.Service.StartAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        await fixture.Service.StopAsync(CancellationToken.None);

        Assert.Equal(1, fixture.ListCalls);
        Assert.Equal(0, fixture.Clock.TimerCount);
        Assert.Equal(0, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
    }

    [Fact]
    public async Task StartupCancellation_RacingANoncancellationDatabaseFailure_DoesNotScheduleRecovery()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var response = new TaskCompletionSource<IReadOnlyList<GlobalSetting>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Load = (_, _) => { entered.TrySetResult(); return response.Task; };
        var starting = fixture.Service.StartAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        response.TrySetException(new InvalidOperationException("database failed after caller cancellation"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);

        Assert.Equal(0, fixture.Clock.TimerCount);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, fixture.ListCalls);
        Assert.Equal(0, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
    }

    [Fact]
    public async Task CancelledStartupSubscription_StopWaitsForItsCompletionBeforeUnsubscribing()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var subscriber = new Mock<ISubscriber>();
        var subscribed = Signal();
        redis.Setup(value => value.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        subscriber.Setup(value => value.SubscribeAsync(It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Returns(subscribed.Task);
        await using var fixture = new Fixture(redis.Object);
        using var cancellation = new CancellationTokenSource();
        var starting = fixture.Service.StartAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);

        var stopping = fixture.Service.StopAsync(CancellationToken.None);
        try
        {
            Assert.False(stopping.IsCompleted);
            subscriber.Verify(value => value.UnsubscribeAsync(It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()), Times.Never);
        }
        finally { subscribed.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        subscriber.Verify(value => value.UnsubscribeAsync(It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()), Times.Once);
        Assert.Equal(0, fixture.ListCalls);
        Assert.Equal(0, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
    }

    [Fact]
    public async Task StopDuringRecoveryDelay_CancelsTheRetryAndPreventsFutureLoads()
    {
        await using var fixture = new Fixture();
        fixture.Load = (_, _) => Task.FromException<IReadOnlyList<GlobalSetting>>(new InvalidOperationException("not ready"));
        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Clock.NextDelayAsync();

        await fixture.Service.StopAsync(CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(1, fixture.ListCalls);
        Assert.Equal(0, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
    }

    [Fact]
    public async Task StopDuringRecoveryLoad_AwaitsTheRepositoryAndRejectsALateUncooperativeSnapshot()
    {
        await using var fixture = new Fixture();
        var entered = Signal();
        var cancelled = Signal();
        var response = new TaskCompletionSource<IReadOnlyList<GlobalSetting>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Load = async (attempt, token) =>
        {
            if (attempt == 1) throw new InvalidOperationException("not ready");
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            return await response.Task; // Deliberately ignores cancellation until released.
        };
        await fixture.Service.StartAsync(CancellationToken.None);
        fixture.Clock.Advance(await fixture.Clock.NextDelayAsync());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopping = fixture.Service.StopAsync(CancellationToken.None);
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
        }
        finally { response.TrySetResult(Settings(33, false)); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
        Assert.Equal(5, await fixture.Service.GetMaxAgenticIterationsAsync());
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, fixture.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryLoads_AreSerializedWithManualReloadAndInvalidation(bool singleKey)
    {
        await using var fixture = new Fixture();
        var recoveryEntered = Signal();
        var releaseRecovery = new TaskCompletionSource<IReadOnlyList<GlobalSetting>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Load = (attempt, _) =>
        {
            if (attempt == 1) return Task.FromException<IReadOnlyList<GlobalSetting>>(new InvalidOperationException("not ready"));
            if (attempt == 2) { recoveryEntered.TrySetResult(); return releaseRecovery.Task; }
            return Task.FromResult(Settings(33, false));
        };
        fixture.ReadKey = (key, _) => Task.FromResult<GlobalSetting?>(new GlobalSetting { Key = key, Value = "33" });
        await fixture.Service.StartAsync(CancellationToken.None);
        fixture.Clock.Advance(await fixture.Clock.NextDelayAsync());
        await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var manual = singleKey ? fixture.Service.InvalidateSettingAsync("Agentic.MaxIterations") : fixture.Service.ReloadAllSettingsAsync();
        try
        {
            Assert.False(manual.IsCompleted);
            Assert.Equal(2, fixture.ListCalls);
            Assert.Equal(0, fixture.KeyCalls);
        }
        finally { releaseRecovery.TrySetResult(Settings(17, false)); }
        await manual.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(33, await fixture.Service.GetMaxAgenticIterationsAsync());
        Assert.False(await fixture.Service.GetDefaultAgenticModeEnabledAsync());
        Assert.Equal(1, fixture.MaximumActiveCalls);
        Assert.Equal(singleKey ? 2 : 3, fixture.ListCalls);
        Assert.Equal(singleKey ? 1 : 0, fixture.KeyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringManualLoading_DrainsTheOperationBeforeClearingAndRejectsLateWrites(bool singleKey)
    {
        await using var fixture = new Fixture();
        await fixture.Service.StartAsync(CancellationToken.None);
        var entered = Signal();
        var cancelled = Signal();
        var release = Signal();
        fixture.Load = async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return Settings(33, false);
        };
        fixture.ReadKey = async (key, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return new GlobalSetting { Key = key, Value = "33" };
        };
        var manual = singleKey ? fixture.Service.InvalidateSettingAsync("Agentic.MaxIterations") : fixture.Service.ReloadAllSettingsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopping = fixture.Service.StopAsync(CancellationToken.None);
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var outcome = await Record.ExceptionAsync(() => manual);
        Assert.True(outcome is null or OperationCanceledException);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
        Assert.Equal(5, await fixture.Service.GetMaxAgenticIterationsAsync());
        await fixture.Service.ReloadAllSettingsAsync();
        await fixture.Service.InvalidateSettingAsync("Agentic.MaxIterations");
        Assert.Equal(singleKey ? 1 : 2, fixture.ListCalls);
        Assert.Equal(singleKey ? 1 : 0, fixture.KeyCalls);
    }

    [Fact]
    public async Task Reload_PublishesTheCompleteReplacementWithoutExposingPartialOrEmptySnapshots()
    {
        await using var fixture = new Fixture();
        fixture.Load = (_, _) => Task.FromResult<IReadOnlyList<GlobalSetting>>(
            [new() { Key = "oldA", Value = "a" }, new() { Key = "oldB", Value = "b" }]);
        await fixture.Service.StartAsync(CancellationToken.None);
        using var release = new ManualResetEventSlim();
        var staged = Signal();
        fixture.Load = (_, _) => Task.FromResult<IReadOnlyList<GlobalSetting>>(new StagedSettings(
            [new() { Key = "newA", Value = "c" }, new() { Key = "newB", Value = "d" }], () =>
            {
                staged.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release staged settings.");
            }));
        var reloading = Task.Run(fixture.Service.ReloadAllSettingsAsync);
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "oldA", "oldB" }, (await fixture.Service.GetCacheStatsAsync()).CachedKeys);
            Assert.Equal("a", await fixture.Service.GetSettingValueAsync("oldA"));
            Assert.Null(await fixture.Service.GetSettingValueAsync("newA"));
        }
        finally { release.Set(); }
        await reloading.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "newA", "newB" }, (await fixture.Service.GetCacheStatsAsync()).CachedKeys);
        Assert.Equal("c", await fixture.Service.GetSettingValueAsync("newA"));
        Assert.Null(await fixture.Service.GetSettingValueAsync("oldA"));
    }

    [Fact]
    public async Task FailedPartialFullLoad_KeepsThePreviousSnapshotAndSuccessfulLoadTime()
    {
        await using var fixture = new Fixture();
        await fixture.Service.StartAsync(CancellationToken.None);
        var original = await fixture.Service.GetCacheStatsAsync();
        fixture.Load = (_, _) => Task.FromResult<IReadOnlyList<GlobalSetting>>(new StagedSettings(
            [new() { Key = "Agentic.MaxIterations", Value = "33" }, new() { Key = "partial", Value = "bad" }],
            () => throw new InvalidOperationException("incomplete snapshot")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReloadAllSettingsAsync());

        Assert.Equal(17, await fixture.Service.GetMaxAgenticIterationsAsync());
        Assert.False(await fixture.Service.GetDefaultAgenticModeEnabledAsync());
        var current = await fixture.Service.GetCacheStatsAsync();
        Assert.Equal(original.CachedKeys, current.CachedKeys);
        Assert.Equal(original.LastResetTime, current.LastResetTime);
        Assert.Null(await fixture.Service.GetSettingValueAsync("partial"));
    }

    [Fact]
    public async Task FailedSingleSettingRefresh_KeepsItsPreviousValue()
    {
        await using var fixture = new Fixture();
        await fixture.Service.StartAsync(CancellationToken.None);
        fixture.ReadKey = (_, _) => Task.FromException<GlobalSetting?>(new InvalidOperationException("read failed"));

        await fixture.Service.InvalidateSettingAsync("Agentic.MaxIterations");

        Assert.Equal(17, await fixture.Service.GetMaxAgenticIterationsAsync());
        Assert.False(await fixture.Service.GetDefaultAgenticModeEnabledAsync());
        Assert.Equal(2, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
    }

    [Fact]
    public async Task FailedPartialRecoverySnapshot_IsNotPublishedAndTheNextRetryRecovers()
    {
        await using var fixture = new Fixture();
        fixture.Load = (attempt, _) => attempt switch
        {
            1 => Task.FromException<IReadOnlyList<GlobalSetting>>(new InvalidOperationException("not ready")),
            2 => Task.FromResult<IReadOnlyList<GlobalSetting>>(new StagedSettings(
                [new() { Key = "Agentic.MaxIterations", Value = "33" }, new() { Key = "partial", Value = "bad" }],
                () => throw new InvalidOperationException("incomplete snapshot"))),
            _ => Task.FromResult(Settings(17, false))
        };
        await fixture.Service.StartAsync(CancellationToken.None);
        fixture.Clock.Advance(await fixture.Clock.NextDelayAsync());
        var secondDelay = await fixture.Clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromSeconds(2), secondDelay);
        Assert.Equal(0, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
        Assert.Equal(5, await fixture.Service.GetMaxAgenticIterationsAsync());

        fixture.Clock.Advance(secondDelay);

        await AssertSettingsEventuallyAsync(fixture.Service, 17, false);
        Assert.Equal(3, fixture.ListCalls);
    }

    private static IReadOnlyList<GlobalSetting> Settings(int maximum = 17, bool enabled = false) =>
        [new() { Key = "Agentic.MaxIterations", Value = maximum.ToString() },
         new() { Key = "Agentic.DefaultEnabled", Value = enabled.ToString() }];

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task AssertSettingsEventuallyAsync(GlobalSettingsCacheService service, int maximum, bool enabled) =>
        EventuallyAsync(async () => await service.GetMaxAgenticIterationsAsync() == maximum &&
            await service.GetDefaultAgenticModeEnabledAsync() == enabled);

    private static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate()) await Task.Delay(1, timeout.Token);
    }

    private sealed class ObservedClock : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();
        private int _timerCount;
        public int TimerCount => Volatile.Read(ref _timerCount);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timerCount);
            _delays.Writer.TryWrite(dueTime);
            return timer;
        }
        public Task<TimeSpan> NextDelayAsync() => _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private int _listCalls;
        private int _keyCalls;
        private int _activeCalls;
        private int _maximumActiveCalls;
        public int ListCalls => Volatile.Read(ref _listCalls);
        public int KeyCalls => Volatile.Read(ref _keyCalls);
        public int MaximumActiveCalls => Volatile.Read(ref _maximumActiveCalls);
        public ObservedClock Clock { get; } = new();
        public GlobalSettingsCacheService Service { get; }
        public Func<int, CancellationToken, Task<IReadOnlyList<GlobalSetting>>> Load { get; set; } = (_, _) => Task.FromResult(Settings());
        public Func<string, CancellationToken, Task<GlobalSetting?>> ReadKey { get; set; } =
            (key, _) => Task.FromResult(Settings().SingleOrDefault(setting => setting.Key == key));

        public Fixture(IConnectionMultiplexer? redis = null)
        {
            var repository = new Mock<IGlobalSettingRepository>();
            repository.Setup(value => value.ListAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken token) => LoadAsync(token));
            repository.Setup(value => value.GetByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, CancellationToken token) => ReadAsync(key, token));
            var services = new ServiceCollection();
            services.AddSingleton(repository.Object);
            _provider = services.BuildServiceProvider();
            Service = new GlobalSettingsCacheService(_provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<GlobalSettingsCacheService>.Instance, redis, Clock);
        }

        private async Task<IReadOnlyList<GlobalSetting>> LoadAsync(CancellationToken token)
        {
            var attempt = Interlocked.Increment(ref _listCalls);
            Enter();
            try { return await Load(attempt, token); }
            finally { Interlocked.Decrement(ref _activeCalls); }
        }

        private async Task<GlobalSetting?> ReadAsync(string key, CancellationToken token)
        {
            Interlocked.Increment(ref _keyCalls);
            Enter();
            try { return await ReadKey(key, token); }
            finally { Interlocked.Decrement(ref _activeCalls); }
        }

        private void Enter()
        {
            var active = Interlocked.Increment(ref _activeCalls);
            int observed;
            do { observed = _maximumActiveCalls; }
            while (active > observed && Interlocked.CompareExchange(ref _maximumActiveCalls, active, observed) != observed);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { await _provider.DisposeAsync(); }
        }
    }

    private sealed class StagedSettings(IReadOnlyList<GlobalSetting> values, Action beforeSecond) : IReadOnlyList<GlobalSetting>
    {
        public int Count => values.Count;
        public GlobalSetting this[int index] { get { if (index == 1) beforeSecond(); return values[index]; } }
        public IEnumerator<GlobalSetting> GetEnumerator()
        {
            for (var index = 0; index < values.Count; index++) yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
