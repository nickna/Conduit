using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Utilities;

/// <summary>
/// Provides a consistent L1 memory/L2 distributed cache cascade for shared services.
/// </summary>
public sealed class HybridCacheAccessor
{
    private static readonly ConcurrentDictionary<string, byte> KnownCacheKeys = new();

    private readonly IMemoryCache _memoryCache;
    private readonly IDistributedCache? _distributedCache;
    private readonly ILogger _logger;
    private readonly string _keyPrefix;
    private readonly TimeSpan _memoryExpiration;
    private readonly TimeSpan _distributedExpiration;

    public HybridCacheAccessor(
        IMemoryCache memoryCache,
        IDistributedCache? distributedCache,
        ILogger logger,
        string keyPrefix,
        TimeSpan memoryExpiration,
        TimeSpan distributedExpiration)
    {
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _distributedCache = distributedCache;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyPrefix = string.IsNullOrWhiteSpace(keyPrefix)
            ? throw new ArgumentException("A cache key prefix is required.", nameof(keyPrefix))
            : keyPrefix;
        _memoryExpiration = memoryExpiration;
        _distributedExpiration = distributedExpiration;
    }

    public async Task<T?> GetAsync<T>(
        string key,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        var fullKey = BuildKey(key);
        KnownCacheKeys.TryAdd(fullKey, 0);

        if (_memoryCache.TryGetValue(fullKey, out T? memoryValue))
        {
            _logger.LogDebug("Memory cache hit for key: {Key}", fullKey);
            return memoryValue;
        }

        if (_distributedCache is null)
        {
            return default;
        }

        try
        {
            var cachedData = await _distributedCache.GetStringAsync(fullKey, cancellationToken);
            if (string.IsNullOrEmpty(cachedData))
            {
                return default;
            }

            var distributedValue = JsonSerializer.Deserialize(cachedData, jsonTypeInfo);
            if (distributedValue is not null)
            {
                _memoryCache.Set(fullKey, distributedValue, _memoryExpiration);
                _logger.LogDebug("Distributed cache hit for key: {Key}", fullKey);
            }

            return distributedValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error retrieving from distributed cache for key: {Key}", fullKey);
            return default;
        }
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        if (value is null)
        {
            return;
        }

        var fullKey = BuildKey(key);
        KnownCacheKeys.TryAdd(fullKey, 0);

        if (_distributedCache is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(value, jsonTypeInfo);
                await _distributedCache.SetStringAsync(
                    fullKey,
                    json,
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = _distributedExpiration
                    },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error writing to distributed cache for key: {Key}", fullKey);
            }
        }

        // L1 is populated even when the optional L2 write fails.
        _memoryCache.Set(fullKey, value, _memoryExpiration);
        _logger.LogDebug("Set value in hybrid cache for key: {Key}", fullKey);
    }

    public async Task RemoveAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            var fullKey = BuildKey(key);
            _memoryCache.Remove(fullKey);
            KnownCacheKeys.TryRemove(fullKey, out _);

            if (_distributedCache is not null)
            {
                await _distributedCache.RemoveAsync(fullKey, cancellationToken);
            }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var keys = KnownCacheKeys.Keys
            .Where(key => key.StartsWith(_keyPrefix, StringComparison.Ordinal))
            .Select(key => key[_keyPrefix.Length..])
            .ToArray();

        return RemoveAsync(keys, cancellationToken);
    }

    private string BuildKey(string key) => $"{_keyPrefix}{key}";
}
