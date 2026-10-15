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
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lifecycleGate = new();
    private readonly ConcurrentDictionary<string, byte> _processedReloads = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CacheSnapshot _snapshot = CacheSnapshot.Empty;
    private Task _recoveryTask = Task.CompletedTask;
    private Task _subscriptionTask = Task.CompletedTask;
    private ISubscriber? _subscriber;
    private bool _stopRequested;
    private bool _hasLoaded;

    private sealed record CacheSnapshot(IReadOnlyDictionary<string, string> Values, DateTime LastLoadTime)
    {
        public static CacheSnapshot Empty { get; } = new(new Dictionary<string, string>(), DateTime.MinValue);
    }

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
    public GlobalSettingsCacheService(
        IServiceScopeFactory scopeFactory,
        ILogger<GlobalSettingsCacheService> logger,
        IConnectionMultiplexer? redis = null)
        : this(scopeFactory, logger, redis, TimeProvider.System)
    {
    }

    internal GlobalSettingsCacheService(
        IServiceScopeFactory scopeFactory,
        ILogger<GlobalSettingsCacheService> logger,
        IConnectionMultiplexer? redis,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _redis = redis;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Starts the service and loads all settings into cache.
    /// Called automatically by the hosting infrastructure.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        _logger.LogInformation("GlobalSettingsCacheService starting - loading settings from database");

        try
        {
            if (_redis != null)
            {
                lock (_lifecycleGate)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    if (_stopRequested)
                        throw new OperationCanceledException(_stopping.Token);
                    _subscriber = _redis.GetSubscriber();
                    _subscriptionTask = _subscriber.SubscribeAsync(
                        RedisChannel.Literal(ReloadChannelName),
                        (channel, value) =>
                        {
                            _ = channel;
                            _ = HandleReloadBroadcastAsync(value.ToString());
                        });
                }
                await _subscriptionTask.WaitAsync(startup.Token);
            }
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to global settings reloads - continuing with local database loading");
        }

        try
        {
            await LoadAllSettingsAsync(startup.Token);
            _logger.LogInformation("GlobalSettingsCacheService started successfully - {Count} settings loaded",
                Volatile.Read(ref _snapshot).Values.Count);
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            startup.Token.ThrowIfCancellationRequested();
            _logger.LogError(ex, "Failed to load global settings on startup - service will use defaults while recovery retries");
            lock (_lifecycleGate)
            {
                if (!_stopRequested)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    _recoveryTask = RecoverInitialLoadAsync();
                }
            }
        }
    }

    /// <summary>
    /// Stops the service and clears the cache.
    /// Called automatically by the hosting infrastructure.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("GlobalSettingsCacheService stopping");
        Task recovery;
        Task subscription;
        lock (_lifecycleGate)
        {
            _stopRequested = true;
            recovery = _recoveryTask;
            subscription = _subscriptionTask;
        }
        _stopping.Cancel();
        await recovery.WaitAsync(cancellationToken);
        // Every repository operation owns this lock. Drain the active operation
        // before clearing; canceled waiters cannot enter and publish after stop.
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Volatile.Write(ref _snapshot, CacheSnapshot.Empty);
        }
        finally
        {
            _lock.Release();
        }

        if (_subscriber != null)
        {
            try
            {
                // A subscription canceled during startup can complete later.
                // Await it before unsubscribing so shutdown cannot leave it behind.
                try { await subscription.WaitAsync(cancellationToken); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                await _subscriber.UnsubscribeAsync(RedisChannel.Literal(ReloadChannelName)).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unsubscribe from global settings reloads during shutdown");
            }
        }
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
        if (string.IsNullOrWhiteSpace(settingKey) || Volatile.Read(ref _stopRequested))
        {
            return;
        }

        var entered = false;
        try
        {
            await _lock.WaitAsync(_stopping.Token);
            entered = true;
            var snapshot = Volatile.Read(ref _snapshot);
            var wasCached = snapshot.Values.TryGetValue(settingKey, out var oldValue);
            Interlocked.Increment(ref _invalidations);

            // Always reload: a newly created setting was never present in this process's cache.
            using (var scope = _scopeFactory.CreateScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IGlobalSettingRepository>();
                var newSetting = await repository.GetByKeyAsync(settingKey, _stopping.Token);
                _stopping.Token.ThrowIfCancellationRequested();
                var updated = new Dictionary<string, string>(snapshot.Values);
                updated.Remove(settingKey);
                if (newSetting != null)
                {
                    updated[settingKey] = newSetting.Value;
                    _logger.LogInformation("Reloaded setting '{Key}' with new value: '{Value}'", settingKey, newSetting.Value);
                }
                Volatile.Write(ref _snapshot, new CacheSnapshot(updated, snapshot.LastLoadTime));
                if (wasCached)
                    _logger.LogInformation("Invalidated cached setting '{Key}' (old value: '{Value}')", settingKey, oldValue);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Shutdown owns cancellation; no stale repository result is published.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating setting '{Key}'", settingKey);
        }
        finally
        {
            if (entered)
                _lock.Release();
        }
    }

    public async Task ReloadAllSettingsAsync()
    {
        if (Volatile.Read(ref _stopRequested))
            return;
        _logger.LogInformation("Reloading all global settings from database");
        await LoadAllSettingsAsync(_stopping.Token);
        _logger.LogInformation("Reloaded {Count} settings from database", Volatile.Read(ref _snapshot).Values.Count);
    }

    public async Task PublishReloadAsync(string requestId)
    {
        if (Volatile.Read(ref _stopRequested))
            return;
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
        if (Volatile.Read(ref _stopRequested) || string.IsNullOrWhiteSpace(requestId) || !_processedReloads.TryAdd(requestId, 0))
        {
            return;
        }

        try
        {
            await ReloadAllSettingsAsync();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Cluster-wide global settings reload {RequestId} was canceled because the service is stopping",
                requestId);
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
        var snapshot = Volatile.Read(ref _snapshot);
        var stats = new CacheStats
        {
            EntryCount = snapshot.Values.Count,
            HitCount = Interlocked.Read(ref _cacheHits),
            MissCount = Interlocked.Read(ref _cacheMisses),
            InvalidationCount = Interlocked.Read(ref _invalidations),
            LastResetTime = snapshot.LastLoadTime,
            CachedKeys = snapshot.Values.Keys.OrderBy(key => key).ToArray()
        };

        return Task.FromResult(stats);
    }

    /// <summary>
    /// Gets a setting value from cache, or returns null if not found.
    /// </summary>
    private Task<string?> GetSettingAsync(string key)
    {
        if (Volatile.Read(ref _snapshot).Values.TryGetValue(key, out var value))
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
    private async Task LoadAllSettingsAsync(CancellationToken cancellationToken, bool recoveryOnly = false)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (recoveryOnly && _hasLoaded)
                return;
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IGlobalSettingRepository>();
            var settings = await repository.ListAsync(cancellationToken);
            var updated = new Dictionary<string, string>();
            foreach (var setting in settings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                updated.TryAdd(setting.Key, setting.Value);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _snapshot, new CacheSnapshot(updated, _timeProvider.GetUtcNow().UtcDateTime));
            _hasLoaded = true;
            _logger.LogDebug("Loaded {Count} settings into cache", updated.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RecoverInitialLoadAsync()
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _timeProvider, _stopping.Token);
                await LoadAllSettingsAsync(_stopping.Token, recoveryOnly: true);
                _logger.LogInformation("Global settings startup recovery completed - {Count} settings loaded",
                    Volatile.Read(ref _snapshot).Values.Count);
                return;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Global settings startup recovery failed - retrying with bounded backoff");
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }
}
