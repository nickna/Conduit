using ConduitLLM.Configuration.Constants;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace ConduitLLM.Gateway.Services;

public partial class RedisVirtualKeyCache
{
    private readonly object _callbackSync = new();
    private readonly HashSet<Task> _callbackTasks = [];
    private readonly CancellationTokenSource _callbackCancellation = new();
    private bool _callbacksClosed;
    private Task? _shutdownTask;
    private int _disposed;

    private void TrackCallback(Func<CancellationToken, Task> callback)
    {
        lock (_callbackSync)
        {
            if (_callbacksClosed) return;
            var task = callback(_callbackCancellation.Token);
            _callbackTasks.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_callbackSync) _callbackTasks.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task DeleteCallbackKeyAsync(string keyHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // StackExchange.Redis has no command cancellation. Join the issued delete,
        // then observe cancellation before the callback can do any subsequent work.
        await Database.KeyDeleteAsync(CacheKeys.VirtualKey.ByHash(keyHash));
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        lock (_callbackSync)
        {
            if (_disposed != 0) return _shutdownTask ??= JoinCallbacksAsync();
            _callbacksClosed = true;
            _callbackCancellation.Cancel();
            return _shutdownTask ??= ShutdownCoreAsync(cancellationToken);
        }
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UnsubscribeAsync(RedisChannel.Literal(CacheKeys.VirtualKey.InvalidationChannel), OnKeyInvalidated, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await UnsubscribeAsync(RedisChannel.Literal(CacheKeys.VirtualKey.BatchInvalidationChannel), OnBatchInvalidated, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            // Closing admission before unsubscribe also handles a concurrently
            // delivered message. Never detach commands when shutdown is canceled.
            await JoinCallbacksAsync();
        }
    }

    private Task UnsubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue> handler,
        CancellationToken cancellationToken)
    {
        lock (_callbackSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _disposed != 0 ? Task.CompletedTask : _subscriber.UnsubscribeAsync(channel, handler);
        }
    }

    private Task JoinCallbacksAsync()
    {
        lock (_callbackSync) return Task.WhenAll(_callbackTasks.ToArray());
    }

    public void Dispose()
    {
        lock (_callbackSync)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            _callbacksClosed = true;
            _callbackCancellation.Cancel();
            if (_callbackTasks.Count == 0) _callbackCancellation.Dispose();
        }
        // Unsubscribe belongs to hosted shutdown, while tracing is still alive.
    }
}

/// <summary>Borrowed cache lifetime; registered before Batch so its final publish is accepted.</summary>
public sealed class RedisVirtualKeyCacheLifecycleHostedService(RedisVirtualKeyCache? cache) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => cache?.ShutdownAsync(cancellationToken) ?? Task.CompletedTask;
}
