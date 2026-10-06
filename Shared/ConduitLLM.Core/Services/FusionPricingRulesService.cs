using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Core.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Services;

public sealed class FusionPricingRulesService : ICachedPricingRulesService
{
    private const ApplicationCacheDomain Domain = ApplicationCacheDomain.PricingRules;
    private readonly IFusionCache _cache;
    private readonly ApplicationCacheGeneration _generation;
    private readonly ILogger<FusionPricingRulesService> _logger;
    private readonly FusionCacheEntryOptions _entry;
    private readonly FusionCacheEntryOptions _localEntry;
    private readonly bool _enabled;
    private static readonly CorePricingJsonContext JsonContext = new(new JsonSerializerOptions
    { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    public FusionPricingRulesService([FromKeyedServices(ApplicationCacheOptions.ServiceKey)] IFusionCache cache,
        ApplicationCacheOptions options, ApplicationCacheGeneration generation,
        ILogger<FusionPricingRulesService> logger)
    {
        _cache = cache; _generation = generation; _logger = logger;
        _enabled = options.PricingRules.Enabled;
        var duration = options.Limit(Domain, options.PricingRules.Duration ?? TimeSpan.FromMinutes(15));
        _entry = options.Entry(duration);
        _entry.Duration = TimeSpan.FromMilliseconds(100) < _entry.Duration ? TimeSpan.FromMilliseconds(100) : _entry.Duration;
        _entry.EnableAutoClone = false;
        _localEntry = _entry.Duplicate(); _localEntry.SkipDistributedCacheRead = true;
    }
    private static string Fingerprint(string json)
    {
        var length = Encoding.UTF8.GetByteCount(json);
        Span<byte> bytes = length <= 1024 ? stackalloc byte[length] : new byte[length];
        Encoding.UTF8.GetBytes(json, bytes);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash);
    }
    private PricingRulesConfig? Parse(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize(json, JsonContext.PricingRulesConfig);
            return result?.Rules is not null && result.Rules.All(rule => rule is not null && rule.Conditions is not null) ? result : null;
        }
        catch (JsonException ex) { _logger.LogWarning(ex, "Invalid pricing rule configuration"); return null; }
    }
    private static PricingRulesConfig Copy(PricingRulesConfig value)
    {
        if (value.Rules is null || value.Rules.Any(rule => rule?.Conditions is null)) throw new JsonException("Incomplete pricing rule cache contract.");
        return new()
        {
            Version = value.Version, PricingType = value.PricingType, UnitField = value.UnitField, DefaultRate = value.DefaultRate,
            Rules = value.Rules.Select(rule => new PricingRule { Rate = rule.Rate, Priority = rule.Priority,
                Description = rule.Description, Conditions = new(rule.Conditions) }).ToList(),
            Constraints = value.Constraints is null ? null : new PricingConstraints
            {
                MinDuration = value.Constraints.MinDuration, MaxDuration = value.Constraints.MaxDuration,
                MinSteps = value.Constraints.MinSteps, MaxSteps = value.Constraints.MaxSteps,
                AllowedResolutions = value.Constraints.AllowedResolutions is null ? null : [.. value.Constraints.AllowedResolutions]
            }
        };
    }
    private static bool StorageFailure(Exception ex) => ex is RedisException or TimeoutException or JsonException
        or FusionCacheDistributedCacheException or FusionCacheSerializationException or FusionCacheBackplaneException;
    public async Task<PricingRulesConfig?> GetConfigAsync(int modelCostId, string pricingConfiguration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(pricingConfiguration)) return null;
        if (!_enabled) return Parse(pricingConfiguration);
        PricingRulesConfig? parsed = null; var didParse = false;
        try
        {
            var key = $"rules:{await _generation.GetAsync(Domain, cancellationToken)}:{modelCostId}:{Fingerprint(pricingConfiguration)}";
            var local = await _cache.TryGetAsync<PricingRulesConfig?>(key, _localEntry, cancellationToken);
            var result = local.HasValue ? local.Value : await _cache.GetOrSetAsync<PricingRulesConfig?>(key, (context, _) =>
            {
                parsed = Parse(pricingConfiguration); didParse = true;
                if (parsed is null)
                {
                    context.Options.SkipMemoryCacheWrite = context.Options.SkipDistributedCacheWrite = true;
                    context.Options.SkipBackplaneNotifications = true;
                }
                return Task.FromResult(parsed);
            }, options: _entry, tags: [ApplicationCacheOptions.Tag(Domain)], token: cancellationToken);
            return result is null ? null : Copy(result);
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            _generation.RecordStorageFailure(Domain); ApplicationCacheMetrics.Bypassed(Domain);
            _logger.LogWarning(ex, "Pricing rule cache unavailable; parsing current configuration");
            return didParse ? (parsed is null ? null : Copy(parsed)) : Parse(pricingConfiguration);
        }
    }
    // Content-addressed variants and generation fencing deliberately use broad dependency expiration.
    public Task InvalidateCacheAsync(int modelCostId, CancellationToken cancellationToken = default) => InvalidateAllAsync(cancellationToken);
    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        try { await _generation.InvalidateAsync(Domain, cancellationToken); await _cache.RemoveByTagAsync(ApplicationCacheOptions.Tag(Domain), token: cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ApplicationCacheMetrics.InvalidationFailed(Domain); throw new ApplicationCacheInvalidationException(Domain, ex); }
    }
}
