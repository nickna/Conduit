using System.Threading.Channels;

using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Events;
using ConduitLLM.Gateway.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

namespace ConduitLLM.Tests.Gateway.Services;

public sealed class GatewayHeartbeatPublisherTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DeferredStartup_WaitsForSchemaAndCompletedRuntimeStart_ThenPublishesImmediatelyAndPeriodically()
    {
        await using var fixture = new Fixture(deferred: true);
        await fixture.StartAsync();
        await fixture.PreflightEntered.Task.WaitAsync(Deadline);
        Assert.False(fixture.Publisher.StartupWait!.IsCompleted);
        Assert.Equal(0, fixture.Runtime.Starts);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.AssertNoPublishing();

        fixture.SchemaReady.SetResult(true);
        await fixture.Runtime.StartEntered.Task.WaitAsync(Deadline);
        Assert.False(fixture.Startup!.IsReady);
        Assert.False(fixture.Publisher.StartupWait.IsCompleted);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.AssertNoPublishing();

        fixture.Runtime.FinishStart.SetResult();
        await fixture.GatedService!.ExecuteTask!.WaitAsync(Deadline);
        var initial = await fixture.ReadHeartbeatAsync();
        await fixture.Clock.TimerCreated.Task.WaitAsync(Deadline);
        Assert.True(fixture.Startup.IsReady);
        Assert.Equal("healthy", initial.Status);
        Assert.Equal(Interval.TotalSeconds, initial.IntervalSeconds);
        Assert.Equal(1, fixture.PublishCount);

        fixture.Clock.Advance(Interval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, fixture.PublishCount);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await fixture.ReadHeartbeatAsync();
        Assert.Equal(2, fixture.PublishCount);
        fixture.Clock.Advance(Interval);
        await fixture.ReadHeartbeatAsync();
        Assert.Equal(3, fixture.PublishCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopWhileWaiting_ExitsWithoutPublishingOrCancellingSharedStartup(bool duringRuntimeStart)
    {
        await using var fixture = new Fixture(deferred: true);
        await fixture.StartAsync();
        await fixture.PreflightEntered.Task.WaitAsync(Deadline);
        if (duringRuntimeStart)
        {
            fixture.SchemaReady.SetResult(true);
            await fixture.Runtime.StartEntered.Task.WaitAsync(Deadline);
        }
        var otherWaiter = fixture.Startup!.WaitUntilReadyAsync(CancellationToken.None);
        Assert.False(otherWaiter.IsCompleted);

        await fixture.Publisher.StopAsync(CancellationToken.None).WaitAsync(Deadline);
        await fixture.Publisher.ExecuteTask!.WaitAsync(Deadline);
        fixture.AssertNoPublishing();
        Assert.False(otherWaiter.IsCompleted);

        // The real runtime deliberately ignores cancellation while its start is pending.
        // Cancelling the heartbeat's wait must leave the shared readiness signal usable.
        fixture.SchemaReady.TrySetResult(true);
        fixture.Runtime.FinishStart.TrySetResult();
        await fixture.GatedService!.ExecuteTask!.WaitAsync(Deadline);
        Assert.True(await otherWaiter.WaitAsync(Deadline));
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.AssertNoPublishing();
        Assert.False(fixture.Publisher.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task FailedRuntimeStartup_EndsHeartbeatWithoutPublishingOrFaulting()
    {
        await using var fixture = new Fixture(deferred: true);
        fixture.Runtime.Failure = new InvalidOperationException("runtime startup failed");
        await fixture.StartAsync();
        fixture.SchemaReady.SetResult(true);
        await fixture.Runtime.StartEntered.Task.WaitAsync(Deadline);
        Assert.False(fixture.Publisher.StartupWait!.IsCompleted);

        fixture.Runtime.FinishStart.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.GatedService!.ExecuteTask!.WaitAsync(Deadline));
        await fixture.Publisher.ExecuteTask!.WaitAsync(Deadline);

        fixture.AssertNoPublishing();
        Assert.False(fixture.Startup!.IsReady);
        Assert.False(fixture.Publisher.ExecuteTask.IsFaulted);
        fixture.Lifetime.Verify(value => value.StopApplication(), Times.Once);
    }

    [Fact]
    public async Task TerminalStartupBeforePublisherStarts_ExitsWithoutCheckingHealthOrPublishing()
    {
        await using var fixture = new Fixture(deferred: true);
        await fixture.GatedService!.StopAsync(CancellationToken.None).WaitAsync(Deadline);

        await fixture.Publisher.StartAsync(CancellationToken.None);
        await fixture.Publisher.ExecuteTask!.WaitAsync(Deadline);

        Assert.False(await fixture.Publisher.StartupWait!);
        fixture.AssertNoPublishing();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateLifecycle_PublishesWithoutStartupDelay(bool registerImmediateState)
    {
        await using var fixture = new Fixture(deferred: false, registerImmediateState);
        await fixture.StartAsync();
        var heartbeat = await fixture.ReadHeartbeatAsync();
        await fixture.Clock.TimerCreated.Task.WaitAsync(Deadline);

        Assert.True(await fixture.Publisher.StartupWait!);
        Assert.Equal(1, fixture.PublishCount);
        Assert.Equal("healthy", heartbeat.Status);
        Assert.False(string.IsNullOrWhiteSpace(heartbeat.InstanceId));
        Assert.False(string.IsNullOrWhiteSpace(heartbeat.Version));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Clock : FakeTimeProvider
    {
        internal TaskCompletionSource TimerCreated { get; } = Signal();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (period == Interval) TimerCreated.TrySetResult();
            return timer;
        }
    }

    private sealed class ObservedPublisher : GatewayHeartbeatPublisher
    {
        internal TaskCompletionSource WaitEntered { get; } = Signal();
        internal Task<bool>? StartupWait { get; private set; }

        internal ObservedPublisher(IServiceProvider provider, HealthCheckService healthChecks,
            IConfiguration configuration, WolverineRuntimeStartupState? startup, TimeProvider clock)
            : base(provider, healthChecks, configuration,
                NullLogger<GatewayHeartbeatPublisher>.Instance, startup, clock) { }

        protected override Task<bool> WaitForStartupAsync(CancellationToken cancellationToken)
        {
            StartupWait = base.WaitForStartupAsync(cancellationToken);
            WaitEntered.TrySetResult();
            return StartupWait;
        }
    }

    private sealed class Runtime : IHostedService
    {
        internal int Starts;
        internal Exception? Failure;
        internal TaskCompletionSource StartEntered { get; } = Signal();
        internal TaskCompletionSource FinishStart { get; } = Signal();

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            StartEntered.TrySetResult();
            await FinishStart.Task;
            if (Failure is { } failure) throw failure;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly Channel<GatewayHeartbeat> _heartbeats = Channel.CreateUnbounded<GatewayHeartbeat>();
        private int _publishCount;
        private int _eventBusResolutions;
        private int _healthCheckCount;

        internal Clock Clock { get; } = new();
        internal Runtime Runtime { get; } = new();
        internal Mock<IHostApplicationLifetime> Lifetime { get; } = new();
        internal WolverineRuntimeStartupState? Startup { get; }
        internal SchemaGatedWolverineHostedService? GatedService { get; }
        internal ObservedPublisher Publisher { get; }
        internal TaskCompletionSource PreflightEntered { get; } = Signal();
        internal TaskCompletionSource<bool> SchemaReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int PublishCount => Volatile.Read(ref _publishCount);

        internal Fixture(bool deferred, bool registerImmediateState = false)
        {
            if (deferred || registerImmediateState) Startup = new WolverineRuntimeStartupState();
            if (deferred)
            {
                GatedService = new SchemaGatedWolverineHostedService(Runtime, token =>
                {
                    PreflightEntered.TrySetResult();
                    return SchemaReady.Task.WaitAsync(token);
                }, Startup!, Lifetime.Object, NullLogger<SchemaGatedWolverineHostedService>.Instance);
            }

            var bus = new Mock<IEventBus>();
            bus.Setup(value => value.PublishAsync(It.IsAny<GatewayHeartbeat>(), It.IsAny<CancellationToken>()))
                .Returns((GatewayHeartbeat heartbeat, CancellationToken _) =>
                {
                    Interlocked.Increment(ref _publishCount);
                    _heartbeats.Writer.TryWrite(heartbeat);
                    return Task.CompletedTask;
                });
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHealthChecks().AddCheck("ready", () =>
            {
                Interlocked.Increment(ref _healthCheckCount);
                return HealthCheckResult.Healthy();
            }, tags: ["ready"]);
            services.AddScoped<IEventBus>(_ =>
            {
                Interlocked.Increment(ref _eventBusResolutions);
                return bus.Object;
            });
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GatewayHeartbeat:IntervalSeconds"] = ((int)Interval.TotalSeconds).ToString()
            }).Build();
            Publisher = new ObservedPublisher(_provider, _provider.GetRequiredService<HealthCheckService>(),
                configuration, Startup, Clock);
        }

        internal async Task StartAsync()
        {
            if (GatedService is { } gated) await gated.StartAsync(CancellationToken.None);
            await Publisher.StartAsync(CancellationToken.None);
            await Publisher.WaitEntered.Task.WaitAsync(Deadline);
        }

        internal Task<GatewayHeartbeat> ReadHeartbeatAsync() => _heartbeats.Reader.ReadAsync().AsTask().WaitAsync(Deadline);

        internal void AssertNoPublishing()
        {
            Assert.Equal(0, PublishCount);
            Assert.Equal(0, Volatile.Read(ref _eventBusResolutions));
            Assert.Equal(0, Volatile.Read(ref _healthCheckCount));
            Assert.False(Clock.TimerCreated.Task.IsCompleted);
        }

        public async ValueTask DisposeAsync()
        {
            SchemaReady.TrySetResult(true);
            Runtime.FinishStart.TrySetResult();
            await Publisher.StopAsync(CancellationToken.None).WaitAsync(Deadline);
            Publisher.Dispose();
            if (GatedService is { } gated)
            {
                await gated.StopAsync(CancellationToken.None).WaitAsync(Deadline);
                gated.Dispose();
            }
            await _provider.DisposeAsync();
        }
    }
}
