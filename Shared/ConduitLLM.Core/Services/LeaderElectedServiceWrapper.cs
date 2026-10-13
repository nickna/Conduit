using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>Runs a hosted service only while this instance holds leadership.</summary>
public class LeaderElectedServiceWrapper : LeaderElectedBackgroundService
{
    private readonly IHostedService _innerService;
    private readonly ILogger<LeaderElectedServiceWrapper> _logger;
    private readonly bool _ownsInnerService;
    private readonly SemaphoreSlim _innerServiceLock = new(1, 1);
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _innerServiceCts;
    private Task? _startTask;
    private Task? _stopTask;
    private Task? _disposeTask;
    private bool _shutdownRequested;
    private bool _disposalRequested;

    public LeaderElectedServiceWrapper(
        IHostedService innerService,
        ILeaderElectionService leaderElectionService,
        ILogger<LeaderElectedServiceWrapper> logger,
        string serviceName,
        bool ownsInnerService = true)
        : base(leaderElectionService, logger, serviceName)
    {
        _innerService = innerService ?? throw new ArgumentNullException(nameof(innerService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownsInnerService = ownsInnerService;
    }

    protected override async Task OnBecameLeaderAsync(CancellationToken cancellationToken)
    {
        await _innerServiceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? generation = null;
            try
            {
                Task starting;
                lock (_lifecycleLock)
                {
                    if (_shutdownRequested || _innerServiceCts is not null) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.LogInformation("Starting inner service {ServiceName} as leader", ServiceName);
                    generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _innerServiceCts = generation;
                    starting = _innerService.StartAsync(generation.Token);
                }
                await starting.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (generation is not null &&
                generation.IsCancellationRequested && ShutdownRequested())
            {
                // Shutdown has already closed admission and canceled this published
                // generation. Its Stop (or local disposal join) owns the remaining work.
            }
            catch
            {
                // A failed start can already have armed background work. Join its cleanup
                // before allowing another leadership generation to start.
                try { await StopInnerGenerationAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to clean up inner service {ServiceName} after startup failed", ServiceName);
                }
                throw;
            }
        }
        finally
        {
            _innerServiceLock.Release();
        }
    }

    protected override async Task OnLostLeadershipAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
            if (_disposalRequested)
                return;
        await StopInnerServiceAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteLeaderWorkAsync(CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_shutdownRequested)
                return Task.CompletedTask;
            return _startTask ??= base.StartAsync(cancellationToken);
        }
    }

    /// <summary>Joins leader work and stops the generation this wrapper actually started.</summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_stopTask is not null)
                return _stopTask;
            if (_disposalRequested)
                return _stopTask = JoinDisposedGenerationAsync(_disposeTask);
            _shutdownRequested = true;
            return _stopTask = StopWrapperAsync(cancellationToken,
                _innerServiceCts?.CancelAsync() ?? Task.CompletedTask);
        }
    }

    private async Task JoinDisposedGenerationAsync(Task? disposing)
    {
        if (disposing is not null) await disposing.ConfigureAwait(false);
        await _innerServiceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource? generation;
            lock (_lifecycleLock)
            {
                generation = _innerServiceCts;
                _innerServiceCts = null;
            }
            if (generation is not null)
            {
                try { await JoinBackgroundWorkerAsync(generation).ConfigureAwait(false); }
                finally
                {
                    generation.Dispose();
                }
            }
            if (ExecuteTask is { } execution)
            {
                try { await execution.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        finally { _innerServiceLock.Release(); }
        // Disposal already closed both producers. Join their existing work without invoking
        // inner StopAsync or releasing leases through dependencies that may be disposed.
    }

    private bool ShutdownRequested()
    {
        lock (_lifecycleLock) return _shutdownRequested;
    }

    private async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Inner service {ServiceName} cancellation callback failed", ServiceName);
        }
    }

    private async Task StopWrapperAsync(CancellationToken cancellationToken, Task cancellation)
    {
        await ObserveCancellationAsync(cancellation).ConfigureAwait(false);
        Exception? stopFailure = null;
        try { await StopInnerServiceAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception)
        {
            stopFailure = exception;
            throw;
        }
        finally
        {
            // Keep the lease until the inner worker has stopped producing effects.
            try { await base.StopAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (stopFailure is not null)
            {
                _logger.LogError(exception, "Leader cleanup failed after inner service {ServiceName} failed to stop", ServiceName);
            }
        }
    }

    private async Task StopInnerServiceAsync(CancellationToken cancellationToken)
    {
        await _innerServiceLock.WaitAsync().ConfigureAwait(false);
        try { await StopInnerGenerationAsync(cancellationToken).ConfigureAwait(false); }
        finally { _innerServiceLock.Release(); }
    }

    private async Task StopInnerGenerationAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? generation;
        lock (_lifecycleLock)
        {
            generation = _innerServiceCts;
            _innerServiceCts = null;
        }
        if (generation is null)
            return;
        Exception? stopFailure = null;
        try
        {
            try { await generation.CancelAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Inner service {ServiceName} cancellation callback failed", ServiceName);
            }
            await _innerService.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                // BackgroundService.StopAsync can return when the host deadline expires.
                // Keep ownership of its worker until the cancellation has actually drained it.
                try { await JoinBackgroundWorkerAsync(generation).ConfigureAwait(false); }
                catch (Exception exception) when (stopFailure is not null)
                {
                    _logger.LogError(exception, "Inner worker {ServiceName} failed after its StopAsync failed", ServiceName);
                }
            }
            finally { generation.Dispose(); }
        }
    }

    private async Task JoinBackgroundWorkerAsync(CancellationTokenSource generation)
    {
        if (_innerService is not BackgroundService { ExecuteTask: { } worker }) return;
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (generation.IsCancellationRequested) { }
    }

    /// <summary>Disposes local resources and, when explicitly owned, the inner service.</summary>
    public override ValueTask DisposeAsync() => new(GetDisposalTask(asynchronous: true));

    public override void Dispose() => GetDisposalTask(asynchronous: false).GetAwaiter().GetResult();

    private Task GetDisposalTask(bool asynchronous)
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null)
                return _disposeTask;
            _shutdownRequested = true;
            _disposalRequested = true;
            return _disposeTask = DisposeWrapperAsync(_stopTask, asynchronous,
                _innerServiceCts?.CancelAsync() ?? Task.CompletedTask);
        }
    }

    private async Task DisposeWrapperAsync(Task? stopping, bool asynchronous, Task cancellation)
    {
        await ObserveCancellationAsync(cancellation).ConfigureAwait(false);
        if (stopping is not null)
        {
            try { await stopping.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Inner service {ServiceName} did not stop successfully", ServiceName);
            }
        }
        await _innerServiceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Disposal never invokes StopAsync or leadership/Redis cleanup. Hosted shutdown
            // owns that work; a borrowed inner service remains owned by its DI registration.
            CancellationTokenSource? generation;
            lock (_lifecycleLock) generation = _innerServiceCts;
            if (generation is not null)
            {
                try { await generation.CancelAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Inner service {ServiceName} cancellation callback failed", ServiceName);
                }
                // Retain a borrowed generation for a later Stop's local join. An owned
                // BackgroundService must finish before its resources can be finalized.
                if (_ownsInnerService)
                {
                    try { await JoinBackgroundWorkerAsync(generation).ConfigureAwait(false); }
                    finally
                    {
                        generation.Dispose();
                        lock (_lifecycleLock) _innerServiceCts = null;
                    }
                }
            }
            if (_ownsInnerService)
            {
                if (asynchronous && _innerService is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (_innerService is IDisposable disposable)
                    disposable.Dispose();
                else if (_innerService is IAsyncDisposable onlyAsyncDisposable)
                    await onlyAsyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // Keep the managed gate usable for concurrent, already queued lifecycle callers.
            _innerServiceLock.Release();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
