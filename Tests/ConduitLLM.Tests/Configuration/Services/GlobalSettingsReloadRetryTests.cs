#nullable enable

using System.Reflection;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Moq;

using StackExchange.Redis;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class GlobalSettingsReloadRetryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalReload_FailedRequestCanRetryWithTheSameIdAndSuccessfulDuplicatesDoNotReload(bool cancel)
    {
        await using var fixture = new Fixture();
        await fixture.Service.StartAsync(CancellationToken.None);
        var original = await fixture.Service.GetCacheStatsAsync();
        var failure = new InvalidOperationException("first reload could not read the database");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        fixture.Load = (attempt, _) => attempt == 2
            ? cancel
                ? Task.FromCanceled<IReadOnlyList<GlobalSetting>>(cancellation.Token)
                : Task.FromException<IReadOnlyList<GlobalSetting>>(failure)
            : Task.FromResult(Settings(29));

        var error = await Record.ExceptionAsync(() => fixture.Service.PublishReloadAsync("local-retry"));

        if (cancel)
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        else
            Assert.Same(failure, error);
        Assert.Equal(2, fixture.ListCalls);
        Assert.Equal(17, await fixture.Service.GetMaxAgenticIterationsAsync());
        Assert.Equal(original.LastResetTime, (await fixture.Service.GetCacheStatsAsync()).LastResetTime);
        AssertRequestNotRetained(fixture.Service, "local-retry");

        await fixture.Service.PublishReloadAsync("local-retry").WaitAsync(TestTimeout);

        Assert.Equal(3, fixture.ListCalls);
        Assert.Equal(29, await fixture.Service.GetMaxAgenticIterationsAsync());
        fixture.Load = (_, _) => Task.FromResult(Settings(41));
        await fixture.Service.PublishReloadAsync("local-retry").WaitAsync(TestTimeout);
        await fixture.Service.PublishReloadAsync("local-retry").WaitAsync(TestTimeout);
        Assert.Equal(3, fixture.ListCalls);
        Assert.Equal(29, await fixture.Service.GetMaxAgenticIterationsAsync());
    }

    [Fact]
    public async Task LocalConcurrentDuplicates_JoinTheFailedAttemptAndTheSameIdCanThenRetry()
    {
        await using var fixture = new Fixture();
        await fixture.Service.StartAsync(CancellationToken.None);
        var entered = Signal();
        var response = new TaskCompletionSource<IReadOnlyList<GlobalSetting>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("shared reload failed");
        fixture.Load = (_, _) =>
        {
            entered.TrySetResult();
            return response.Task;
        };
        var first = fixture.Service.PublishReloadAsync("concurrent-retry");
        await entered.Task.WaitAsync(TestTimeout);
        var duplicate = fixture.Service.PublishReloadAsync("concurrent-retry");
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(duplicate.IsCompleted);
            Assert.Equal(2, fixture.ListCalls);
        }
        finally
        {
            response.TrySetException(failure);
        }

        var firstError = await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TestTimeout));
        var duplicateError = await Assert.ThrowsAsync<InvalidOperationException>(() => duplicate.WaitAsync(TestTimeout));

        Assert.Same(failure, firstError);
        Assert.Same(firstError, duplicateError);
        Assert.Equal(2, fixture.ListCalls);
        AssertRequestNotRetained(fixture.Service, "concurrent-retry");
        fixture.Load = (_, _) => Task.FromResult(Settings(33));
        await fixture.Service.PublishReloadAsync("concurrent-retry").WaitAsync(TestTimeout);
        await fixture.Service.PublishReloadAsync("concurrent-retry").WaitAsync(TestTimeout);
        Assert.Equal(3, fixture.ListCalls);
        Assert.Equal(33, await fixture.Service.GetMaxAgenticIterationsAsync());
    }

    [Fact]
    public async Task SubscribedRedisCallback_LogsFailureThenRetriesTheSameIdAndSuppressesSuccessfulDuplicates()
    {
        await using var fixture = new Fixture(subscribe: true);
        await fixture.Service.StartAsync(CancellationToken.None);
        var failure = new InvalidOperationException("subscriber database reload failed");
        fixture.Load = (_, _) => Task.FromException<IReadOnlyList<GlobalSetting>>(failure);

        fixture.Deliver("subscriber-retry");
        var logged = await fixture.Logger.ReloadFailure.Task.WaitAsync(TestTimeout);

        Assert.Same(failure, logged.Exception);
        Assert.Contains("subscriber-retry", logged.Message);
        Assert.Equal(2, fixture.ListCalls);
        Assert.Equal(17, await fixture.Service.GetMaxAgenticIterationsAsync());
        AssertRequestNotRetained(fixture.Service, "subscriber-retry");

        var entered = Signal();
        var response = new TaskCompletionSource<IReadOnlyList<GlobalSetting>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Load = (_, _) =>
        {
            entered.TrySetResult();
            return response.Task;
        };
        fixture.Deliver("subscriber-retry");
        await entered.Task.WaitAsync(TestTimeout);
        // Redis callbacks are fire-and-forget. Join their actual coordinator task
        // while the repository is gated so completion does not depend on a delay.
        var retry = ProcessReloadRequestAsync(fixture.Service, "subscriber-retry");
        response.TrySetResult(Settings(37));
        await retry.WaitAsync(TestTimeout);
        Assert.Equal(3, fixture.ListCalls);
        Assert.Equal(37, await fixture.Service.GetMaxAgenticIterationsAsync());

        fixture.Load = (_, _) => Task.FromResult(Settings(41));
        fixture.Deliver("subscriber-retry");
        await HandleReloadBroadcastAsync(fixture.Service, "subscriber-retry").WaitAsync(TestTimeout);
        Assert.Equal(3, fixture.ListCalls);
        Assert.Equal(37, await fixture.Service.GetMaxAgenticIterationsAsync());
        fixture.Subscriber!.Verify(value => value.PublishAsync(It.IsAny<RedisChannel>(),
            It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task ShutdownDuringReload_CancelsAndReleasesRequestWithoutAcknowledgingSkippedReloads()
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
            // Model a repository that returns a late result despite cancellation.
            await release.Task;
            return Settings(43);
        };
        var reload = fixture.Service.PublishReloadAsync("shutdown-retry");
        await entered.Task.WaitAsync(TestTimeout);
        var stopping = fixture.Service.StopAsync(CancellationToken.None);
        try
        {
            await cancelled.Task.WaitAsync(TestTimeout);
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload.WaitAsync(TestTimeout));
        await stopping.WaitAsync(TestTimeout);

        Assert.Equal(2, fixture.ListCalls);
        Assert.Equal(0, (await fixture.Service.GetCacheStatsAsync()).EntryCount);
        AssertRequestNotRetained(fixture.Service, "shutdown-retry");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessReloadRequestAsync(fixture.Service, "shutdown-retry").WaitAsync(TestTimeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessReloadRequestAsync(fixture.Service, "arrived-after-stop").WaitAsync(TestTimeout));
        AssertRequestNotRetained(fixture.Service, "arrived-after-stop");

        // The public manual API keeps its established shutdown no-op behavior.
        await fixture.Service.ReloadAllSettingsAsync().WaitAsync(TestTimeout);
        await fixture.Service.PublishReloadAsync("public-after-stop").WaitAsync(TestTimeout);
        Assert.Equal(2, fixture.ListCalls);
        AssertRequestNotRetained(fixture.Service, "public-after-stop");
    }

    private static IReadOnlyList<GlobalSetting> Settings(int maximum = 17) =>
        [new() { Key = "Agentic.MaxIterations", Value = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture) }];

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task ProcessReloadRequestAsync(GlobalSettingsCacheService service, string requestId) =>
        InvokeReloadMethod(service, "ProcessReloadRequestAsync", requestId);

    private static Task HandleReloadBroadcastAsync(GlobalSettingsCacheService service, string requestId) =>
        InvokeReloadMethod(service, "HandleReloadBroadcastAsync", requestId);

    private static Task InvokeReloadMethod(GlobalSettingsCacheService service, string methodName, string requestId)
    {
        var method = typeof(GlobalSettingsCacheService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(service, [requestId]));
    }

    private static void AssertRequestNotRetained(GlobalSettingsCacheService service, string requestId)
    {
        object Field(string name) => typeof(GlobalSettingsCacheService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        lock (Field("_reloadGate"))
        {
            Assert.DoesNotContain(requestId, (HashSet<string>)Field("_processedReloads"));
            Assert.False(((Dictionary<string, TaskCompletionSource>)Field("_reloadsInFlight")).ContainsKey(requestId));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private Action<RedisChannel, RedisValue>? _callback;
        private int _listCalls;

        public GlobalSettingsCacheService Service { get; }
        public RecordingLogger Logger { get; } = new();
        public Mock<ISubscriber>? Subscriber { get; }
        public int ListCalls => Volatile.Read(ref _listCalls);
        public Func<int, CancellationToken, Task<IReadOnlyList<GlobalSetting>>> Load { get; set; } =
            (_, _) => Task.FromResult(Settings());

        public Fixture(bool subscribe = false)
        {
            var repository = new Mock<IGlobalSettingRepository>();
            repository.Setup(value => value.ListAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken token) => Load(Interlocked.Increment(ref _listCalls), token));
            var services = new ServiceCollection();
            services.AddSingleton(repository.Object);
            _provider = services.BuildServiceProvider();
            IConnectionMultiplexer? redis = null;
            if (subscribe)
            {
                Subscriber = new Mock<ISubscriber>();
                Subscriber.Setup(value => value.SubscribeAsync(It.IsAny<RedisChannel>(),
                        It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
                    .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((channel, callback, _) =>
                    {
                        Assert.Equal("conduit:global-settings:reload", channel.ToString());
                        _callback = callback;
                    })
                    .Returns(Task.CompletedTask);
                Subscriber.Setup(value => value.UnsubscribeAsync(It.IsAny<RedisChannel>(),
                        It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
                    .Returns(Task.CompletedTask);
                var multiplexer = new Mock<IConnectionMultiplexer>();
                multiplexer.Setup(value => value.GetSubscriber(It.IsAny<object>())).Returns(Subscriber.Object);
                redis = multiplexer.Object;
            }

            Service = new GlobalSettingsCacheService(_provider.GetRequiredService<IServiceScopeFactory>(), Logger, redis);
        }

        public void Deliver(string requestId)
        {
            Assert.NotNull(_callback);
            _callback(RedisChannel.Literal("conduit:global-settings:reload"), requestId);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout); }
            finally { await _provider.DisposeAsync(); }
        }
    }

    private sealed record LoggedFailure(string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<GlobalSettingsCacheService>
    {
        public TaskCompletionSource<LoggedFailure> ReloadFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Error && message.Contains("Failed to process cluster-wide global settings reload", StringComparison.Ordinal))
                ReloadFailure.TrySetResult(new LoggedFailure(message, exception));
        }
    }
}
