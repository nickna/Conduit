using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Services;

public partial class BatchSpendUpdateService
{
    private readonly object _lifetimeSync = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly CancellationTokenSource _flushLifetime = new();
    private readonly CancellationTokenSource _queueLifetime = new();
    private readonly HashSet<Task> _flushTasks = [];
    private readonly HashSet<Task> _queueTasks = [];
    private CancellationTokenSource? _producerCancellation;
    private Task? _producerTask;
    private Task? _producerStopTask;
    private Task? _drainTask;
    private bool _admissionClosed;
    private int _disposed;
    private int _leaderActive;

    private sealed record FallbackSpendUpdate(
        int VirtualKeyId, decimal Cost, DateTime BillingWindowStartUtc, string IdempotencyKey);

    private void ThrowIfAdmissionClosed()
    {
        if (_admissionClosed || Volatile.Read(ref _disposed) != 0)
            throw new InvalidOperationException("Batch spending has completed host shutdown");
    }

    // Direct use retains the original Start/Stop contract. Production uses the two
    // borrowed adapters below, separating host lifetime from restartable leadership.
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await StartLocalProducerAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            await DrainAsync(cancellationToken);
            return;
        }
        await base.StopAsync(cancellationToken);
        if (ExecuteTask is { } execution) await JoinAsync(execution);
        await StopLocalProducerAsync(cancellationToken);
        await DrainAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunLeaderWorkerAsync(stoppingToken);

    public Task StartLocalProducerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifetimeSync)
        {
            ThrowIfAdmissionClosed();
            if (_producerTask is null)
            {
                _producerCancellation = new CancellationTokenSource();
                _producerTask = ProduceAsync(_producerCancellation.Token);
            }
        }
        return Task.CompletedTask;
    }

    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_flushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try { await FlushPendingUpdatesAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Error in periodic batch spend flush"); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task RunLeaderWorkerAsync(CancellationToken stoppingToken)
    {
        using var leaderCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaderToken = leaderCancellation.Token;
        lock (_lifetimeSync)
        {
            if (_admissionClosed || Volatile.Read(ref _disposed) != 0) return;
            _cancellationTokenSource = leaderCancellation;
            Volatile.Write(ref _leaderActive, 1);
        }
        _logger.LogInformation("BatchSpendUpdateService started");
        try
        {
            try
            {
                var count = await FlushPendingUpdatesAsync(leaderToken);
                if (count > 0) _logger.LogInformation("Flushed {Count} pending updates from previous session", count);
            }
            catch (OperationCanceledException) when (leaderToken.IsCancellationRequested || _flushLifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogError(ex, "Error flushing pending updates on startup"); }

            using var timer = new PeriodicTimer(_flushInterval);
            while (await timer.WaitForNextTickAsync(leaderToken))
            {
                try { await FlushPendingUpdatesAsync(leaderToken); }
                catch (OperationCanceledException) when (leaderToken.IsCancellationRequested || _flushLifetime.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Error in BatchSpendUpdateService background execution"); }
            }
        }
        catch (OperationCanceledException) when (leaderToken.IsCancellationRequested) { }
        finally
        {
            leaderCancellation.Cancel();
            Volatile.Write(ref _leaderActive, 0);
            lock (_lifetimeSync) _cancellationTokenSource = null;
            _logger.LogInformation("BatchSpendUpdateService stopped");
        }
    }

    public Task StopLocalProducerAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeSync)
        {
            if (Volatile.Read(ref _disposed) != 0) return _producerStopTask ??= JoinProducerAndFlushesAsync();
            _producerCancellation?.Cancel();
            _flushLifetime.Cancel();
            return _producerStopTask ??= JoinProducerAndFlushesAsync();
        }
    }

    private async Task JoinProducerAndFlushesAsync()
    {
        if (_producerTask is { } producer) await JoinAsync(producer);
        Task[] flushes;
        lock (_lifetimeSync) flushes = _flushTasks.ToArray();
        foreach (var flush in flushes) await JoinAsync(flush);
    }

    private async Task JoinAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Batch spending work ended during shutdown"); }
    }

    public Task DrainAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeSync)
        {
            if (Volatile.Read(ref _disposed) != 0) return _drainTask ??= JoinDisposedWorkAsync();
            _admissionClosed = true;
            return _drainTask ??= DrainCoreAsync(cancellationToken);
        }
    }

    private async Task DrainCoreAsync(CancellationToken cancellationToken)
    {
        using var cancellation = cancellationToken.Register(() => _queueLifetime.Cancel());
        await StopLocalProducerAsync(cancellationToken);
        Task[] queues;
        lock (_lifetimeSync) queues = _queueTasks.ToArray();
        foreach (var queue in queues) await JoinAsync(queue);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var count = await FlushSerializedAsync(cancellationToken, finalDrain: true);
            if (count > 0) _logger.LogInformation("Flushed {Count} pending updates during shutdown", count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogError(ex, "Error flushing pending updates during shutdown"); }
    }

    private async Task JoinDisposedWorkAsync()
    {
        if (ExecuteTask is { } execution) await JoinAsync(execution);
        await StopLocalProducerAsync(CancellationToken.None);
        Task[] queues;
        lock (_lifetimeSync) queues = _queueTasks.ToArray();
        foreach (var queue in queues) await JoinAsync(queue);
    }

    public Task<int> FlushPendingUpdatesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifetimeSync)
        {
            ThrowIfAdmissionClosed();
            var task = FlushSerializedAsync(cancellationToken);
            TrackTask(_flushTasks, task);
            return task;
        }
    }

    private void TrackTask(HashSet<Task> tasks, Task task)
    {
        tasks.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_lifetimeSync) tasks.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<int> FlushSerializedAsync(CancellationToken cancellationToken, bool finalDrain = false)
    {
        using var linked = finalDrain ? null :
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _flushLifetime.Token);
        var token = linked?.Token ?? cancellationToken;
        await _flushLock.WaitAsync(token);
        try { return await FlushCoreAsync(token); }
        finally { _flushLock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_lifetimeSync)
        {
            _admissionClosed = true;
            _producerCancellation?.Cancel();
            _cancellationTokenSource?.Cancel();
            _flushLifetime.Cancel();
            _queueLifetime.Cancel();
            if (_flushTasks.Count == 0 && _queueTasks.Count == 0 &&
                _producerTask?.IsCompleted != false && _drainTask?.IsCompleted != false &&
                Volatile.Read(ref _leaderActive) == 0)
            {
                _producerCancellation?.Dispose();
                _flushLifetime.Dispose();
                _queueLifetime.Dispose();
                _flushLock.Dispose();
            }
        }
        // Hosted shutdown has already joined work. Disposal never starts a flush or Redis I/O.
        base.Dispose();
    }
}

/// <summary>Restartable leader work; the batch producer belongs to the whole host.</summary>
public sealed class BatchSpendLeaderWorker(BatchSpendUpdateService batch) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => batch.RunLeaderWorkerAsync(stoppingToken);
}

/// <summary>Borrowed host lifetime adapter, including follower-local fallback flushing.</summary>
public sealed class BatchSpendLifecycleHostedService(BatchSpendUpdateService batch) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => batch.StartLocalProducerAsync(cancellationToken);
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => batch.StopLocalProducerAsync(cancellationToken);
    public Task StoppedAsync(CancellationToken cancellationToken) => batch.DrainAsync(cancellationToken);
}
