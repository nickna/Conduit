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

public sealed class FusionModelProviderMappingService : IModelProviderMappingService
{
    private readonly IModelProviderMappingService _inner;
    private readonly IFusionCache _cache;
    private readonly ApplicationCacheGeneration _generation;
    private readonly IModelMappingCacheInvalidator _invalidation;
    private readonly ILogger<FusionModelProviderMappingService> _logger;
    private readonly FusionCacheEntryOptions _entry;
    private readonly FusionCacheEntryOptions _localEntry;
    private readonly bool _enabled;
    private const ApplicationCacheDomain Domain = ApplicationCacheDomain.Mappings;

    public FusionModelProviderMappingService(IModelProviderMappingService inner,
        [FromKeyedServices(ApplicationCacheOptions.ServiceKey)] IFusionCache cache,
        ApplicationCacheOptions options, ApplicationCacheGeneration generation, IModelMappingCacheInvalidator invalidation,
        IOptions<CacheManagerOptions> legacyOptions, ILogger<FusionModelProviderMappingService> logger)
    {
        _inner = inner; _cache = cache; _generation = generation; _invalidation = invalidation; _logger = logger;
        var region = legacyOptions.Value.RegionConfigs?.GetValueOrDefault(CacheRegion.ModelMetadata);
        _enabled = region?.Enabled ?? true;
        var duration = TimeSpan.FromMinutes(10);
        if (region?.MaxTTL is { } maximum && duration > maximum) duration = maximum;
        _entry = options.Entry(duration);
        _entry.Duration = TimeSpan.FromMilliseconds(100) < _entry.Duration ? TimeSpan.FromMilliseconds(100) : _entry.Duration;
        _entry.EnableAutoClone = false; // immutable detached snapshots are reconstructed as owned graphs on every return
        _localEntry = _entry.Duplicate(); _localEntry.SkipDistributedCacheRead = true;
    }
    private static bool StorageFailure(Exception ex) => ex is RedisException or TimeoutException or JsonException
        or FusionCacheDistributedCacheException or FusionCacheSerializationException or FusionCacheBackplaneException;

    private async Task<List<ModelProviderMapping>> ReadAsync(string key, Func<Task<List<ModelProviderMapping>>> load, bool cacheEmpty = true)
    {
        if (!_enabled) return await load();
        List<ModelProviderMapping>? loaded = null;
        var businessFailed = false;
        try
        {
            var generation = await _generation.GetAsync(Domain);
            var cacheKey = $"mappings:{generation}:{key}";
            var local = await _cache.TryGetAsync<List<MappingCacheSnapshot>>(cacheKey, _localEntry);
            var snapshots = local.HasValue ? local.Value : await _cache.GetOrSetAsync<List<MappingCacheSnapshot>>(cacheKey, async (context, _) =>
            {
                try
                {
                    loaded = await load();
                    if (!cacheEmpty && loaded.Count == 0) context.Options.SkipMemoryCacheWrite = context.Options.SkipDistributedCacheWrite = true;
                    return loaded.Select(MappingCacheSnapshot.From).ToList();
                }
                catch { businessFailed = true; throw; }
            }, options: _entry, tags: [ApplicationCacheOptions.Tag(Domain)]);
            if (snapshots is null) throw new JsonException("Missing mapping snapshot list.");
            return snapshots.Select(snapshot => snapshot.ToDomain()).ToList();
        }
        catch (Exception ex) when (!businessFailed && StorageFailure(ex))
        { _generation.RecordStorageFailure(Domain); _logger.LogWarning(ex, "Mapping cache unavailable; loading current routing graph"); ApplicationCacheMetrics.Bypassed(Domain); return loaded ?? await load(); }
    }

    private async Task<ModelProviderMapping?> SingleAsync(string key, Func<Task<ModelProviderMapping?>> load) =>
        (await ReadAsync(key, async () => await load() is { } value ? [value] : [], cacheEmpty: false)).SingleOrDefault();
    public Task<ModelProviderMapping?> GetMappingByIdAsync(int id) => SingleAsync(CacheKeys.ModelMapping.ById(id), () => _inner.GetMappingByIdAsync(id));
    public Task<ModelProviderMapping?> GetMappingByModelAliasAsync(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        return SingleAsync(CacheKeys.ModelMapping.ByAlias(alias), () => _inner.GetMappingByModelAliasAsync(alias));
    }
    public Task<List<ModelProviderMapping>> GetMappingsByModelAliasAsync(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        return ReadAsync(CacheKeys.ModelMapping.ByAlias(alias) + ":all", () => _inner.GetMappingsByModelAliasAsync(alias));
    }
    public Task<List<ModelProviderMapping>> GetAllMappingsAsync() => ReadAsync(CacheKeys.ModelMapping.AllMappings, _inner.GetAllMappingsAsync);
    public async Task AddMappingAsync(ModelProviderMapping mapping) { await _inner.AddMappingAsync(mapping); await _invalidation.InvalidateAsync(); }
    public async Task UpdateMappingAsync(ModelProviderMapping mapping) { await _inner.UpdateMappingAsync(mapping); await _invalidation.InvalidateAsync(); }
    public async Task DeleteMappingAsync(int id) { await _inner.DeleteMappingAsync(id); await _invalidation.InvalidateAsync(); }
    public async Task<(bool success, string? errorMessage, ModelProviderMapping? createdMapping)> ValidateAndCreateMappingAsync(ModelProviderMapping mapping)
    {
        var result = await _inner.ValidateAndCreateMappingAsync(mapping);
        if (result.success) await _invalidation.InvalidateAsync();
        return result;
    }
    public async Task<(bool success, string? errorMessage)> ValidateAndUpdateMappingAsync(int id, ModelProviderMapping mapping)
    {
        var result = await _inner.ValidateAndUpdateMappingAsync(id, mapping);
        if (result.success) await _invalidation.InvalidateAsync();
        return result;
    }
    public Task<bool> ProviderExistsByIdAsync(int id) => _inner.ProviderExistsByIdAsync(id);
    public Task<List<(int Id, string ProviderName)>> GetAvailableProvidersAsync() => _inner.GetAvailableProvidersAsync();
}
