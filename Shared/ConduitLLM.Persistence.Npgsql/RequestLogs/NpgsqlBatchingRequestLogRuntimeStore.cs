using System.Threading.Channels;

using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// Coalesces concurrent Native request logs without weakening caller acknowledgment:
/// a successful write always means that its generated ID was committed.
/// </summary>
public sealed class NpgsqlBatchingRequestLogRuntimeStore : IRequestLogRuntimeStore, IDisposable, IAsyncDisposable
{
    private readonly Channel<PendingWrite> _queue;
    private readonly Func<RequestLogRuntimeRecord[], CancellationToken, Task<int[]>> _write;
    private readonly CancellationTokenSource _abort = new();
    private readonly object _lifecycle = new();
    private readonly int _batchSize;
    private Task? _worker;
    private bool _stopping;
    private int _disposed;

    public NpgsqlBatchingRequestLogRuntimeStore(NpgsqlDataSource dataSource)
        : this(new NpgsqlRequestLogBatchWriter(dataSource ?? throw new ArgumentNullException(nameof(dataSource))).WriteAsync)
    {
    }

    internal NpgsqlBatchingRequestLogRuntimeStore(
        Func<RequestLogRuntimeRecord[], CancellationToken, Task<int[]>> write,
        int capacity = 1024,
        int batchSize = 100)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        _write = write;
        _batchSize = batchSize;
        _queue = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Cancellation applies before/while waiting for bounded admission. Once
    /// admitted, the caller observes the durable outcome, even if its token is
    /// subsequently canceled. Accepted work is never abandoned in the background.
    /// </summary>
    public async Task<int> WriteAsync(RequestLogRuntimeRecord record, CancellationToken cancellationToken = default)
    {
        NpgsqlRequestLogRuntimeStore.Validate(record);
        cancellationToken.ThrowIfCancellationRequested();
        // Every field is scalar/string. Own the immutable record before yielding
        // for backpressure; the configuration writer already owns MetadataJson.
        var pending = new PendingWrite(record with { });
        Start();
        try
        {
            await _queue.Writer.WriteAsync(pending, cancellationToken);
        }
        catch (ChannelClosedException error)
        {
            throw new InvalidOperationException("Request log writer is no longer accepting writes.", error);
        }
        return await pending.Completion.Task;
    }

    /// <summary>Starts the single writer; no database work occurs until admission.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Start();
        return Task.CompletedTask;
    }

    private void Start()
    {
        lock (_lifecycle)
        {
            if (_stopping) throw new InvalidOperationException("Request log writer is stopping.");
            if (_worker is not null) return;
            // A lazy first write must not make every later batch inherit that
            // request's Activity, logging scopes or ambient transaction.
            if (ExecutionContext.IsFlowSuppressed()) _worker = Task.Run(RunAsync);
            else
            {
                using (ExecutionContext.SuppressFlow()) _worker = Task.Run(RunAsync);
            }
        }
    }

    /// <summary>
    /// Closes admission, drains accepted writes and joins their worker. A canceled
    /// shutdown aborts database work and explicitly completes every remaining
    /// waiter with failure; it still joins cleanup before returning.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task worker;
        lock (_lifecycle)
        {
            _stopping = true;
            _queue.Writer.TryComplete();
            worker = _worker ?? Task.CompletedTask;
        }
        if (worker.IsCompleted) { await worker; return; }
        using var registration = cancellationToken.Register(static state => ((NpgsqlBatchingRequestLogRuntimeStore)state!).Abort(), this);
        await worker.ConfigureAwait(false);
    }

    private void Abort()
    {
        try { _abort.Cancel(); }
        // Disposal can race a repeated StopAsync only after the worker has
        // already joined. There is then no remaining database operation to abort.
        catch (ObjectDisposedException) { return; }
    }

    private async Task RunAsync()
    {
        Exception? stopped = null;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_abort.Token))
            {
                var pending = new List<PendingWrite>(_batchSize);
                while (pending.Count < _batchSize && _queue.Reader.TryRead(out var item)) pending.Add(item);
                if (pending.Count == 0) continue;
                await WriteAvailableAsync(pending);
            }
        }
        catch (Exception error)
        {
            stopped = error;
        }
        finally
        {
            _queue.Writer.TryComplete(stopped);
            while (_queue.Reader.TryRead(out var pending))
                Fail(pending, stopped ?? new InvalidOperationException("Request log writer stopped before committing its write."));
        }
    }

    private async Task WriteAvailableAsync(List<PendingWrite> pending)
    {
        try
        {
            await CommitAsync(pending);
        }
        catch (RequestLogBatchRolledBackException) when (pending.Count > 1 && !_abort.IsCancellationRequested)
        {
            // A bad SQL row must not reject unrelated callers, but isolation is
            // safe only after the entire failed batch's rollback was confirmed.
            foreach (var item in pending)
            {
                try { await CommitAsync([item]); }
                catch (Exception error) { Fail(item, error); }
            }
        }
        catch (Exception error)
        {
            foreach (var item in pending) Fail(item, error);
        }
    }

    private async Task CommitAsync(List<PendingWrite> pending)
    {
        _abort.Token.ThrowIfCancellationRequested();
        var ids = await _write(pending.Select(item => item.Record).ToArray(), _abort.Token);
        if (ids.Length != pending.Count)
            throw new InvalidOperationException("Request log backend did not acknowledge every committed ID.");
        for (var index = 0; index < ids.Length; index++) pending[index].Completion.TrySetResult(ids[index]);
    }

    private static void Fail(PendingWrite pending, Exception error)
    {
        if (error is OperationCanceledException cancellation)
            pending.Completion.TrySetCanceled(cancellation.CancellationToken);
        else
            pending.Completion.TrySetException(error);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _abort.Dispose();
    }

    private sealed class PendingWrite(RequestLogRuntimeRecord record)
    {
        internal RequestLogRuntimeRecord Record { get; } = record;
        internal TaskCompletionSource<int> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
