using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Services;

/// <summary>Discovery variants share one factory path and an explicit discovery invalidation dependency.</summary>
public sealed class FusionDiscoveryCacheService : IDiscoveryCacheService
{
    private const ApplicationCacheDomain Domain = ApplicationCacheDomain.Discovery;
    private readonly IFusionCache _cache;
    private readonly ApplicationCacheGeneration _generation;
    private readonly FusionCacheEntryOptions _entry;
    private readonly ILogger<FusionDiscoveryCacheService> _logger;
    private readonly bool _enabled;
    private long _hits, _misses, _invalidations;
    private long _lastInvalidationTicks;

    public FusionDiscoveryCacheService(
        [FromKeyedServices(ApplicationCacheOptions.ServiceKey)] IFusionCache cache,
        ApplicationCacheOptions applicationOptions,
        ApplicationCacheGeneration generation,
        IOptions<DiscoveryCacheOptions> options, IOptions<CacheManagerOptions> legacyOptions,
        ILogger<FusionDiscoveryCacheService> logger)
    {
        _cache = cache;
        _generation = generation;
        _logger = logger;
        CacheRegionConfig? region = null;
        legacyOptions.Value.RegionConfigs?.TryGetValue(CacheRegion.ModelDiscovery, out region);
        _enabled = options.Value.EnableCaching && (region?.Enabled ?? true);
        var duration = TimeSpan.FromMinutes(options.Value.CacheDurationMinutes);
        if (region?.MaxTTL is { } maximum && duration > maximum) duration = maximum;
        _entry = applicationOptions.Entry(duration);
        // JsonElement is immutable. Detach incoming documents once, then copy the mutable list on reads.
        // This preserves ownership without serializing the complete discovery response on every L1 hit.
        _entry.EnableAutoClone = false;
    }

    private async ValueTask<string> KeyAsync(string key, CancellationToken token) =>
        $"discovery:{await _generation.GetAsync(Domain, token)}:{key}";
    private static DiscoveryModelsResult Copy(DiscoveryModelsResult value, bool detach = false) => new()
    {
        Count = value.Count, CapabilityFilter = value.CapabilityFilter,
        CachedAt = detach ? DateTime.UtcNow : value.CachedAt,
        Data = detach ? value.Data.Select(element => element.Clone()).ToList() : [.. value.Data]
    };

    private static bool CacheFailure(Exception exception) => exception is RedisException or JsonException
        or TimeoutException or FusionCacheDistributedCacheException or FusionCacheSerializationException or FusionCacheBackplaneException;

    public async Task<DiscoveryModelsResult> GetOrLoadAsync(string cacheKey,
        Func<CancellationToken, Task<DiscoveryModelsResult>> load, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled)
        {
            ApplicationCacheMetrics.Bypassed(Domain);
            return await load(cancellationToken);
        }
        DiscoveryModelsResult? loaded = null;
        var factoryFailed = false;
        try
        {
            var key = await KeyAsync(cacheKey, cancellationToken);
            var result = await _cache.GetOrSetAsync<DiscoveryModelsResult>(key, async (_, token) =>
            {
                try { loaded = await load(token); }
                catch { factoryFailed = true; throw; }
                return Copy(loaded, detach: true);
            }, options: _entry, tags: [ApplicationCacheOptions.Tag(Domain)], token: cancellationToken);
            if (loaded is null) Interlocked.Increment(ref _hits);
            else Interlocked.Increment(ref _misses);
            return Copy(result);
        }
        catch (Exception exception) when (!factoryFailed && CacheFailure(exception))
        {
            // A read failure may bypass cache; a failed write must not execute a successful business load twice.
            _logger.LogWarning(exception, "Discovery cache unavailable; serving the current load");
            ApplicationCacheMetrics.Bypassed(Domain);
            Interlocked.Increment(ref _misses);
            return loaded ?? await load(cancellationToken);
        }
    }

    public async Task<DiscoveryModelsResult?> GetDiscoveryResultsAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled) return null;
        try
        {
            var key = await KeyAsync(cacheKey, cancellationToken);
            var result = await _cache.TryGetAsync<DiscoveryModelsResult>(key, _entry, cancellationToken);
            if (result.HasValue) Interlocked.Increment(ref _hits);
            else Interlocked.Increment(ref _misses);
            return result.HasValue ? Copy(result.Value) : null;
        }
        catch (Exception exception) when (CacheFailure(exception))
        {
            _logger.LogWarning(exception, "Discovery cache read failed");
            ApplicationCacheMetrics.Bypassed(Domain);
            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    public async Task SetDiscoveryResultsAsync(string cacheKey, DiscoveryModelsResult results, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled) return;
        try
        {
            var key = await KeyAsync(cacheKey, cancellationToken);
            await _cache.SetAsync(key, Copy(results, detach: true), _entry, [ApplicationCacheOptions.Tag(Domain)], cancellationToken);
        }
        catch (Exception exception) when (CacheFailure(exception))
        {
            _logger.LogWarning(exception, "Discovery cache write failed");
        }
    }

    public async Task InvalidateAllDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Invalidation remains active when reads are disabled, and failures reach durable message retry.
            await _generation.InvalidateAsync(Domain, cancellationToken);
            await _cache.RemoveByTagAsync(ApplicationCacheOptions.Tag(Domain), token: cancellationToken);
            Interlocked.Increment(ref _invalidations);
            Interlocked.Exchange(ref _lastInvalidationTicks, DateTime.UtcNow.Ticks);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplicationCacheMetrics.InvalidationFailed(Domain);
            throw new ApplicationCacheInvalidationException(Domain, exception);
        }
    }

    // Existing wildcard dependencies are broad; no narrower dependency has been proven for these callers.
    public Task InvalidatePatternAsync(string pattern, CancellationToken cancellationToken = default) =>
        InvalidateAllDiscoveryAsync(cancellationToken);

    public Task<CacheStats> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ticks = Interlocked.Read(ref _lastInvalidationTicks);
        return Task.FromResult(new CacheStats
        {
            HitCount = Interlocked.Read(ref _hits), MissCount = Interlocked.Read(ref _misses),
            InvalidationCount = Interlocked.Read(ref _invalidations), EntryCount = 0,
            LastInvalidationTime = ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc)
        });
    }
}
