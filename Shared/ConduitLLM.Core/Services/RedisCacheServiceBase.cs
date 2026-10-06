using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using ConduitLLM.Configuration.Constants;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Base class for Redis-backed cache services providing common infrastructure:
/// get-with-fallback, stats tracking, key scanning, and cache entry management.
/// </summary>
/// <remarks>
/// Subclasses that use buffered statistics should override <see cref="TrackHitAsync"/>,
/// <see cref="TrackMissAsync"/>, and <see cref="TrackInvalidationAsync"/> to use
/// local counters with periodic flush instead of direct Redis increments.
/// </remarks>
public abstract class RedisCacheServiceBase
{
    protected readonly IDatabase Database;
    protected readonly ILogger Logger;
    protected readonly JsonSerializerOptions JsonOptions;
    protected readonly TimeSpan DefaultExpiry;

    protected RedisCacheServiceBase(
        IConnectionMultiplexer redis,
        ILogger logger,
        TimeSpan defaultExpiry,
        JsonSerializerOptions? jsonOptions = null)
    {
        Database = redis.GetDatabase();
        Logger = logger;
        DefaultExpiry = defaultExpiry;
        JsonOptions = jsonOptions ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    /// <summary>
    /// Core get-with-fallback pattern: check cache → deserialize → track hit/miss → fallback to DB → cache result.
    /// </summary>
    /// <typeparam name="T">The cached entity type</typeparam>
    /// <param name="cacheKey">Redis key to look up</param>
    /// <param name="serviceName">Stats service name for tracking</param>
    /// <param name="dbFallback">Async function to load from database on cache miss</param>
    /// <param name="cacheResult">If true, caches the DB result (default true)</param>
    /// <param name="expiry">Custom expiry, or null to use <see cref="DefaultExpiry"/></param>
    /// <param name="debugLabel">Label for debug log messages (e.g., "Global setting: AuthKey")</param>
    /// <returns>The cached or freshly-loaded entity, or null if not found</returns>
    protected async Task<T?> GetOrFallbackAsync<T>(
        string cacheKey,
        string serviceName,
        Func<Task<T?>> dbFallback,
        JsonTypeInfo<T> jsonTypeInfo,
        bool cacheResult = true,
        TimeSpan? expiry = null,
        string? debugLabel = null) where T : class
    {
        try
        {
            var cachedValue = await Database.StringGetAsync(cacheKey);

            if (cachedValue.HasValue)
            {
                var jsonString = (string?)cachedValue;
                if (jsonString is not null)
                {
                    var result = JsonSerializer.Deserialize(jsonString, jsonTypeInfo);
                    if (result != null)
                    {
                        Logger.LogDebug("Cache hit: {Label}", debugLabel ?? cacheKey);
                        await TrackHitAsync(serviceName);
                        return result;
                    }
                }
            }

            // Cache miss — load from database
            Logger.LogDebug("Cache miss, querying database: {Label}", debugLabel ?? cacheKey);
            await TrackMissAsync(serviceName);

            var dbResult = await dbFallback();

            if (dbResult != null && cacheResult)
            {
                await SetCacheEntryAsync(cacheKey, dbResult, jsonTypeInfo, expiry);
            }

            return dbResult;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error accessing cache, falling back to database: {Label}", debugLabel ?? cacheKey);
            await TrackMissAsync(serviceName);
            return await dbFallback();
        }
    }

    /// <summary>
    /// Read a cache entry with source-generated metadata.
    /// </summary>
    protected async Task<T?> TryGetCacheEntryAsync<T>(
        string cacheKey,
        JsonTypeInfo<T> jsonTypeInfo) where T : class
    {
        var cachedValue = await Database.StringGetAsync(cacheKey);
        if (!cachedValue.HasValue)
        {
            return null;
        }

        var jsonString = (string?)cachedValue;
        return jsonString is null
            ? null
            : JsonSerializer.Deserialize(jsonString, jsonTypeInfo);
    }

    /// <summary>
    /// Serialize and store a value with source-generated metadata.
    /// </summary>
    protected async Task SetCacheEntryAsync<T>(
        string cacheKey,
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        TimeSpan? expiry = null)
    {
        var json = JsonSerializer.Serialize(value, jsonTypeInfo);
        await Database.StringSetAsync(cacheKey, json, expiry ?? DefaultExpiry);
    }

    /// <summary>
    /// Scan for keys matching a pattern and delete them all.
    /// </summary>
    protected async Task ClearAllByPatternAsync(string pattern)
    {
        try
        {
            var server = Database.Multiplexer.GetServer(Database.Multiplexer.GetEndPoints()[0]);
            var keys = server.Keys(pattern: pattern);

            foreach (var key in keys)
            {
                await Database.KeyDeleteAsync(key);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error clearing cache entries by pattern: {Pattern}", pattern);
        }
    }

    /// <summary>
    /// Count matching keys by scanning the Redis keyspace.
    /// </summary>
    protected long CountEntries(string pattern)
    {
        var server = Database.Multiplexer.GetServer(Database.Multiplexer.GetEndPoints()[0]);
        var count = 0L;
        foreach (var _ in server.Keys(pattern: pattern))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Read base stats (hits, misses, invalidations, reset time) from Redis for a given service.
    /// </summary>
    protected async Task<(long hits, long misses, long invalidations, DateTime resetTime)> GetBaseStatsAsync(string serviceName)
    {
        var hits = await Database.StringGetAsync(CacheKeys.Stats.Hits(serviceName));
        var misses = await Database.StringGetAsync(CacheKeys.Stats.Misses(serviceName));
        var invalidations = await Database.StringGetAsync(CacheKeys.Stats.Invalidations(serviceName));
        var resetTime = await Database.StringGetAsync(CacheKeys.Stats.ResetTime(serviceName));

        return (
            hits.HasValue ? (long)hits : 0,
            misses.HasValue ? (long)misses : 0,
            invalidations.HasValue ? (long)invalidations : 0,
            resetTime.HasValue && DateTime.TryParse(resetTime, out var time) ? time : DateTime.UtcNow
        );
    }

    /// <summary>
    /// Initialize the stats reset time key if it doesn't already exist (fire-and-forget).
    /// Call this from the subclass constructor.
    /// </summary>
    protected void InitializeStatsResetTime(string serviceName)
    {
        _ = Database.StringSetAsync(
                CacheKeys.Stats.ResetTime(serviceName),
                DateTime.UtcNow.ToString("O"),
                when: When.NotExists)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Logger.LogWarning(t.Exception, "Failed to initialize stats reset time");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Track a cache hit. Override for buffered stats.
    /// Default: direct Redis increment.
    /// </summary>
    protected virtual Task TrackHitAsync(string serviceName)
        => Database.StringIncrementAsync(CacheKeys.Stats.Hits(serviceName));

    /// <summary>
    /// Track a cache miss. Override for buffered stats.
    /// Default: direct Redis increment.
    /// </summary>
    protected virtual Task TrackMissAsync(string serviceName)
        => Database.StringIncrementAsync(CacheKeys.Stats.Misses(serviceName));

    /// <summary>
    /// Track cache invalidation(s). Override for buffered stats.
    /// Default: direct Redis increment.
    /// </summary>
    protected virtual Task TrackInvalidationAsync(string serviceName, long count = 1)
        => Database.StringIncrementAsync(CacheKeys.Stats.Invalidations(serviceName), count);
}
