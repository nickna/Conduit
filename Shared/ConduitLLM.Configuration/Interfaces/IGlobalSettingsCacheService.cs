using ConduitLLM.Core.Models;

namespace ConduitLLM.Configuration.Interfaces;

/// <summary>
/// Service for caching and retrieving global settings from the database.
/// Settings are loaded at application startup and cached in-memory for fast access.
/// Cache is automatically invalidated when settings are modified via the Admin API.
/// </summary>
public interface IGlobalSettingsCacheService
{
    /// <summary>
    /// Gets the maximum number of agentic iterations allowed per request.
    /// </summary>
    /// <returns>
    /// The configured maximum iterations value from GlobalSettings,
    /// or the default value of 5 if the setting doesn't exist.
    /// Value is clamped to the valid range of 1-100.
    /// </returns>
    Task<int> GetMaxAgenticIterationsAsync();

    /// <summary>
    /// Gets the minimum number of agentic iterations allowed per request.
    /// </summary>
    /// <returns>
    /// The configured minimum iterations value from GlobalSettings,
    /// or the default value of 1 if the setting doesn't exist.
    /// Value is clamped to the valid range of 1-100.
    /// </returns>
    Task<int> GetMinAgenticIterationsAsync();

    /// <summary>
    /// Gets the default state for agentic mode when not specified in request.
    /// </summary>
    /// <returns>
    /// The configured default enabled state from GlobalSettings,
    /// or the default value of true if the setting doesn't exist.
    /// </returns>
    Task<bool> GetDefaultAgenticModeEnabledAsync();

    /// <summary>
    /// Gets a raw setting value by key from the cache.
    /// Returns null if the key does not exist.
    /// </summary>
    /// <param name="key">The setting key to retrieve.</param>
    /// <returns>The setting value, or null if not found.</returns>
    Task<string?> GetSettingValueAsync(string key);

    /// <summary>
    /// Invalidates and immediately reloads a specific setting from the database.
    /// </summary>
    /// <param name="settingKey">The key of the setting to invalidate.</param>
    Task InvalidateSettingAsync(string settingKey);

    /// <summary>
    /// Reloads all cached settings from the database.
    /// </summary>
    Task ReloadAllSettingsAsync();

    /// <summary>
    /// Broadcasts a full reload to every process-local cache. Redis pub/sub is used when
    /// configured; a Redis-less development process reloads itself.
    /// </summary>
    /// <remarks>
    /// Each receiver records the request ID only after a successful reload. Concurrent
    /// duplicates share the in-flight result; failed or canceled IDs remain retryable.
    /// Completed IDs are limited to 10,000 per process in successful-completion FIFO order.
    /// Deduplication expires after 24 hours of monotonic TimeProvider elapsed time from completion;
    /// duplicate hits do not extend expiry or reorder entries. Expired entries are pruned
    /// on the next request or completion, and the count cap is enforced immediately.
    /// In-flight requests are tracked separately and never evicted by this retention policy.
    /// A late or out-of-order duplicate from either durable Admin or Gateway queue can
    /// reload after expiry, eviction or restart. Reloading reads the current full database
    /// snapshot rather than reapplying an event payload; permanent exactly-once execution
    /// is not promised. These contracts bound ID tracking, not measured heap usage.
    /// Without Redis, this task propagates load failures. With Redis, it acknowledges
    /// publication only; receiver failures are logged without recording success.
    /// Redis pub/sub does not provide durable redelivery.
    /// </remarks>
    /// <param name="requestId">The duplicate-correlation ID; a blank value creates a fresh ID.</param>
    Task PublishReloadAsync(string requestId);

    /// <summary>
    /// Gets cache statistics for monitoring and debugging.
    /// </summary>
    /// <returns>Strongly typed cache hit/miss counts and other metrics.</returns>
    Task<CacheStats> GetCacheStatsAsync();
}
