using System.Collections.Concurrent;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Caching;

/// <summary>Five bounded invalidation tokens, independent of client clocks. FusionCache still owns payload caching.</summary>
public sealed class ApplicationCacheGeneration : IDisposable
{
    private readonly IFusionCache _cache;
    private readonly ApplicationCacheOptions _options;
    private readonly ConfigurationOptions? _redis;
    private readonly ConcurrentDictionary<ApplicationCacheDomain, string> _local = new();
    private readonly object _gate = new();
    private Task<ConnectionMultiplexer>? _connection;
    private bool _disposed;

    public ApplicationCacheGeneration(IFusionCache cache, ApplicationCacheOptions options, string? redis)
    {
        _cache = cache;
        _options = options;
        if (!string.IsNullOrWhiteSpace(redis))
        {
            _redis = ConfigurationOptions.Parse(redis);
            _redis.AbortOnConnectFail = false;
            _redis.ConnectTimeout = Math.Min(_redis.ConnectTimeout, Math.Max(1, (int)options.DistributedReadTimeout.TotalMilliseconds));
            _redis.ConnectRetry = 0;
        }
    }

    private static string Key(ApplicationCacheDomain domain) => $"generation:{ApplicationCacheOptions.Tag(domain)}";
    private string RedisKey(ApplicationCacheDomain domain) => _options.Prefix + Key(domain);
    private FusionCacheEntryOptions Entry(ApplicationCacheDomain domain, bool notify = false)
    {
        var duration = domain is ApplicationCacheDomain.Mappings or ApplicationCacheDomain.Costs or ApplicationCacheDomain.PricingRules
            ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        var entry = _options.Entry(duration);
        entry.EnableAutoClone = false; // immutable string
        entry.SkipDistributedCacheRead = entry.SkipDistributedCacheWrite = true;
        entry.SkipBackplaneNotifications = !notify;
        return entry;
    }

    private async Task<IConnectionMultiplexer> ConnectionAsync(CancellationToken token)
    {
        Task<ConnectionMultiplexer> connection;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            connection = _connection ??= ConnectionMultiplexer.ConnectAsync(_redis!);
        }
        var result = await connection.WaitAsync(_options.DistributedReadTimeout, token).ConfigureAwait(false);
        if (!result.IsConnected) throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Application cache generation store is disconnected.");
        return result;
    }

    public async ValueTask<string> GetAsync(ApplicationCacheDomain domain, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        IConnectionMultiplexer? connection = null;
        try
        {
            // Never use a cached generation once this connection has observed a disconnect.
            if (_redis is not null) connection = await ConnectionAsync(token).ConfigureAwait(false);
            return await _cache.GetOrSetAsync<string>(Key(domain), async (_, cancellation) =>
            {
                if (connection is null) return _local.GetOrAdd(domain, _ => Guid.NewGuid().ToString("N"));
                var database = connection.GetDatabase();
                var key = RedisKey(domain);
                var value = await database.StringGetAsync(key).WaitAsync(_options.DistributedReadTimeout, cancellation).ConfigureAwait(false);
                if (!value.IsNull) return value.ToString();
                // Initialization must not overwrite a concurrently published invalidation.
                await database.StringSetAsync(key, Guid.NewGuid().ToString("N"), when: When.NotExists)
                    .WaitAsync(_options.DistributedReadTimeout, cancellation).ConfigureAwait(false);
                value = await database.StringGetAsync(key).WaitAsync(_options.DistributedReadTimeout, cancellation).ConfigureAwait(false);
                if (value.IsNull) throw new RedisException("Application cache generation initialization was lost.");
                return value.ToString();
            }, options: Entry(domain), tags: [], token: token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplicationCacheMetrics.RedisFailure("generation_read");
            throw;
        }
    }

    public async Task InvalidateAsync(ApplicationCacheDomain domain, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var generation = Guid.NewGuid().ToString("N");
        try
        {
            if (_redis is null) _local[domain] = generation;
            else
            {
                var connection = await ConnectionAsync(token).ConfigureAwait(false);
                // Persistent, clock-independent metadata: one key per domain, no TTL or shared-store flush.
                await connection.GetDatabase().StringSetAsync(RedisKey(domain), generation).WaitAsync(token).ConfigureAwait(false);
            }
            await _cache.RemoveAsync(Key(domain), Entry(domain, notify: true), token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplicationCacheMetrics.RedisFailure("generation_write");
            throw;
        }
    }

    public void Dispose()
    {
        Task<ConnectionMultiplexer>? connection;
        lock (_gate) { if (_disposed) return; _disposed = true; connection = _connection; }
        if (connection is null) return;
        // DI can dispose while the bounded initial read has timed out; dispose its eventual connection, too.
        _ = connection.ContinueWith(static completed =>
        {
            if (completed.IsCompletedSuccessfully) completed.Result.Dispose();
            else _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
