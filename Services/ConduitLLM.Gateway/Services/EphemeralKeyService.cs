using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json.Serialization;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Models;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Service for managing reusable, short-lived API keys for direct browser-to-API communication.
    /// Keys remain valid until their cache TTL expires or they are explicitly deleted.
    /// </summary>
    public interface IEphemeralKeyService
    {
        /// <summary>
        /// Creates an ephemeral key for a virtual key
        /// </summary>
        /// <param name="virtualKeyId">The virtual key ID to associate with the ephemeral key</param>
        /// <param name="virtualKey">The actual virtual key value to store (will be encrypted)</param>
        /// <param name="metadata">Optional metadata about the request</param>
        /// <returns>The ephemeral key response with token and expiration</returns>
        Task<EphemeralKeyResponse> CreateEphemeralKeyAsync(int virtualKeyId, string virtualKey, EphemeralKeyMetadata? metadata = null);

        /// <summary>
        /// Deletes an ephemeral key after use
        /// </summary>
        /// <param name="key">The ephemeral key to delete</param>
        Task DeleteKeyAsync(string key);

        /// <summary>
        /// Checks if a key exists and is valid
        /// </summary>
        /// <param name="key">The ephemeral key to check</param>
        /// <returns>True if the key exists and is valid</returns>
        Task<bool> KeyExistsAsync(string key);

        /// <summary>
        /// Retrieves the virtual key associated with an ephemeral key
        /// </summary>
        /// <param name="key">The ephemeral key</param>
        /// <returns>The virtual key if found and valid, null otherwise</returns>
        Task<string?> GetVirtualKeyAsync(string key);

        /// <summary>
        /// Gets the virtual key ID for an ephemeral key without consuming it
        /// </summary>
        /// <param name="key">The ephemeral key</param>
        /// <returns>The virtual key ID if valid, null otherwise</returns>
        Task<int?> GetVirtualKeyIdAsync(string key);

        /// <summary>
        /// Gets the full ephemeral key data without consuming it
        /// </summary>
        /// <param name="key">The ephemeral key</param>
        /// <returns>The key data if found, null otherwise</returns>
        Task<EphemeralKeyData?> GetKeyDataAsync(string key);
    }

    /// <summary>
    /// Implementation of the ephemeral key service for Gateway API authentication
    /// </summary>
    public class EphemeralKeyService : EphemeralKeyServiceBase<EphemeralKeyData>, IEphemeralKeyService
    {
        private const int DefaultTTLSeconds = 900; // 15 minutes - longer for video generation which can take several minutes
        private const string ProtectorPurpose = "ConduitLLM.Gateway.EphemeralVirtualKey.v1";
        private const string ProtectedValuePrefix = "dp:v1:";

        private readonly IDataProtector _protector;

        /// <inheritdoc />
        protected override string KeyPrefix => CacheKeys.Ephemeral.Prefix;

        /// <inheritdoc />
        protected override string TokenPrefix => CacheKeys.Ephemeral.TokenPrefix;

        /// <inheritdoc />
        protected override int TTLSeconds => DefaultTTLSeconds;

        /// <summary>
        /// Initializes a new instance of the <see cref="EphemeralKeyService"/> class.
        /// </summary>
        /// <param name="cache">The distributed cache</param>
        /// <param name="dataProtectionProvider">The application Data Protection provider</param>
        /// <param name="logger">The logger</param>
        public EphemeralKeyService(
            IDistributedCache cache,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<EphemeralKeyService> logger)
            : base(cache, logger, EphemeralKeyJsonContext.Default.EphemeralKeyData)
        {
            ArgumentNullException.ThrowIfNull(dataProtectionProvider);
            _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        }

        /// <inheritdoc />
        public async Task<EphemeralKeyResponse> CreateEphemeralKeyAsync(int virtualKeyId, string virtualKey, EphemeralKeyMetadata? metadata = null)
        {
            var key = GenerateSecureToken();
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(TTLSeconds);

            // Encrypt the virtual key for storage
            var encryptedVirtualKey = ProtectString(virtualKey);

            var keyData = new EphemeralKeyData
            {
                Key = key,
                VirtualKeyId = virtualKeyId,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
                Metadata = metadata,
                EncryptedVirtualKey = encryptedVirtualKey
            };

            await StoreKeyDataAsync(key, keyData);

            Logger.LogInformation("Created ephemeral key for virtual key {VirtualKeyId}, expires at {ExpiresAt}",
                virtualKeyId, expiresAt);

            return new EphemeralKeyResponse
            {
                EphemeralKey = key,
                ExpiresAt = expiresAt,
                ExpiresInSeconds = TTLSeconds
            };
        }

        /// <inheritdoc />
        public async Task<string?> GetVirtualKeyAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                Logger.LogDebug("GetVirtualKeyAsync: empty key");
                return null;
            }

            var keyData = await GetKeyDataFromCacheAsync(key);
            if (keyData == null || string.IsNullOrEmpty(keyData.EncryptedVirtualKey))
            {
                Logger.LogWarning("GetVirtualKeyAsync: Ephemeral key not found or no encrypted virtual key: {Key}",
                    SanitizeKeyForLogging(key));
                return null;
            }

            // Check expiration
            if (keyData.ExpiresAt < DateTimeOffset.UtcNow)
            {
                Logger.LogWarning("GetVirtualKeyAsync: Ephemeral key expired: {Key}", SanitizeKeyForLogging(key));
                return null;
            }

            // Decrypt and return the virtual key
            try
            {
                return UnprotectString(keyData.EncryptedVirtualKey);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to decrypt virtual key for ephemeral key: {Key}", SanitizeKeyForLogging(key));
                return null;
            }
        }

        /// <inheritdoc />
        public async Task<int?> GetVirtualKeyIdAsync(string key)
        {
            var keyData = await GetKeyDataAsync(key);
            return keyData?.VirtualKeyId;
        }

        /// <inheritdoc />
        public async Task<EphemeralKeyData?> GetKeyDataAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            return await GetKeyDataFromCacheAsync(key);
        }

        private string ProtectString(string plainText)
        {
            return ProtectedValuePrefix + _protector.Protect(plainText);
        }

        private string UnprotectString(string protectedValue)
        {
            if (!protectedValue.StartsWith(ProtectedValuePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsupported ephemeral key protection format.");
            }

            return _protector.Unprotect(protectedValue[ProtectedValuePrefix.Length..]);
        }
    }

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(EphemeralKeyData))]
    internal partial class EphemeralKeyJsonContext : JsonSerializerContext;
}
