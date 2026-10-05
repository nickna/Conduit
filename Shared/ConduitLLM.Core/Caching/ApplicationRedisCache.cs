using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Core.Caching;

// Storage adapter only: records Redis errors without swallowing them or owning cache policy.
internal sealed class ApplicationRedisCache(string connectionString) : IDistributedCache, IDisposable
{
    private readonly RedisCache _inner = new(Microsoft.Extensions.Options.Options.Create(new RedisCacheOptions { Configuration = connectionString }));

    private static T Observe<T>(Func<T> operation, string name)
    {
        try { return operation(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplicationCacheMetrics.RedisFailure(name);
            throw;
        }
    }

    private static async Task<T> ObserveAsync<T>(Func<Task<T>> operation, string name)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplicationCacheMetrics.RedisFailure(name);
            throw;
        }
    }

    private static Task ObserveAsync(Func<Task> operation, string name) => ObserveAsync(async () => { await operation().ConfigureAwait(false); return true; }, name);
    public byte[]? Get(string key) => Observe(() => _inner.Get(key), "redis_read");
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => ObserveAsync(() => _inner.GetAsync(key, token), "redis_read");
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Observe(() => { _inner.Set(key, value, options); return true; }, "redis_write");
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => ObserveAsync(() => _inner.SetAsync(key, value, options, token), "redis_write");
    public void Remove(string key) => Observe(() => { _inner.Remove(key); return true; }, "redis_remove");
    public Task RemoveAsync(string key, CancellationToken token = default) => ObserveAsync(() => _inner.RemoveAsync(key, token), "redis_remove");
    public void Refresh(string key) => Observe(() => { _inner.Refresh(key); return true; }, "redis_refresh");
    public Task RefreshAsync(string key, CancellationToken token = default) => ObserveAsync(() => _inner.RefreshAsync(key, token), "redis_refresh");
    public void Dispose() => _inner.Dispose();
}
