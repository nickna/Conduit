using System.Collections.Concurrent;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class BatchAuditHostedLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task QueueAdapterForwardsLifecycleTokensAndBorrowsItsInnerService()
    {
        using var inner = new GatedHostedService();
        var adapter = new BatchAuditQueueHostedService(inner);
        using var startup = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        Task starting = null;
        Task stopping = null;
        try
        {
            starting = adapter.StartAsync(startup.Token);
            Assert.Equal(startup.Token, await inner.Started.Task.WaitAsync(TestTimeout));
            Assert.False(starting.IsCompleted);
            inner.ReleaseStart.TrySetResult();
            await starting.WaitAsync(TestTimeout);

            stopping = adapter.StopAsync(shutdown.Token);
            Assert.Equal(shutdown.Token, await inner.Stopped.Task.WaitAsync(TestTimeout));
            Assert.False(stopping.IsCompleted);
            inner.ReleaseStop.TrySetResult();
            await stopping.WaitAsync(TestTimeout);

            Assert.False((object)adapter is IDisposable);
            Assert.False((object)adapter is IAsyncDisposable);
            Assert.Equal(0, inner.DisposeCalls);
        }
        finally
        {
            inner.ReleaseStart.TrySetResult();
            inner.ReleaseStop.TrySetResult();
            if (starting is not null) await starting.WaitAsync(TestTimeout);
            if (stopping is not null) await stopping.WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task LocalQueueFlushesAndDrainsWhenRetentionNeverAcquiresLeadership()
    {
        using var queue = new ProbeAuditQueue();
        var local = new BatchAuditQueueHostedService(queue);
        var timerEvent = new BillingAuditEvent();
        var shutdownEvent = new BillingAuditEvent();
        try
        {
            await queue.LogEventAsync(timerEvent);
            await local.StartAsync(CancellationToken.None);
            await queue.WhenPersistedAsync(timerEvent.Id).WaitAsync(TestTimeout);
            Assert.Equal(0, queue.RetentionCalls);

            await queue.LogEventAsync(shutdownEvent);
            await local.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

            Assert.Equal(new[] { timerEvent.Id, shutdownEvent.Id }, queue.PersistedIds);
            Assert.Equal(0, queue.RetentionCalls);
            Assert.Equal(0, queue.DisposeCalls);
        }
        finally { await local.StopAsync(CancellationToken.None).WaitAsync(TestTimeout); }
    }

    [Fact]
    public async Task RetentionStopCancelsAndJoinsItsGenerationBeforeRestartingWithAFreshToken()
    {
        var first = new GatedRetention();
        var second = new GatedRetention();
        var runs = 0;
        await using var retention = new BatchAuditRetentionHostedService(
            token => Interlocked.Increment(ref runs) == 1 ? first.RunAsync(token) : second.RunAsync(token),
            NullLogger<BatchAuditRetentionHostedService>.Instance);
        using var startup = new CancellationTokenSource();
        using var expiredShutdown = new CancellationTokenSource();
        expiredShutdown.Cancel();
        Task stopping = null;
        try
        {
            await retention.StartAsync(startup.Token);
            var firstToken = await first.Entered.Task.WaitAsync(TestTimeout);
            Assert.NotEqual(startup.Token, firstToken);
            Assert.False(firstToken.IsCancellationRequested);
            await retention.StartAsync(startup.Token);
            Assert.Equal(1, Volatile.Read(ref runs));

            // Even an expired host deadline must not detach an admitted worker.
            stopping = retention.StopAsync(expiredShutdown.Token);
            await first.Canceled.Task.WaitAsync(TestTimeout);
            Assert.False(stopping.IsCompleted);
            Assert.False(startup.IsCancellationRequested);
            first.Release();
            await stopping.WaitAsync(TestTimeout);
            await first.Completed.Task.WaitAsync(TestTimeout);

            await retention.StartAsync(startup.Token);
            var secondToken = await second.Entered.Task.WaitAsync(TestTimeout);
            Assert.NotEqual(firstToken, secondToken);
            Assert.False(secondToken.IsCancellationRequested);
            Assert.Equal(2, Volatile.Read(ref runs));

            stopping = retention.StopAsync(CancellationToken.None);
            await second.Canceled.Task.WaitAsync(TestTimeout);
            Assert.False(stopping.IsCompleted);
            second.Release();
            await stopping.WaitAsync(TestTimeout);
            await second.Completed.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            first.Release();
            second.Release();
            if (stopping is not null) await stopping.WaitAsync(TestTimeout);
            await retention.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task RetentionStopDoesNotDrainOrDisposeTheBorrowedAuditQueue()
    {
        var run = new GatedRetention();
        using var queue = new ProbeAuditQueue(run.RunAsync);
        await using var retention = new BatchAuditRetentionHostedService(queue.RunDataRetentionAsync,
            NullLogger<BatchAuditRetentionHostedService>.Instance);
        var auditEvent = new BillingAuditEvent();
        Task stopping = null;
        try
        {
            // A request may arrive before the local hosted service starts.
            await queue.LogEventAsync(auditEvent);
            await retention.StartAsync(CancellationToken.None);
            await run.Entered.Task.WaitAsync(TestTimeout);
            Assert.Equal(1, queue.RetentionCalls);
            stopping = retention.StopAsync(CancellationToken.None);
            await run.Canceled.Task.WaitAsync(TestTimeout);
            Assert.False(stopping.IsCompleted);
            Assert.Empty(queue.PersistedIds);
            run.Release();
            await stopping.WaitAsync(TestTimeout);

            Assert.Empty(queue.PersistedIds);
            Assert.Equal(0, queue.DisposeCalls);
            await new BatchAuditQueueHostedService(queue).StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            Assert.Equal(new[] { auditEvent.Id }, queue.PersistedIds);
        }
        finally
        {
            run.Release();
            if (stopping is not null) await stopping.WaitAsync(TestTimeout);
            await retention.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await queue.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task StoppingRetentionLeavesTheProcessLocalFlushTimerRunning()
    {
        var run = new GatedRetention();
        using var queue = new ProbeAuditQueue(run.RunAsync);
        var local = new BatchAuditQueueHostedService(queue);
        await using var retention = new BatchAuditRetentionHostedService(queue.RunDataRetentionAsync,
            NullLogger<BatchAuditRetentionHostedService>.Instance);
        Task stopping = null;
        try
        {
            await local.StartAsync(CancellationToken.None);
            await retention.StartAsync(CancellationToken.None);
            await run.Entered.Task.WaitAsync(TestTimeout);
            stopping = retention.StopAsync(CancellationToken.None);
            await run.Canceled.Task.WaitAsync(TestTimeout);
            run.Release();
            await stopping.WaitAsync(TestTimeout);

            var afterLeadershipLoss = new BillingAuditEvent();
            await queue.LogEventAsync(afterLeadershipLoss);
            // This is the real periodic timer, not an explicit flush or shutdown drain.
            await queue.WhenPersistedAsync(afterLeadershipLoss.Id).WaitAsync(TestTimeout);
            Assert.Equal(new[] { afterLeadershipLoss.Id }, queue.PersistedIds);
            Assert.Equal(0, queue.DisposeCalls);
        }
        finally
        {
            run.Release();
            if (stopping is not null) await stopping.WaitAsync(TestTimeout);
            await retention.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await local.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task FaultedRetentionIsObservedAtStopAndDoesNotPreventANewGeneration()
    {
        var original = new InvalidOperationException("Owned retention worker failure");
        var failedEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new GatedRetention();
        var logger = new RecordingRetentionLogger();
        var runs = 0;
        await using var retention = new BatchAuditRetentionHostedService(token =>
        {
            if (Interlocked.Increment(ref runs) != 1) return next.RunAsync(token);
            failedEntered.TrySetResult(token);
            return Task.FromException(original);
        }, logger);
        try
        {
            await retention.StartAsync(CancellationToken.None);
            var failedToken = await failedEntered.Task.WaitAsync(TestTimeout);
            Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                retention.StopAsync(CancellationToken.None).WaitAsync(TestTimeout)));
            Assert.Contains(logger.Entries, entry => entry.Level >= LogLevel.Error && ReferenceEquals(entry.Exception, original));

            await retention.StartAsync(CancellationToken.None);
            var nextToken = await next.Entered.Task.WaitAsync(TestTimeout);
            Assert.NotEqual(failedToken, nextToken);
            Assert.False(nextToken.IsCancellationRequested);
            Assert.Equal(2, Volatile.Read(ref runs));
        }
        finally
        {
            next.Release();
            await retention.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetentionDisposalJoinsOnlyItsWorkerAndLeavesQueueOwnershipToTheHost(bool asynchronous)
    {
        var run = new GatedRetention();
        using var queue = new ProbeAuditQueue(run.RunAsync);
        var retention = new BatchAuditRetentionHostedService(queue.RunDataRetentionAsync,
            NullLogger<BatchAuditRetentionHostedService>.Instance);
        var auditEvent = new BillingAuditEvent();
        Task disposing = null;
        try
        {
            await queue.LogEventAsync(auditEvent);
            await retention.StartAsync(CancellationToken.None);
            await run.Entered.Task.WaitAsync(TestTimeout);
            disposing = asynchronous ? retention.DisposeAsync().AsTask() : Task.Run(retention.Dispose);
            await run.Canceled.Task.WaitAsync(TestTimeout);
            Assert.False(disposing.IsCompleted);
            Assert.Empty(queue.PersistedIds);
            run.Release();
            await disposing.WaitAsync(TestTimeout);
            await run.Completed.Task.WaitAsync(TestTimeout);

            Assert.Empty(queue.PersistedIds);
            Assert.Equal(0, queue.DisposeCalls);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => retention.StartAsync(CancellationToken.None));
            await retention.DisposeAsync();
            await queue.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            Assert.Equal(new[] { auditEvent.Id }, queue.PersistedIds);
        }
        finally
        {
            run.Release();
            if (disposing is not null) await disposing.WaitAsync(TestTimeout);
            await retention.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            await queue.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task DisposalObservesAndLogsARetentionFaultWhenHostedStopNeverRan()
    {
        var original = new InvalidOperationException("Unobserved retention failure");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingRetentionLogger();
        var retention = new BatchAuditRetentionHostedService(_ =>
        {
            entered.TrySetResult();
            return Task.FromException(original);
        }, logger);
        try
        {
            await retention.StartAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TestTimeout);
            await retention.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            Assert.Contains(logger.Entries, entry => entry.Level >= LogLevel.Error && ReferenceEquals(entry.Exception, original));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => retention.StartAsync(CancellationToken.None));
        }
        finally { await retention.DisposeAsync().AsTask().WaitAsync(TestTimeout); }
    }

    private sealed class GatedHostedService : IHostedService, IDisposable
    {
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CancellationToken> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(cancellationToken);
            return ReleaseStart.Task;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped.TrySetResult(cancellationToken);
            return ReleaseStop.Task;
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class GatedRetention
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _released.TrySetResult();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Canceled.TrySetResult());
            Entered.TrySetResult(cancellationToken);
            try
            {
                await _released.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally { Completed.TrySetResult(); }
        }
    }

    private sealed class ProbeAuditQueue(Func<CancellationToken, Task> retention = null)
        : BatchAuditServiceBase<BillingAuditEvent>(new NoDatabaseServices(), NullLogger.Instance)
    {
        private readonly ConcurrentQueue<Guid> _persisted = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _observed = new();
        private readonly string _name = $"HostedLifecycle_{Guid.NewGuid():N}";
        private int _retentionCalls;
        private int _disposeCalls;
        public int RetentionCalls => Volatile.Read(ref _retentionCalls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public Guid[] PersistedIds => _persisted.ToArray();
        protected override string EntityName => _name;
        protected override int FlushIntervalSeconds => 1;

        public Task WhenPersistedAsync(Guid id) =>
            _observed.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        protected override DbSet<BillingAuditEvent> GetDbSet(ConduitDbContext context) =>
            throw new InvalidOperationException("The lifecycle fixture must not access a database.");

        protected override Task PersistEventsAsync(IReadOnlyList<BillingAuditEvent> events,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in events)
            {
                _persisted.Enqueue(row.Id);
                _observed.GetOrAdd(row.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }
            return Task.CompletedTask;
        }

        protected override Task ScheduleDataRetentionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _retentionCalls);
            return retention?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref _disposeCalls);
            base.Dispose(disposing);
        }
    }

    private sealed class NoDatabaseServices : IServiceProvider
    {
        public object GetService(Type serviceType) =>
            throw new InvalidOperationException("The lifecycle fixture must not resolve database services.");
    }

    private sealed class RecordingRetentionLogger : ILogger<BatchAuditRetentionHostedService>
    {
        public ConcurrentQueue<(LogLevel Level, Exception Exception)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter) => Entries.Enqueue((logLevel, exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
