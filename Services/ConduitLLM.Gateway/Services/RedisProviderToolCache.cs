using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Serialization;
using StackExchange.Redis;

namespace ConduitLLM.Gateway.Services;

/// <summary>
/// Redis-backed cache for provider tool lookups in the billing pipeline.
/// Caches active tools per provider type to eliminate per-request database queries.
/// </summary>
public class RedisProviderToolCache : BufferedStatsRedisCacheBase, IProviderToolCache
{
    private readonly ISubscriber _subscriber;

    protected override string ServiceName => CacheKeys.Stats.ProviderToolService;

    public RedisProviderToolCache(
        IConnectionMultiplexer redis,
        ILogger<RedisProviderToolCache> logger)
        : base(redis, logger, TimeSpan.FromHours(1), ConduitLLM.Core.Serialization.ConduitJsonOptions.Compact)
    {
        _subscriber = redis.GetSubscriber();

        // Subscribe to invalidation channel for cross-instance cache consistency
        _subscriber.Subscribe(
            RedisChannel.Literal(CacheKeys.ProviderTool.InvalidationChannel),
            OnToolInvalidated);

        Logger.LogDebug("RedisProviderToolCache initialized with {Expiry} TTL", DefaultExpiry);
    }

    /// <inheritdoc/>
    public async Task<List<ProviderTool>> GetActiveToolsForProviderAsync(
        ProviderType providerType,
        Func<ProviderType, Task<List<ProviderTool>>> databaseFallback)
    {
        var cacheKey = CacheKeys.ProviderTool.ByProvider(providerType.ToString());

        var result = await GetOrFallbackAsync<List<ProviderTool>>(
            cacheKey,
            ServiceName,
            async () => await databaseFallback(providerType) as List<ProviderTool>,
            GatewayRedisJsonContext.Default.ListProviderTool,
            debugLabel: $"Provider tools for {providerType}");

        return result ?? new List<ProviderTool>();
    }

    /// <inheritdoc/>
    public async Task InvalidateProviderAsync(ProviderType providerType)
    {
        try
        {
            var cacheKey = CacheKeys.ProviderTool.ByProvider(providerType.ToString());
            await Database.KeyDeleteAsync(cacheKey);
            await TrackInvalidationAsync(ServiceName);
            Logger.LogDebug("Provider tool cache invalidated for {ProviderType}", providerType);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error invalidating provider tool cache for {ProviderType}", providerType);
        }
    }

    /// <inheritdoc/>
    public async Task ClearAllAsync()
    {
        await ClearAllByPatternAsync(CacheKeys.ProviderTool.Prefix + "*");
        Logger.LogWarning("All provider tool cache entries cleared");
    }

    /// <inheritdoc/>
    public async Task<CacheStats> GetStatsAsync()
    {
        try
        {
            var (hits, misses, invalidations, resetTime) = await GetBaseStatsAsync(ServiceName);

            return new CacheStats
            {
                HitCount = hits + PendingHits,
                MissCount = misses + PendingMisses,
                InvalidationCount = invalidations + PendingInvalidations,
                LastResetTime = resetTime,
                EntryCount = CountEntries(CacheKeys.ProviderTool.Prefix + "*")
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting provider tool cache statistics");
            return new CacheStats { LastResetTime = DateTime.UtcNow };
        }
    }

    #region Pub/Sub

    private void OnToolInvalidated(RedisChannel channel, RedisValue message)
    {
        _ = OnToolInvalidatedAsync(message);
    }

    private async Task OnToolInvalidatedAsync(RedisValue message)
    {
        try
        {
            var providerTypeStr = message.ToString();
            if (providerTypeStr == "*")
            {
                await ClearAllAsync();
            }
            else if (Enum.TryParse<ProviderType>(providerTypeStr, true, out var providerType))
            {
                await InvalidateProviderAsync(providerType);
                Logger.LogDebug("Invalidated provider tool cache from pub/sub: {ProviderType}", providerType);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling provider tool cache invalidation from pub/sub: {Message}",
                message.ToString());
        }
    }

    #endregion
}
