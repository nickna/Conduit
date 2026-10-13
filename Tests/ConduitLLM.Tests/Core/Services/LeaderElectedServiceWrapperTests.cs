using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Threading.Channels;

namespace ConduitLLM.Tests.Core.Services;

public sealed class LeaderElectedServiceWrapperTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FollowerShutdown_DoesNotStopBorrowedInnerOrStartAfterShutdown()
    {
        var election = new Mock<ILeaderElectionService>(MockBehavior.Strict);
        var inner = new ProbeService();
        var wrapper = new InspectableWrapper(inner, election.Object, ownsInner: false);

        var stopping = wrapper.StopAsync(CancellationToken.None);
        Assert.Same(stopping, wrapper.StopAsync(CancellationToken.None));
        await stopping.WaitAsync(Timeout);
        await wrapper.StartAsync(CancellationToken.None);
        await wrapper.BecomeLeaderAsync();
        await wrapper.LoseLeadershipAsync();
        await wrapper.DisposeAsync();
        wrapper.Dispose();

        Assert.Equal(0, inner.Starts);
        Assert.Equal(0, inner.Stops);
        Assert.Equal(0, inner.Finalizations);
        election.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LeaderShutdown_StopsStartedGenerationOnceAndDoesNotDisposeBorrowedInner()
    {
        var inner = new ProbeService();
        var wrapper = Wrapper(inner, ownsInner: false);
        await wrapper.BecomeLeaderAsync();
        await wrapper.BecomeLeaderAsync();

        await wrapper.StopAsync(CancellationToken.None).WaitAsync(Timeout);
        await wrapper.LoseLeadershipAsync();
        await wrapper.StopAsync(CancellationToken.None);
        await wrapper.DisposeAsync();

        Assert.Equal(1, inner.Starts);
        Assert.Equal(1, inner.Stops);
        Assert.True(Assert.Single(inner.StartTokens).IsCancellationRequested);
        Assert.Equal(0, inner.Finalizations);
    }

    [Fact]
    public async Task LeadershipLossAndReacquisition_StopEachGenerationBeforeStartingAnother()
    {
        var inner = new ProbeService();
        var wrapper = Wrapper(inner, ownsInner: false);
        await wrapper.BecomeLeaderAsync();
        await wrapper.LoseLeadershipAsync();
        await wrapper.LoseLeadershipAsync();
        await wrapper.BecomeLeaderAsync();
        await wrapper.StopAsync(CancellationToken.None);
        await wrapper.DisposeAsync();

        Assert.Equal(2, inner.Starts);
        Assert.Equal(2, inner.Stops);
        Assert.All(inner.StartTokens, token => Assert.True(token.IsCancellationRequested));
        Assert.Equal(0, inner.Finalizations);
    }

    [Fact]
    public async Task ConcurrentShutdownAndDisposal_JoinOneStopBeforeOwnedFinalization()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ProbeService
        {
            OnStop = async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        var wrapper = Wrapper(inner, ownsInner: true);
        await wrapper.BecomeLeaderAsync();
        var stopping = wrapper.StopAsync(CancellationToken.None);
        await entered.Task.WaitAsync(Timeout);
        var repeated = wrapper.StopAsync(CancellationToken.None);
        var disposing = wrapper.DisposeAsync().AsTask();
        var lateStart = wrapper.BecomeLeaderAsync();
        Assert.Same(stopping, repeated);
        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, inner.Finalizations);

        release.TrySetResult();
        await Task.WhenAll(stopping, disposing, lateStart).WaitAsync(Timeout);
        wrapper.Dispose();
        await wrapper.DisposeAsync();

        Assert.Equal(1, inner.Starts);
        Assert.Equal(1, inner.Stops);
        Assert.Equal(1, inner.DisposeCalls);
        Assert.Equal(1, inner.Finalizations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedDisposal_IsLocalAndIdempotentWithoutInvokingHostedStop(bool asynchronous)
    {
        var election = new Mock<ILeaderElectionService>(MockBehavior.Strict);
        var inner = new ProbeService();
        var wrapper = new InspectableWrapper(inner, election.Object, ownsInner: true);
        await wrapper.BecomeLeaderAsync();
        if (asynchronous) await wrapper.DisposeAsync();
        else wrapper.Dispose();
        await wrapper.DisposeAsync();
        wrapper.Dispose();
        await wrapper.StopAsync(CancellationToken.None);
        await wrapper.StartAsync(CancellationToken.None);
        await wrapper.BecomeLeaderAsync();

        Assert.Equal(1, inner.Starts);
        Assert.Equal(0, inner.Stops);
        Assert.Equal(1, inner.DisposeCalls);
        Assert.Equal(1, inner.Finalizations);
        election.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("registered")]
    [InlineData("borrowed-factory")]
    [InlineData("owned-factory")]
    public async Task RegistrationOwnership_FinalizesTheInnerExactlyOnce(string registration)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(Mock.Of<ILeaderElectionService>());
        ProbeService? inner = null;
        if (registration == "registered")
            services.AddLeaderElectedHostedService<ProbeService>();
        else if (registration == "borrowed-factory")
        {
            services.AddSingleton<ProbeService>();
            services.AddLeaderElectedHostedService<ProbeService>(provider => provider.GetRequiredService<ProbeService>(),
                ownsInnerService: false);
        }
        else
            services.AddLeaderElectedHostedService<ProbeService>(_ => inner = new ProbeService());
        var provider = services.BuildServiceProvider();
        var wrapper = Assert.IsType<LeaderElectedServiceWrapper>(Assert.Single(provider.GetServices<IHostedService>()));
        inner ??= provider.GetRequiredService<ProbeService>();

        await wrapper.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();
        wrapper.Dispose();

        Assert.Equal(0, inner.Stops);
        Assert.Equal(1, inner.DisposeCalls);
        Assert.Equal(1, inner.Finalizations);
    }

    [Fact]
    public async Task BorrowedAliases_KeepResourceFinalizationIdempotentWithoutWrapperDisposal()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(Mock.Of<ILeaderElectionService>());
        services.AddSingleton<ProbeService>();
        services.AddSingleton<IProbeService>(provider => provider.GetRequiredService<ProbeService>());
        services.AddLeaderElectedHostedService<ProbeService>(provider => (ProbeService)provider.GetRequiredService<IProbeService>(),
            ownsInnerService: false);
        var provider = services.BuildServiceProvider();
        _ = provider.GetServices<IHostedService>().ToArray();
        var inner = provider.GetRequiredService<ProbeService>();

        await provider.DisposeAsync();

        // DI captures both aliases. The wrapper adds no disposal, and the resource itself
        // tolerates the alias graph's repeated disposal calls.
        Assert.Equal(2, inner.DisposeCalls);
        Assert.Equal(1, inner.Finalizations);
    }

    [Fact]
    public async Task HostedShutdown_StopsInnerBeforeContainerDisposesItsOwnedResources()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ProbeService>();
        builder.Services.AddSingleton<IHostedService>(provider => new InspectableWrapper(
            provider.GetRequiredService<ProbeService>(), Mock.Of<ILeaderElectionService>(), ownsInner: false));
        var host = builder.Build();
        try
        {
            await host.StartAsync();
            var inner = host.Services.GetRequiredService<ProbeService>();
            var wrapper = Assert.IsType<InspectableWrapper>(Assert.Single(host.Services.GetServices<IHostedService>()));
            await wrapper.BecomeLeaderAsync();

            await host.StopAsync().WaitAsync(Timeout);

            Assert.Equal(1, inner.Stops);
            Assert.Equal(0, inner.Finalizations);
            await ((IAsyncDisposable)host).DisposeAsync();
            Assert.Equal(1, inner.DisposeCalls);
            Assert.Equal(1, inner.Finalizations);
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
        }
    }

    [Fact]
    public async Task CanceledHostedStop_ForwardsTokenAndDoesNotRetryTheGenerationDuringDisposal()
    {
        var inner = new ProbeService { OnStop = token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        var logger = new Mock<ILogger<LeaderElectedServiceWrapper>>();
        var wrapper = new InspectableWrapper(inner, Mock.Of<ILeaderElectionService>(), ownsInner: false, logger.Object);
        await wrapper.BecomeLeaderAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var stopping = wrapper.StopAsync(canceled.Token);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
        Assert.Equal(canceled.Token, failure.CancellationToken);
        Assert.Same(stopping, wrapper.StopAsync(CancellationToken.None));
        await wrapper.DisposeAsync();

        Assert.Equal(1, inner.Stops);
        Assert.Equal(0, inner.Finalizations);
        logger.Verify(value => value.Log(It.Is<LogLevel>(level => level >= LogLevel.Error),
            It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Never);
    }

    [Fact]
    public async Task LeadershipReacquisition_RestartsAnActualBackgroundServiceWithFreshCancellation()
    {
        var inner = new RestartingBackgroundService();
        var wrapper = new InspectableWrapper(inner, Mock.Of<ILeaderElectionService>(), ownsInner: true);
        await wrapper.BecomeLeaderAsync();
        Assert.Equal(1, await inner.Generations.Reader.ReadAsync().AsTask().WaitAsync(Timeout));
        await wrapper.LoseLeadershipAsync().WaitAsync(Timeout);
        await wrapper.BecomeLeaderAsync();
        Assert.Equal(2, await inner.Generations.Reader.ReadAsync().AsTask().WaitAsync(Timeout));
        await wrapper.StopAsync(CancellationToken.None).WaitAsync(Timeout);
        await wrapper.DisposeAsync();

        Assert.Equal(2, inner.CompletedGenerations);
    }

    [Fact]
    public async Task CanceledHostedStop_JoinsBackgroundWorkerBeforeDisposal()
    {
        var inner = new DrainingBackgroundService();
        var wrapper = new InspectableWrapper(inner, Mock.Of<ILeaderElectionService>(), ownsInner: true);
        await wrapper.BecomeLeaderAsync();
        await inner.Started.Task.WaitAsync(Timeout);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var stopping = wrapper.StopAsync(canceled.Token);
        await inner.Draining.Task.WaitAsync(Timeout);
        var disposing = wrapper.DisposeAsync().AsTask();
        Assert.False(stopping.IsCompleted);
        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, inner.Disposals);

        inner.Release.TrySetResult();
        try { await stopping.WaitAsync(Timeout); }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        await disposing.WaitAsync(Timeout);

        Assert.True(inner.WorkerCompleted);
        Assert.Equal(1, inner.Stops);
        Assert.Equal(1, inner.Disposals);
    }

    [Fact]
    public async Task FailedStartup_JoinsCleanupAndPreservesTheOriginalFailure()
    {
        var failure = new InvalidOperationException("startup failed");
        var inner = new ProbeService
        {
            OnStart = _ => throw failure,
            OnStop = _ => throw new IOException("cleanup failed")
        };
        var wrapper = Wrapper(inner, ownsInner: true);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(wrapper.BecomeLeaderAsync));
        await wrapper.StopAsync(CancellationToken.None);
        await wrapper.DisposeAsync();
        Assert.Equal(1, inner.Starts);
        Assert.Equal(1, inner.Stops);
        Assert.Equal(1, inner.Finalizations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalBeforeStop_CancelsAndJoinsTheExistingWorkerWithoutDependencyCleanup(bool ownsInner)
    {
        var election = new Mock<ILeaderElectionService>(MockBehavior.Strict);
        var inner = new DrainingBackgroundService();
        var wrapper = new InspectableWrapper(inner, election.Object, ownsInner);
        await wrapper.BecomeLeaderAsync();
        await inner.Started.Task.WaitAsync(Timeout);
        var disposing = wrapper.DisposeAsync().AsTask();
        await inner.Draining.Task.WaitAsync(Timeout);
        var stopping = wrapper.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        Assert.Equal(0, inner.Stops);
        Assert.Equal(0, inner.Disposals);

        inner.Release.TrySetResult();
        await Task.WhenAll(stopping, disposing).WaitAsync(Timeout);
        Assert.True(inner.WorkerCompleted);
        Assert.Equal(0, inner.Stops);
        Assert.Equal(ownsInner ? 1 : 0, inner.Disposals);
        election.VerifyNoOtherCalls();
        if (!ownsInner) inner.Dispose();
    }

    [Fact]
    public async Task ShutdownDuringStartup_CancelsThePublishedGenerationBeforeWaitingForItsTransition()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new Mock<ILogger<LeaderElectedServiceWrapper>>();
        var inner = new ProbeService
        {
            OnStart = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }
        };
        var election = new Mock<ILeaderElectionService>(MockBehavior.Strict);
        var wrapper = new InspectableWrapper(inner, election.Object, ownsInner: false, logger.Object);
        var starting = wrapper.BecomeLeaderAsync();
        await entered.Task.WaitAsync(Timeout);

        var stopping = wrapper.StopAsync(CancellationToken.None);

        Assert.True(Assert.Single(inner.StartTokens).IsCancellationRequested);
        await Task.WhenAll(starting, stopping).WaitAsync(Timeout);
        await wrapper.DisposeAsync();
        Assert.Equal(1, inner.Stops);
        Assert.Equal(0, inner.Finalizations);
        logger.Verify(value => value.Log(It.Is<LogLevel>(level => level >= LogLevel.Error),
            It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Never);
        election.VerifyNoOtherCalls();
    }

    private static InspectableWrapper Wrapper(ProbeService inner, bool ownsInner) =>
        new(inner, Mock.Of<ILeaderElectionService>(), ownsInner);

    private sealed class InspectableWrapper(IHostedService inner, ILeaderElectionService election, bool ownsInner,
        ILogger<LeaderElectedServiceWrapper>? logger = null)
        : LeaderElectedServiceWrapper(inner, election, logger ?? NullLogger<LeaderElectedServiceWrapper>.Instance,
            "ownership-test", ownsInner)
    {
        public Task BecomeLeaderAsync() => OnBecameLeaderAsync(CancellationToken.None);
        public Task LoseLeadershipAsync() => OnLostLeadershipAsync(CancellationToken.None);
    }

    public interface IProbeService { }

    private sealed class RestartingBackgroundService : BackgroundService
    {
        private int _generation;
        public Channel<int> Generations { get; } = Channel.CreateUnbounded<int>();
        public int CompletedGenerations { get; private set; }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await Generations.Writer.WriteAsync(Interlocked.Increment(ref _generation));
            try { await Task.Delay(System.Threading.Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            CompletedGenerations++;
        }
    }

    private sealed class DrainingBackgroundService : BackgroundService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Draining { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops { get; private set; }
        public int Disposals { get; private set; }
        public bool WorkerCompleted { get; private set; }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            Draining.TrySetResult();
            await Release.Task;
            WorkerCompleted = true;
        }
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            return base.StopAsync(cancellationToken);
        }
        public override void Dispose()
        {
            Assert.True(WorkerCompleted);
            Disposals++;
            base.Dispose();
        }
    }

    public sealed class ProbeService : IHostedService, IProbeService, IDisposable, IAsyncDisposable
    {
        private int _starts;
        private int _stops;
        private int _disposeCalls;
        private int _finalizations;
        private int _disposed;
        public int Starts => Volatile.Read(ref _starts);
        public int Stops => Volatile.Read(ref _stops);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public int Finalizations => Volatile.Read(ref _finalizations);
        public List<CancellationToken> StartTokens { get; } = [];
        public Func<CancellationToken, Task>? OnStart { get; init; }
        public Func<CancellationToken, Task>? OnStop { get; init; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            StartTokens.Add(cancellationToken);
            return OnStart?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(0, Finalizations);
            Interlocked.Increment(ref _stops);
            return OnStop?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Increment(ref _finalizations);
        }
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
