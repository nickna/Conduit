using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Abstract base class for ephemeral key services that provides common cache operations
    /// and key management functionality.
    /// </summary>
    /// <typeparam name="TKeyData">The type of key data stored in cache</typeparam>
    public abstract class EphemeralKeyServiceBase<TKeyData> where TKeyData : class
    {
        protected readonly IDistributedCache Cache;
        protected readonly ILogger Logger;
        private readonly JsonTypeInfo<TKeyData> _keyDataTypeInfo;

        /// <summary>
        /// The prefix used for cache keys (e.g., "ephemeral:" or "ephemeral:master:")
        /// </summary>
        protected abstract string KeyPrefix { get; }

        /// <summary>
        /// The prefix added to generated tokens (e.g., "ek_" or "emk_")
        /// </summary>
        protected abstract string TokenPrefix { get; }

        /// <summary>
        /// The TTL in seconds for ephemeral keys
        /// </summary>
        protected abstract int TTLSeconds { get; }

        /// <summary>
        /// Initializes a new instance of the ephemeral key service base
        /// </summary>
        /// <param name="cache">The distributed cache</param>
        /// <param name="logger">The logger</param>
        protected EphemeralKeyServiceBase(
            IDistributedCache cache,
            ILogger logger,
            JsonTypeInfo<TKeyData> keyDataTypeInfo)
        {
            Cache = cache ?? throw new ArgumentNullException(nameof(cache));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _keyDataTypeInfo = keyDataTypeInfo ?? throw new ArgumentNullException(nameof(keyDataTypeInfo));
        }

        /// <summary>
        /// Generates a cryptographically secure token with the configured prefix
        /// </summary>
        /// <returns>A secure, URL-safe token</returns>
        protected string GenerateSecureToken()
        {
            const int tokenLength = 32; // 256 bits
            var randomBytes = new byte[tokenLength];

            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }

            // Convert to URL-safe base64
            var token = Convert.ToBase64String(randomBytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            return $"{TokenPrefix}{token}";
        }

        /// <summary>
        /// Sanitizes a key for safe logging by truncating it
        /// </summary>
        /// <param name="key">The key to sanitize</param>
        /// <returns>A truncated version of the key safe for logging</returns>
        protected static string SanitizeKeyForLogging(string key)
        {
            return Utilities.SpanHelper.MaskSecret(key);
        }

        /// <summary>
        /// Gets the full cache key for a given ephemeral key token
        /// </summary>
        /// <param name="key">The ephemeral key token</param>
        /// <returns>The full cache key</returns>
        protected string GetCacheKey(string key) => $"{KeyPrefix}{key}";

        /// <summary>
        /// Stores key data in the distributed cache with the configured TTL
        /// </summary>
        /// <param name="key">The ephemeral key token</param>
        /// <param name="keyData">The key data to store</param>
        /// <param name="ttlOverride">Optional TTL override in seconds</param>
        protected async Task StoreKeyDataAsync(string key, TKeyData keyData, int? ttlOverride = null)
        {
            var cacheKey = GetCacheKey(key);
            var serializedData = JsonSerializer.Serialize(keyData, _keyDataTypeInfo);
            var ttl = ttlOverride ?? TTLSeconds;

            await Cache.SetStringAsync(
                cacheKey,
                serializedData,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl)
                });
        }

        /// <summary>
        /// Retrieves key data from the distributed cache
        /// </summary>
        /// <param name="key">The ephemeral key token</param>
        /// <returns>The key data if found, null otherwise</returns>
        protected async Task<TKeyData?> GetKeyDataFromCacheAsync(string key)
        {
            var cacheKey = GetCacheKey(key);
            var serializedData = await Cache.GetStringAsync(cacheKey);

            if (string.IsNullOrEmpty(serializedData))
            {
                return null;
            }

            return JsonSerializer.Deserialize(serializedData, _keyDataTypeInfo);
        }

        /// <summary>
        /// Deletes an ephemeral key from the cache
        /// </summary>
        /// <param name="key">The ephemeral key to delete</param>
        public virtual async Task DeleteKeyAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            var cacheKey = GetCacheKey(key);
            await Cache.RemoveAsync(cacheKey);

            Logger.LogDebug("Deleted ephemeral key: {Key}", SanitizeKeyForLogging(key));
        }

        /// <summary>
        /// Checks if a key exists in the cache
        /// </summary>
        /// <param name="key">The ephemeral key to check</param>
        /// <returns>True if the key exists, false otherwise</returns>
        public virtual async Task<bool> KeyExistsAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            var cacheKey = GetCacheKey(key);
            var data = await Cache.GetStringAsync(cacheKey);
            return !string.IsNullOrEmpty(data);
        }
    }

    /// <summary>
    /// Base class for single-use ephemeral key services that validate and consume cached keys.
    /// </summary>
    /// <typeparam name="TKeyData">The type of key data stored in cache</typeparam>
    public abstract class ConsumableEphemeralKeyServiceBase<TKeyData> : EphemeralKeyServiceBase<TKeyData>
        where TKeyData : class
    {
        /// <summary>
        /// Initializes a new instance of the consumable ephemeral key service base.
        /// </summary>
        /// <param name="cache">The distributed cache</param>
        /// <param name="logger">The logger</param>
        protected ConsumableEphemeralKeyServiceBase(
            IDistributedCache cache,
            ILogger logger,
            JsonTypeInfo<TKeyData> keyDataTypeInfo)
            : base(cache, logger, keyDataTypeInfo)
        {
        }

        /// <summary>
        /// Checks if the key data indicates the key has been consumed
        /// </summary>
        /// <param name="keyData">The key data to check</param>
        /// <returns>True if the key has been consumed</returns>
        protected abstract bool IsKeyConsumed(TKeyData keyData);

        /// <summary>
        /// Gets the expiration time from the key data
        /// </summary>
        /// <param name="keyData">The key data</param>
        /// <returns>The expiration time</returns>
        protected abstract DateTimeOffset GetKeyExpiration(TKeyData keyData);

        /// <summary>
        /// Checks if the key data indicates the key is valid (beyond just not-consumed and not-expired)
        /// </summary>
        /// <param name="keyData">The key data to check</param>
        /// <returns>True if the key is valid</returns>
        protected virtual bool IsKeyValid(TKeyData keyData) => true;

        /// <summary>
        /// Marks the key data as consumed
        /// </summary>
        /// <param name="keyData">The key data to mark</param>
        protected abstract void MarkKeyAsConsumed(TKeyData keyData);

        /// <summary>
        /// Validates key data without consuming it
        /// </summary>
        /// <param name="key">The ephemeral key to validate</param>
        /// <returns>The key data if valid, null otherwise</returns>
        protected async Task<TKeyData?> ValidateKeyAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                Logger.LogDebug("Ephemeral key validation failed: empty or whitespace key");
                return null;
            }

            var keyData = await GetKeyDataFromCacheAsync(key);
            if (keyData == null)
            {
                Logger.LogWarning("Ephemeral key not found: {Key}", SanitizeKeyForLogging(key));
                return null;
            }

            if (IsKeyConsumed(keyData))
            {
                Logger.LogWarning("Ephemeral key already used: {Key}", SanitizeKeyForLogging(key));
                return null;
            }

            var expiresAt = GetKeyExpiration(keyData);
            if (expiresAt < DateTimeOffset.UtcNow)
            {
                Logger.LogWarning("Ephemeral key expired: {Key}, expired at {ExpiresAt}",
                    SanitizeKeyForLogging(key), expiresAt);
                await Cache.RemoveAsync(GetCacheKey(key));
                return null;
            }

            if (!IsKeyValid(keyData))
            {
                Logger.LogWarning("Ephemeral key is not valid: {Key}", SanitizeKeyForLogging(key));
                return null;
            }

            return keyData;
        }

        /// <summary>
        /// Validates a key and marks it as consumed, keeping it briefly in cache for cleanup tracking
        /// </summary>
        /// <param name="key">The ephemeral key to validate and consume</param>
        /// <returns>The key data if valid and successfully consumed, null otherwise</returns>
        protected async Task<TKeyData?> ValidateAndConsumeKeyInternalAsync(string key)
        {
            var keyData = await ValidateKeyAsync(key);
            if (keyData == null)
            {
                return null;
            }

            // Mark as consumed but keep in cache for cleanup tracking
            MarkKeyAsConsumed(keyData);
            await StoreKeyDataAsync(key, keyData, ttlOverride: 30); // Keep for 30s for cleanup

            return keyData;
        }

        /// <summary>
        /// Validates a key and immediately deletes it (for streaming scenarios)
        /// </summary>
        /// <param name="key">The ephemeral key to consume</param>
        /// <returns>The key data if valid, null otherwise</returns>
        protected async Task<TKeyData?> ConsumeKeyInternalAsync(string key)
        {
            var keyData = await ValidateKeyAsync(key);
            if (keyData == null)
            {
                return null;
            }

            // For streaming, immediately delete the key after successful validation
            await Cache.RemoveAsync(GetCacheKey(key));

            return keyData;
        }
    }
}
