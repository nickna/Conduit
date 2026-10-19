using System.Text.Json;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Services;

/// <summary>Selects the cached billing shape and supported lookup policy.</summary>
public enum ModelCostCacheProfile
{
    /// <summary>Caches complete management graphs and explicit missing results.</summary>
    Complete,
    /// <summary>Caches positive native scalar lookups by ID; other reads stay direct.</summary>
    NativeRuntime
}

/// <summary>Coalesced billing reads, explicit missing results, and per-read time validity.</summary>
public sealed class FusionModelCostService : IModelCostService
{
    private const ApplicationCacheDomain Domain = ApplicationCacheDomain.Costs;
    private const string RuntimeIdKeyPrefix = "runtime-scalar-id-v1:";
    private readonly IModelCostService _inner;
    private readonly IFusionCache _cache;
    private readonly ApplicationCacheGeneration _generation;
    private readonly ApplicationCacheOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FusionModelCostService> _logger;
    private readonly bool _enabled;
    private readonly bool _runtimeReadOnly;
    private readonly TimeSpan _positive, _negative;
    private readonly FusionCacheEntryOptions _positiveEntry, _localEntry;
    private readonly IModelMappingCacheInvalidator? _mappings;
    private readonly ICachedPricingRulesService? _rules;
    private readonly IDiscoveryCacheService? _discovery;
    /// <summary>Creates the cache decorator with the selected loader and lookup profile.</summary>
    public FusionModelCostService(IModelCostService inner,
        [FromKeyedServices(ApplicationCacheOptions.ServiceKey)] IFusionCache cache,
        ApplicationCacheOptions options, ApplicationCacheGeneration generation, TimeProvider clock,
        ILogger<FusionModelCostService> logger,
        IModelMappingCacheInvalidator? mappings = null, ICachedPricingRulesService? rules = null,
        IDiscoveryCacheService? discovery = null, ModelCostCacheProfile profile = ModelCostCacheProfile.Complete)
    {
        if (profile is not ModelCostCacheProfile.Complete and not ModelCostCacheProfile.NativeRuntime)
            throw new ArgumentOutOfRangeException(nameof(profile));
        _inner = inner; _cache = cache; _options = options; _generation = generation; _clock = clock; _logger = logger;
        _mappings = mappings; _rules = rules; _discovery = discovery;
        _enabled = options.Costs.Enabled;
        _runtimeReadOnly = profile == ModelCostCacheProfile.NativeRuntime;
        _positive = options.Limit(Domain, options.Costs.Duration ?? TimeSpan.FromHours(12));
        _negative = options.Limit(Domain, TimeSpan.FromMinutes(1));
        _positiveEntry = Entry(_positive); _ = Entry(_negative);
        _localEntry = _positiveEntry.Duplicate(); _localEntry.SkipDistributedCacheRead = true;
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
        if (!_enabled) return await ReadCurrentAsync(load, token);
        ModelCost? loaded = null; var didLoad = false; var businessFailed = false;
        try
        {
            var key = $"costs:{await _generation.GetAsync(Domain, token)}:{logicalKey}";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var loadedThisAttempt = false;
                var local = await _cache.TryGetAsync<CostLookupResult>(key, _localEntry, token);
                var result = local.HasValue ? local.Value : await _cache.GetOrSetAsync<CostLookupResult>(key, async (context, cancellation) =>
                {
                    try { loaded = await load(cancellation); didLoad = loadedThisAttempt = true; }
                    catch { businessFailed = true; throw; }
                    var snapshot = Snapshot(loaded);
                    var duration = snapshot.ValidUntil - Now;
                    // Native's active-only loader hides future effective dates. A missing
                    // result must be read again rather than cached past its activation.
                    if (duration <= TimeSpan.Zero || (_runtimeReadOnly && snapshot.Value is null))
                        context.Options.SkipMemoryCacheWrite = context.Options.SkipDistributedCacheWrite = true;
                    else { var policy = Entry(duration); context.Options.Duration = policy.Duration; context.Options.DistributedCacheDuration = policy.DistributedCacheDuration; }
                    return snapshot;
                }, options: _positiveEntry, tags: [ApplicationCacheOptions.Tag(Domain)], token: token);
                if (result is null) throw new JsonException("Missing cost lookup contract.");
                if (_runtimeReadOnly && result.Value is null && !loadedThisAttempt)
                    throw new JsonException("Unexpected cached missing runtime cost.");
                var value = result.Value?.ToDomain();
                if (result.ValidUntil > Now && (value is null || Active(value))) return value;
                await _cache.RemoveAsync(key, _positiveEntry, token);
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
    /// <summary>Reads current source data without caching identifier selection or missing prices.</summary>
    private async Task<ModelCost?> ReadCurrentAsync(Func<CancellationToken, Task<ModelCost?>> load, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var value = await load(token);
        return value is not null && Active(value) ? value : null;
    }
    public Task<ModelCost?> GetCostForModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        // Identifier selection can change when another cost becomes effective.
        // The fixed-shape store does not expose that next boundary, so keep it direct.
        if (_runtimeReadOnly)
            return ReadCurrentAsync(token => _inner.GetCostForModelAsync(modelId, token), cancellationToken);
        return ReadAsync(CacheKeys.ModelCost.ByModelId(modelId), token => _inner.GetCostForModelAsync(modelId, token), cancellationToken);
    }
    public Task<ModelCost?> GetCostByIdAsync(int id, CancellationToken cancellationToken = default) =>
        ReadAsync((_runtimeReadOnly ? RuntimeIdKeyPrefix : "") + CacheKeys.ModelCost.ById(id),
            token => _inner.GetCostByIdAsync(id, token), cancellationToken);
    public async Task<List<ModelCost>> ListModelCostsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled || _runtimeReadOnly) return await _inner.ListModelCostsAsync(cancellationToken);
        List<ModelCost>? loaded = null; var businessFailed = false;
        try
        {
            var key = $"costs:{await _generation.GetAsync(Domain, cancellationToken)}:{CacheKeys.ModelCost.All}";
            var local = await _cache.TryGetAsync<List<CostCacheSnapshot>>(key, _localEntry, cancellationToken);
            var result = local.HasValue ? local.Value : await _cache.GetOrSetAsync<List<CostCacheSnapshot>>(key, async (_, token) =>
            {
                try { loaded = await _inner.ListModelCostsAsync(token); return loaded.Select(CostCacheSnapshot.From).ToList(); }
                catch { businessFailed = true; throw; }
            }, options: _positiveEntry, tags: [ApplicationCacheOptions.Tag(Domain)], token: cancellationToken);
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
