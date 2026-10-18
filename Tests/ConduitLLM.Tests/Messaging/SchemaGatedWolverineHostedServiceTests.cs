using ConduitLLM.Configuration.Messaging.Wolverine;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Npgsql;

using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace ConduitLLM.Tests.Messaging;

public sealed class SchemaGatedWolverineHostedServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HealthCheckActivation_ResolvesStartupStateAndKeepsImmediateLifecycleCompatible(bool deferred, bool explicitThreshold)
    {
        var state = new WolverineRuntimeStartupState();
        using var service = deferred ? Create(new Runtime(), _ => Task.FromResult(false), state) : null;
        var store = new Mock<IMessageStore>();
        var admin = new Mock<IMessageStoreAdmin>();
        store.SetupGet(value => value.Admin).Returns(admin.Object);
        admin.Setup(value => value.FetchCountsAsync()).ReturnsAsync(new PersistedCounts());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store.Object);
        if (deferred) services.AddSingleton(state);
        services.AddHealthChecks().AddTypeActivatedCheck<WolverineBusHealthCheck>(
            "bus", HealthStatus.Unhealthy, new[] { "ready" },
            args: explicitThreshold ? new object[] { 1 } : Array.Empty<object>());
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(deferred ? HealthStatus.Unhealthy : HealthStatus.Healthy, result.Status);
        admin.Verify(value => value.FetchCountsAsync(), deferred ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task WaitingAndStartingRemainUnhealthy_UntilRuntimeStartActuallyCompletes()
    {
        var provisioned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime { PauseStart = true };
        var state = new WolverineRuntimeStartupState();
        using var service = Create(runtime, _ => provisioned.Task, state);
        var store = new Mock<IMessageStore>();
        var admin = new Mock<IMessageStoreAdmin>();
        store.SetupGet(value => value.Admin).Returns(admin.Object);
        admin.Setup(value => value.FetchCountsAsync()).ReturnsAsync(new PersistedCounts());
        var check = new WolverineBusHealthCheck(store.Object, NullLogger<WolverineBusHealthCheck>.Instance, 1, state);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(0, runtime.Starts);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        provisioned.SetResult(true);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(state.IsReady);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        admin.Verify(value => value.FetchCountsAsync(), Times.Never);
        runtime.FinishStart.SetResult();
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.IsReady);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.False(state.IsReady);
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(1, runtime.Stops);
    }

    [Fact]
    public async Task CancelingReadinessWait_DoesNotCancelRuntimeStartupOrOtherWaiters()
    {
        var runtime = new Runtime { PauseStart = true };
        var state = new WolverineRuntimeStartupState();
        using var service = Create(runtime, _ => Task.FromResult(true), state);
        await service.StartAsync(CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        var canceledWait = state.WaitUntilReadyAsync(caller.Token);
        var survivingWait = state.WaitUntilReadyAsync(CancellationToken.None);
        Assert.False(canceledWait.IsCompleted);
        Assert.False(survivingWait.IsCompleted);

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        Assert.False(survivingWait.IsCompleted);
        Assert.False(state.IsReady);

        runtime.FinishStart.SetResult();
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await survivingWait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await state.WaitUntilReadyAsync(CancellationToken.None));
        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownBeforeRuntimeStartup_CompletesReadinessWaitAsUnavailable(bool waitBeforeStop)
    {
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime();
        var state = new WolverineRuntimeStartupState();
        using var service = Create(runtime, async token =>
        {
            polling.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return false;
        }, state);
        await service.StartAsync(CancellationToken.None);
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var readiness = waitBeforeStop ? state.WaitUntilReadyAsync(CancellationToken.None) : null;
        if (readiness is not null) Assert.False(readiness.IsCompleted);

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(await (readiness ?? state.WaitUntilReadyAsync(CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(service.ExecuteTask!.IsFaulted);
        Assert.False(state.IsReady);
        Assert.Equal(0, runtime.Starts);
        Assert.Equal(0, runtime.Stops);
    }

    [Fact]
    public async Task ShutdownDuringUncancellableStartup_WaitsBeforeStoppingAndCannotBecomeReady()
    {
        var runtime = new Runtime { PauseStart = true };
        var state = new WolverineRuntimeStartupState();
        using var service = Create(runtime, _ => Task.FromResult(true), state);
        await service.StartAsync(CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var readiness = state.WaitUntilReadyAsync(CancellationToken.None);
        Assert.False(readiness.IsCompleted);
        using var shutdownDeadline = new CancellationTokenSource();
        shutdownDeadline.Cancel();
        var stopping = service.StopAsync(shutdownDeadline.Token);
        Assert.Equal(0, runtime.Stops);
        Assert.False(stopping.IsCompleted);
        Assert.False(await readiness.WaitAsync(TimeSpan.FromSeconds(5)));
        runtime.FinishStart.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runtime.Stops);
        Assert.False(state.IsReady);
        Assert.False(await state.WaitUntilReadyAsync(CancellationToken.None));
        Assert.False(runtime.StoppedBeforeStartFinished);
    }

    [Fact]
    public async Task ShutdownDuringWrappedCancellationInPreflight_DoesNotStartOrFailTheApplication()
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime();
        using var service = Create(runtime, async token =>
        {
            polling.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException exception) { throw new InvalidOperationException("storage assertion", exception); }
            return false;
        }, new WolverineRuntimeStartupState(), lifetime);
        await service.StartAsync(CancellationToken.None);
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, runtime.Starts);
        Assert.Equal(0, runtime.Stops);
        Assert.False(service.ExecuteTask!.IsFaulted);
        lifetime.Verify(value => value.StopApplication(), Times.Never);
    }

    [Fact]
    public async Task TransientWrappedStorageFailure_IsRetriedBeforeOneSuccessfulStart()
    {
        var calls = 0;
        var runtime = new Runtime();
        var state = new WolverineRuntimeStartupState();
        using var service = Create(runtime, _ =>
        {
            if (++calls == 1) throw new InvalidOperationException("storage assertion", Postgres("42P01"));
            return Task.FromResult(true);
        }, state);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        Assert.Equal(1, runtime.Starts);
        Assert.True(state.IsReady);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedPartialRuntimeStartup_IsFatalAndIsNeverRetried()
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        var runtime = new Runtime { StartupFailure = new InvalidOperationException("generated code missing") };
        var state = new WolverineRuntimeStartupState();
        using var service = Create(runtime, _ => Task.FromResult(true), state, lifetime);
        var readiness = state.WaitUntilReadyAsync(CancellationToken.None);
        Assert.False(readiness.IsCompleted);
        await service.StartAsync(CancellationToken.None);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTask!);
        Assert.Equal("generated code missing", failure.Message);
        Assert.Equal(1, runtime.Starts);
        Assert.False(state.IsReady);
        Assert.False(await readiness.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await state.WaitUntilReadyAsync(CancellationToken.None));
        lifetime.Verify(value => value.StopApplication(), Times.Once);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, runtime.Stops);
    }

    [Fact]
    public async Task PermanentPreflightFailure_IsFatalWithoutStartingRuntime()
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        var runtime = new Runtime();
        var state = new WolverineRuntimeStartupState();
        using var service = Create(runtime, _ => throw Postgres("42501"), state, lifetime);
        var readiness = state.WaitUntilReadyAsync(CancellationToken.None);
        Assert.False(readiness.IsCompleted);
        await service.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<PostgresException>(() => service.ExecuteTask!);
        Assert.Equal(0, runtime.Starts);
        Assert.False(state.IsReady);
        Assert.False(await readiness.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await state.WaitUntilReadyAsync(CancellationToken.None));
        lifetime.Verify(value => value.StopApplication(), Times.Once);
        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("42P01", true)]
    [InlineData("3F000", true)]
    [InlineData("57P03", true)]
    [InlineData("08006", true)]
    [InlineData("42501", false)]
    [InlineData("28P01", false)]
    [InlineData("3D000", false)]
    public void PreflightRetry_PreservesPermanentPostgresFailures(string code, bool expected)
    {
        Assert.Equal(expected, SchemaGatedWolverineHostedService.IsRetryablePreflightFailure(
            new InvalidOperationException("storage assertion", Postgres(code))));
    }

    [Fact]
    public void MixedAggregatePreflightFailure_CannotHidePermanentConfigurationErrors()
    {
        Assert.True(SchemaGatedWolverineHostedService.IsRetryablePreflightFailure(
            new AggregateException(Postgres("42P01"), new TimeoutException())));
        Assert.False(SchemaGatedWolverineHostedService.IsRetryablePreflightFailure(
            new AggregateException(Postgres("42P01"), new ArgumentException("invalid options"))));
        Assert.False(SchemaGatedWolverineHostedService.IsRetryablePreflightFailure(new AggregateException()));
    }

    private static SchemaGatedWolverineHostedService Create(Runtime runtime,
        Func<CancellationToken, Task<bool>> preflight, WolverineRuntimeStartupState state,
        Mock<IHostApplicationLifetime>? lifetime = null) =>
        new(runtime, preflight, state, (lifetime ?? new Mock<IHostApplicationLifetime>()).Object,
            NullLogger<SchemaGatedWolverineHostedService>.Instance, TimeSpan.Zero);

    private static PostgresException Postgres(string code) => new("fixture", "ERROR", "ERROR", code);

    private sealed class Runtime : IHostedService
    {
        public int Starts;
        public int Stops;
        public bool PauseStart;
        public Exception? StartupFailure;
        public bool StoppedBeforeStartFinished;
        private bool _startFinished;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            Started.TrySetResult();
            if (PauseStart) await FinishStart.Task; // Simulate the pinned runtime ignoring cancellation.
            _startFinished = true;
            if (StartupFailure is not null) throw StartupFailure;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Stops);
            StoppedBeforeStartFinished = !_startFinished;
            return Task.CompletedTask;
        }
    }
}
