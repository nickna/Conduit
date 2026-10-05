using System.Text.Json;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Functions.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Services;

/// <summary>Scoped function policy and loaders; FusionCache owns coalescing and shared storage.</summary>
public sealed class FusionFunctionDiscoveryCacheService(
    [FromKeyedServices(ApplicationCacheOptions.ServiceKey)] IFusionCache cache,
    ApplicationCacheOptions options, ApplicationCacheGeneration generation,
    IFunctionConfigurationRepository configurations, IGlobalSettingRepository settings,
    IOptions<CacheManagerOptions> legacyOptions,
    ILogger<FusionFunctionDiscoveryCacheService> logger) : IFunctionDiscoveryCacheService
{
    private const ApplicationCacheDomain Domain = ApplicationCacheDomain.Functions;
    private long _hits, _misses, _invalidations, _lastInvalidation;
    private readonly FusionCacheEntryOptions _baseEntry = CreateEntry(options,
        legacyOptions.Value.RegionConfigs?.GetValueOrDefault(CacheRegion.FunctionDiscovery), 15);
    private readonly FusionCacheEntryOptions _localEntry = LocalEntry(options,
        legacyOptions.Value.RegionConfigs?.GetValueOrDefault(CacheRegion.FunctionDiscovery));
    private CacheRegionConfig? Region => legacyOptions.Value.RegionConfigs?.GetValueOrDefault(CacheRegion.FunctionDiscovery);
    private FusionCacheEntryOptions Entry(int minutes)
    {
        return CreateEntry(options, Region, minutes);
    }
    private static FusionCacheEntryOptions LocalEntry(ApplicationCacheOptions options, CacheRegionConfig? region)
    {
        var entry = CreateEntry(options, region, 15); entry.SkipDistributedCacheRead = true; return entry;
    }
    private static FusionCacheEntryOptions CreateEntry(ApplicationCacheOptions options, CacheRegionConfig? region, int minutes)
    {
        var duration = TimeSpan.FromMinutes(minutes);
        if (region?.MaxTTL is { } maximum && duration > maximum) duration = maximum;
        var entry = options.Entry(duration); entry.EnableAutoClone = false; return entry;
    }
    private static bool StorageFailure(Exception ex) => ex is RedisException or TimeoutException or JsonException
        or FusionCacheDistributedCacheException or FusionCacheSerializationException or FusionCacheBackplaneException;
    private static List<Tool> Copy(List<Tool> tools) => tools.Select(tool => new Tool
    {
        Type = tool.Type, Function = new FunctionDefinition { Name = tool.Function.Name,
            Description = tool.Function.Description, Parameters = (System.Text.Json.Nodes.JsonObject?)tool.Function.Parameters?.DeepClone() }
    }).ToList();
    private async ValueTask<string> KeyAsync(List<int> ids, CancellationToken token) =>
        $"functions:{await generation.GetAsync(Domain, token)}:configs:{string.Join(',', ids.Distinct().Order())}";

    public async Task<bool> IsCachingEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Region?.Enabled == false) return false;
        try
        {
            var setting = await settings.GetByKeyAsync("Functions.DiscoveryCacheEnabled", cancellationToken);
            return setting?.Value?.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "enabled";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Function cache enable lookup failed; bypassing"); return false; }
    }

    public async Task<List<Tool>> GetOrLoadAsync(List<int> ids,
        Func<CancellationToken, Task<FunctionDiscoveryLoad>> load, int? ttlMinutes = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Count == 0 || !await IsCachingEnabledAsync(cancellationToken))
            return (await load(cancellationToken)).Tools;
        FunctionDiscoveryLoad? loaded = null;
        var businessFailed = false;
        try
        {
            var key = await KeyAsync(ids, cancellationToken);
            var local = await cache.TryGetAsync<List<Tool>>(key, _localEntry, cancellationToken);
            var result = local.HasValue ? local.Value : await cache.GetOrSetAsync<List<Tool>>(key, async (context, token) =>
            {
                try { loaded = await load(token); }
                catch { businessFailed = true; throw; }
                if ((ttlMinutes ?? loaded.TtlMinutes) is { } minutes)
                {
                    var policy = Entry(minutes);
                    context.Options.Duration = policy.Duration;
                    context.Options.DistributedCacheDuration = policy.DistributedCacheDuration;
                }
                else { context.Options.SkipMemoryCacheWrite = context.Options.SkipDistributedCacheWrite = true; context.Options.SkipBackplaneNotifications = true; }
                return Copy(loaded.Tools);
            }, options: _baseEntry, tags: [ApplicationCacheOptions.Tag(Domain)], token: cancellationToken);
            if (loaded is null) Interlocked.Increment(ref _hits);
            else Interlocked.Increment(ref _misses);
            return Copy(result);
        }
        catch (Exception ex) when (!businessFailed && StorageFailure(ex))
        {
            logger.LogWarning(ex, "Function cache unavailable; serving current tools");
            ApplicationCacheMetrics.Bypassed(Domain);
            return loaded?.Tools ?? (await load(cancellationToken)).Tools;
        }
    }

    public async Task<List<Tool>?> GetCachedToolsAsync(List<int> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0 || !await IsCachingEnabledAsync(cancellationToken)) return null;
        try
        {
            var result = await cache.TryGetAsync<List<Tool>>(await KeyAsync(ids, cancellationToken), _baseEntry, cancellationToken);
            return result.HasValue ? Copy(result.Value) : null;
        }
        catch (Exception ex) when (StorageFailure(ex))
        { logger.LogWarning(ex, "Function cache read failed"); return null; }
    }

    public async Task SetCachedToolsAsync(List<int> ids, List<Tool> tools, int? ttlMinutes = null,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0 || !await IsCachingEnabledAsync(cancellationToken)) return;
        // Compatibility writer only. Production loaders calculate this from their already loaded configurations.
        if (ttlMinutes is null)
        {
            var configs = await configurations.GetByIdsAsync(ids, cancellationToken);
            ttlMinutes = configs.Where(config => config.CacheTtlMinutes.HasValue).Select(config => config.CacheTtlMinutes).Min();
        }
        if (ttlMinutes is null) return;
        try
        {
            await cache.SetAsync(await KeyAsync(ids, cancellationToken), Copy(tools), Entry(ttlMinutes.Value),
                [ApplicationCacheOptions.Tag(Domain)], cancellationToken);
        }
        catch (Exception ex) when (StorageFailure(ex)) { logger.LogWarning(ex, "Function cache write failed"); }
    }

    public async Task InvalidateAllFunctionDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await generation.InvalidateAsync(Domain, cancellationToken);
            await cache.RemoveByTagAsync(ApplicationCacheOptions.Tag(Domain), token: cancellationToken);
            Interlocked.Increment(ref _invalidations);
            Interlocked.Exchange(ref _lastInvalidation, DateTime.UtcNow.Ticks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ApplicationCacheMetrics.InvalidationFailed(Domain); throw new ApplicationCacheInvalidationException(Domain, ex); }
    }
    public Task InvalidateFunctionConfigurationAsync(int id, CancellationToken cancellationToken = default) =>
        InvalidateAllFunctionDiscoveryAsync(cancellationToken);
    public async Task<CacheStats> GetStatisticsAsync(CancellationToken cancellationToken = default) => new()
    {
        HitCount = Interlocked.Read(ref _hits), MissCount = Interlocked.Read(ref _misses),
        InvalidationCount = Interlocked.Read(ref _invalidations), EntryCount = 0,
        LastInvalidationTime = _lastInvalidation == 0 ? null : new DateTime(_lastInvalidation, DateTimeKind.Utc),
        IsEnabled = await IsCachingEnabledAsync(cancellationToken)
    };
}
