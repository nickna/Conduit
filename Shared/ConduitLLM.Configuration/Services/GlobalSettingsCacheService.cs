using System.Collections.Concurrent;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConduitLLM.Configuration.Services;

/// <summary>
/// In-memory cache service for GlobalSettings that loads settings at startup
/// and provides fast, strongly-typed access to configuration values.
/// Automatically invalidates cache when settings are modified via Admin API.
/// </summary>
public class GlobalSettingsCacheService : IHostedService, IGlobalSettingsCacheService
{
    private const string ReloadChannelName = "conduit:global-settings:reload";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GlobalSettingsCacheService> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _processedReloads = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Setting keys
    private const string KEY_MAX_AGENTIC_ITERATIONS = "Agentic.MaxIterations";
    private const string KEY_MIN_AGENTIC_ITERATIONS = "Agentic.MinIterations";
    private const string KEY_DEFAULT_AGENTIC_ENABLED = "Agentic.DefaultEnabled";

    // Default values
    private const int DEFAULT_MAX_AGENTIC_ITERATIONS = 5;
    private const int DEFAULT_MIN_AGENTIC_ITERATIONS = 1;
    private const bool DEFAULT_AGENTIC_ENABLED = true;

    // Validation constants
    private const int MIN_VALID_ITERATIONS = 1;
    private const int MAX_VALID_ITERATIONS = 100;

    // Statistics
    private long _cacheHits = 0;
    private long _cacheMisses = 0;
    private long _invalidations = 0;
    private DateTime _lastLoadTime = DateTime.MinValue;

    public GlobalSettingsCacheService(
        IServiceScopeFactory scopeFactory,
        ILogger<GlobalSettingsCacheService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _redis = redis;
    }

    /// <summary>
    /// Starts the service and loads all settings into cache.
    /// Called automatically by the hosting infrastructure.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("GlobalSettingsCacheService starting - loading settings from database");

        try
        {
            if (_redis != null)
            {
                await _redis.GetSubscriber().SubscribeAsync(
                    RedisChannel.Literal(ReloadChannelName),
                    (channel, value) =>
                    {
                        _ = channel;
                        _ = HandleReloadBroadcastAsync(value.ToString());
                    });
            }
            await LoadAllSettingsAsync(cancellationToken);
            _logger.LogInformation("GlobalSettingsCacheService started successfully - {Count} settings loaded", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load global settings on startup - service will use defaults");
            // Don't throw - allow service to start with defaults
        }
    }

    /// <summary>
    /// Stops the service and clears the cache.
    /// Called automatically by the hosting infrastructure.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("GlobalSettingsCacheService stopping");
        if (_redis != null)
        {
            await _redis.GetSubscriber().UnsubscribeAsync(RedisChannel.Literal(ReloadChannelName));
        }
        _cache.Clear();
    }

    public async Task<int> GetMaxAgenticIterationsAsync()
        => await GetClampedIntSettingAsync(KEY_MAX_AGENTIC_ITERATIONS, DEFAULT_MAX_AGENTIC_ITERATIONS,
            MIN_VALID_ITERATIONS, MAX_VALID_ITERATIONS, "Max agentic iterations");

    public async Task<int> GetMinAgenticIterationsAsync()
        => await GetClampedIntSettingAsync(KEY_MIN_AGENTIC_ITERATIONS, DEFAULT_MIN_AGENTIC_ITERATIONS,
            MIN_VALID_ITERATIONS, MAX_VALID_ITERATIONS, "Min agentic iterations");

    public async Task<bool> GetDefaultAgenticModeEnabledAsync()
        => await GetBoolSettingAsync(KEY_DEFAULT_AGENTIC_ENABLED, DEFAULT_AGENTIC_ENABLED, "Default agentic enabled");

    /// <inheritdoc />
    public Task<string?> GetSettingValueAsync(string key)
    {
        return GetSettingAsync(key);
    }

    /// <summary>
    /// Parses a string value as boolean, accepting "true"/"false", "1"/"0", "yes"/"no", "on"/"off".
    /// </summary>
    private static bool TryParseFuzzyBool(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "1" or "yes" or "on") { result = true; return true; }
        if (normalized is "0" or "no" or "off") { result = false; return true; }

        result = default;
        return false;
    }

    /// <summary>
    /// Gets a boolean setting with fuzzy parsing and fallback to default.
    /// </summary>
    private async Task<bool> GetBoolSettingAsync(string key, bool defaultValue, string settingName)
    {
        var value = await GetSettingAsync(key);

        if (string.IsNullOrWhiteSpace(value))
        {
            _logger.LogDebug("{SettingName} setting not found, using default: {Default}", settingName, defaultValue);
            return defaultValue;
        }

        if (TryParseFuzzyBool(value, out var result))
            return result;

        _logger.LogWarning("Failed to parse {SettingName} value '{Value}', using default: {Default}",
            settingName, value, defaultValue);
        return defaultValue;
    }

    /// <summary>
    /// Gets an integer setting, clamped to the specified range, with fallback to default.
    /// </summary>
    private async Task<int> GetClampedIntSettingAsync(string key, int defaultValue, int min, int max, string settingName)
    {
        var value = await GetSettingAsync(key);

        if (string.IsNullOrWhiteSpace(value))
        {
            _logger.LogDebug("{SettingName} setting not found, using default: {Default}", settingName, defaultValue);
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed))
        {
            _logger.LogWarning("Failed to parse {SettingName} value '{Value}', using default: {Default}",
                settingName, value, defaultValue);
            return defaultValue;
        }

        var clamped = Math.Clamp(parsed, min, max);
        if (clamped != parsed)
        {
            _logger.LogWarning("{SettingName} {Value} out of valid range ({Min}-{Max}), clamping to {Clamped}",
                settingName, parsed, min, max, clamped);
        }

        return clamped;
    }

    public async Task InvalidateSettingAsync(string settingKey)
    {
        if (string.IsNullOrWhiteSpace(settingKey))
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            var wasCached = _cache.TryRemove(settingKey, out var oldValue);
            Interlocked.Increment(ref _invalidations);
            if (wasCached)
            {
                _logger.LogInformation("Invalidated cached setting '{Key}' (old value: '{Value}')", settingKey, oldValue);
            }

            // Always reload: a newly created setting was never present in this process's cache.
            using (var scope = _scopeFactory.CreateScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IGlobalSettingRepository>();
                var newSetting = await repository.GetByKeyAsync(settingKey);
                if (newSetting != null)
                {
                    _cache[settingKey] = newSetting.Value;
                    _logger.LogInformation("Reloaded setting '{Key}' with new value: '{Value}'", settingKey, newSetting.Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating setting '{Key}'", settingKey);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ReloadAllSettingsAsync()
    {
        _logger.LogInformation("Reloading all global settings from database");

        await _lock.WaitAsync();
        try
        {
            _cache.Clear();
            await LoadAllSettingsAsync(CancellationToken.None);
            _logger.LogInformation("Reloaded {Count} settings from database", _cache.Count);
        }

        finally
        {
            _lock.Release();
        }
    }

    public async Task PublishReloadAsync(string requestId)
    {
        var id = string.IsNullOrWhiteSpace(requestId)
            ? Guid.NewGuid().ToString("N")
            : requestId;
        if (_redis == null)
        {
            if (_processedReloads.TryAdd(id, 0))
            {
                await ReloadAllSettingsAsync();
            }
            return;
        }

        await _redis.GetSubscriber().PublishAsync(
            RedisChannel.Literal(ReloadChannelName),
            id);
    }

    private async Task HandleReloadBroadcastAsync(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || !_processedReloads.TryAdd(requestId, 0))
        {
            return;
        }

        try
        {
            await ReloadAllSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process cluster-wide global settings reload {RequestId}",
                requestId);
        }
    }

    public Task<CacheStats> GetCacheStatsAsync()
    {
        var stats = new CacheStats
        {
            EntryCount = _cache.Count,
            HitCount = Interlocked.Read(ref _cacheHits),
            MissCount = Interlocked.Read(ref _cacheMisses),
            InvalidationCount = Interlocked.Read(ref _invalidations),
            LastResetTime = _lastLoadTime,
            CachedKeys = _cache.Keys.OrderBy(key => key).ToArray()
        };

        return Task.FromResult(stats);
    }

    /// <summary>
    /// Gets a setting value from cache, or returns null if not found.
    /// </summary>
    private Task<string?> GetSettingAsync(string key)
    {
        if (_cache.TryGetValue(key, out var value))
        {
            Interlocked.Increment(ref _cacheHits);
            return Task.FromResult<string?>(value);
        }

        Interlocked.Increment(ref _cacheMisses);
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Loads all settings from database into the cache.
    /// </summary>
    private async Task LoadAllSettingsAsync(CancellationToken cancellationToken)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IGlobalSettingRepository>();
            var settings = await repository.ListAsync(cancellationToken);

            foreach (var setting in settings)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _cache.TryAdd(setting.Key, setting.Value);
            }

            _lastLoadTime = DateTime.UtcNow;
            _logger.LogDebug("Loaded {Count} settings into cache", _cache.Count);
        }
    }
}
