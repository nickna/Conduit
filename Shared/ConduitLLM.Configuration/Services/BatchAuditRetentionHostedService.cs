using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Services;

/// <summary>
/// Owns a restartable retention worker while borrowing the audit service it cleans.
/// Queue startup, shutdown draining and disposal belong to the host independently.
/// </summary>
public sealed class BatchAuditRetentionHostedService : IHostedService, IDisposable, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _run;
    private readonly ILogger<BatchAuditRetentionHostedService> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _generation;
    private Task? _worker;
    private bool _disposed;

    public BatchAuditRetentionHostedService(
        Func<CancellationToken, Task> run,
        ILogger<BatchAuditRetentionHostedService> logger)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_generation is not null) return;
            cancellationToken.ThrowIfCancellationRequested();
            var generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _generation = generation;
            // Keep the task even when a one-shot scheduler finishes before Stop.
            // Cancellation and faults must be observed before another generation starts.
            _worker = Task.Run(() => RunAsync(generation.Token), CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Closing and joining an admitted generation cannot be skipped because the
        // host deadline expired: the leader wrapper still owns its lease/dependencies.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopGenerationAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _run(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task StopGenerationAsync()
    {
        var generation = _generation;
        var worker = _worker;
        if (generation is null) return;
        try
        {
            try { await generation.CancelAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Audit retention cancellation callback failed");
            }
            if (worker is not null) await worker.ConfigureAwait(false);
        }
        finally
        {
            _worker = null;
            _generation = null;
            generation.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await StopGenerationAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                // Observe failures even when hosted Stop never ran; disposal still
                // owns only this local worker and never disposes its borrowed audit.
                _logger.LogError(exception, "Audit retention worker failed during disposal");
            }
        }
        finally
        {
            // Queued lifecycle callers may still be waiting on this managed gate.
            _lifecycle.Release();
        }
    }
}
