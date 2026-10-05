using System.Text.Json;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Services;

/// <summary>Coalesced billing reads, explicit missing results, and per-read time validity.</summary>
public sealed class FusionModelCostService : IModelCostService
{
    private const ApplicationCacheDomain Domain = ApplicationCacheDomain.Costs;
    private readonly IModelCostService _inner;
    private readonly IFusionCache _cache;
    private readonly ApplicationCacheGeneration _generation;
    private readonly ApplicationCacheOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FusionModelCostService> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _positive, _negative;
    private readonly IModelMappingCacheInvalidator? _mappings;
    private readonly ICachedPricingRulesService? _rules;
    private readonly IDiscoveryCacheService? _discovery;
    public FusionModelCostService(IModelCostService inner,
        [FromKeyedServices(ApplicationCacheOptions.ServiceKey)] IFusionCache cache,
        ApplicationCacheOptions options, ApplicationCacheGeneration generation, TimeProvider clock,
        IOptions<CacheManagerOptions> legacyOptions, ILogger<FusionModelCostService> logger,
        IModelMappingCacheInvalidator? mappings = null, ICachedPricingRulesService? rules = null,
        IDiscoveryCacheService? discovery = null)
    {
        _inner = inner; _cache = cache; _options = options; _generation = generation; _clock = clock; _logger = logger;
        _mappings = mappings; _rules = rules; _discovery = discovery;
        var region = legacyOptions.Value.RegionConfigs?.GetValueOrDefault(CacheRegion.ModelCosts);
        _enabled = region?.Enabled ?? true;
        _positive = region?.DefaultTTL ?? TimeSpan.FromHours(12);
        _negative = TimeSpan.FromMinutes(1);
        if (region?.MaxTTL is { } maximum) { if (_positive > maximum) _positive = maximum; if (_negative > maximum) _negative = maximum; }
        _ = Entry(_positive); _ = Entry(_negative);
    }
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private bool Active(ModelCost value) => value.IsActive && value.EffectiveDate <= Now && (value.ExpiryDate is null || value.ExpiryDate > Now);
    private FusionCacheEntryOptions Entry(TimeSpan duration)
    {
        var entry = _options.Entry(duration);
        entry.Duration = TimeSpan.FromMilliseconds(100) < entry.Duration ? TimeSpan.FromMilliseconds(100) : entry.Duration;
        entry.EnableAutoClone = false; // detached snapshot reconstructed into an owned result on every return
        return entry;
    }
    private static bool StorageFailure(Exception ex) => ex is RedisException or TimeoutException or JsonException
        or FusionCacheDistributedCacheException or FusionCacheSerializationException or FusionCacheBackplaneException;
    private CostLookupResult Snapshot(ModelCost? value)
    {
        var now = Now;
        if (value is not null && Active(value))
            return new(CostCacheSnapshot.From(value), value.ExpiryDate is { } expiry && expiry < now + _positive ? expiry : now + _positive);
        var until = now + _negative;
        if (value?.IsActive == true && value.EffectiveDate > now && value.EffectiveDate < until) until = value.EffectiveDate;
        return new(null, until);
    }
    private async Task<ModelCost?> ReadAsync(string logicalKey, Func<CancellationToken, Task<ModelCost?>> load, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_enabled) { var value = await load(token); return value is not null && Active(value) ? value : null; }
        ModelCost? loaded = null; var didLoad = false; var businessFailed = false;
        try
        {
            var key = $"costs:{await _generation.GetAsync(Domain, token)}:{logicalKey}";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await _cache.GetOrSetAsync<CostLookupResult>(key, async (context, cancellation) =>
                {
                    try { loaded = await load(cancellation); didLoad = true; }
                    catch { businessFailed = true; throw; }
                    var snapshot = Snapshot(loaded);
                    var duration = snapshot.ValidUntil - Now;
                    if (duration <= TimeSpan.Zero) context.Options.SkipMemoryCacheWrite = context.Options.SkipDistributedCacheWrite = true;
                    else { var policy = Entry(duration); context.Options.Duration = policy.Duration; context.Options.DistributedCacheDuration = policy.DistributedCacheDuration; }
                    return snapshot;
                }, options: Entry(_positive), tags: [ApplicationCacheOptions.Tag(Domain)], token: token);
                if (result is null) throw new JsonException("Missing cost lookup contract.");
                var value = result.Value?.ToDomain();
                if (result.ValidUntil > Now && (value is null || Active(value))) return value;
                await _cache.RemoveAsync(key, Entry(_positive), token);
            }
            return loaded is not null && Active(loaded) ? loaded : null;
        }
        catch (Exception ex) when (!businessFailed && StorageFailure(ex))
        {
            _generation.RecordStorageFailure(Domain); ApplicationCacheMetrics.Bypassed(Domain);
            _logger.LogWarning(ex, "Billing cost cache unavailable; using current repository result");
            var value = didLoad ? loaded : await load(token);
            return value is not null && Active(value) ? value : null;
        }
    }
    public Task<ModelCost?> GetCostForModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return ReadAsync(CacheKeys.ModelCost.ByModelId(modelId), token => _inner.GetCostForModelAsync(modelId, token), cancellationToken);
    }
    public Task<ModelCost?> GetCostByIdAsync(int id, CancellationToken cancellationToken = default) =>
        ReadAsync(CacheKeys.ModelCost.ById(id), token => _inner.GetCostByIdAsync(id, token), cancellationToken);
    public async Task<List<ModelCost>> ListModelCostsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled) return await _inner.ListModelCostsAsync(cancellationToken);
        List<ModelCost>? loaded = null; var businessFailed = false;
        try
        {
            var key = $"costs:{await _generation.GetAsync(Domain, cancellationToken)}:{CacheKeys.ModelCost.All}";
            var result = await _cache.GetOrSetAsync<List<CostCacheSnapshot>>(key, async (_, token) =>
            {
                try { loaded = await _inner.ListModelCostsAsync(token); return loaded.Select(CostCacheSnapshot.From).ToList(); }
                catch { businessFailed = true; throw; }
            }, options: Entry(_positive), tags: [ApplicationCacheOptions.Tag(Domain)], token: cancellationToken);
            if (result is null) throw new JsonException("Missing cost list.");
            return result.Select(snapshot => snapshot.ToDomain()).ToList();
        }
        catch (Exception ex) when (!businessFailed && StorageFailure(ex))
        { _generation.RecordStorageFailure(Domain); ApplicationCacheMetrics.Bypassed(Domain); return loaded ?? await _inner.ListModelCostsAsync(cancellationToken); }
    }
    public async Task AddModelCostAsync(ModelCost value, CancellationToken cancellationToken = default)
    { await _inner.AddModelCostAsync(value, cancellationToken); await InvalidateAfterWriteAsync(cancellationToken); }
    public async Task<bool> UpdateModelCostAsync(ModelCost value, CancellationToken cancellationToken = default)
    { var changed = await _inner.UpdateModelCostAsync(value, cancellationToken); if (changed) await InvalidateAfterWriteAsync(cancellationToken); return changed; }
    public async Task<bool> DeleteModelCostAsync(int id, CancellationToken cancellationToken = default)
    { var changed = await _inner.DeleteModelCostAsync(id, cancellationToken); if (changed) await InvalidateAfterWriteAsync(cancellationToken); return changed; }
    private async Task InvalidateAfterWriteAsync(CancellationToken token)
    {
        await ClearCacheAsync(token);
        if (_mappings is not null) await _mappings.InvalidateAsync(token);
        if (_rules is not null) await _rules.InvalidateAllAsync(token);
        if (_discovery is not null) await _discovery.InvalidateAllDiscoveryAsync(token);
    }
    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        try { await _generation.InvalidateAsync(Domain, cancellationToken); await _cache.RemoveByTagAsync(ApplicationCacheOptions.Tag(Domain), token: cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ApplicationCacheMetrics.InvalidationFailed(Domain); throw new ApplicationCacheInvalidationException(Domain, ex); }
    }
}
