using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json.Serialization;
using ConduitLLM.Admin.Models;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Core.Services;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Service for managing ephemeral master keys for Admin API authentication
    /// </summary>
    public interface IEphemeralMasterKeyService
    {
        /// <summary>
        /// Creates an ephemeral master key
        /// </summary>
        /// <returns>The ephemeral master key response with token and expiration</returns>
        Task<EphemeralMasterKeyResponse> CreateEphemeralMasterKeyAsync();

        /// <summary>
        /// Validates and consumes an ephemeral master key
        /// </summary>
        /// <param name="key">The ephemeral master key to validate</param>
        /// <returns>True if valid, false otherwise</returns>
        Task<bool> ValidateAndConsumeKeyAsync(string key);

        /// <summary>
        /// Validates an ephemeral master key without consuming it.
        /// </summary>
        /// <param name="key">The ephemeral master key to validate.</param>
        /// <returns>True when the key exists, is unexpired, valid, and unconsumed.</returns>
        Task<bool> IsKeyValidAsync(string key);

        /// <summary>
        /// Deletes an ephemeral master key after use
        /// </summary>
        /// <param name="key">The ephemeral master key to delete</param>
        Task DeleteKeyAsync(string key);

        /// <summary>
        /// Checks if a key exists and is valid
        /// </summary>
        /// <param name="key">The ephemeral master key to check</param>
        /// <returns>True if the key exists and is valid</returns>
        Task<bool> KeyExistsAsync(string key);
    }

    /// <summary>
    /// Implementation of the ephemeral master key service for Admin API authentication
    /// </summary>
    public class EphemeralMasterKeyService : ConsumableEphemeralKeyServiceBase<EphemeralMasterKeyData>, IEphemeralMasterKeyService
    {
        private const int DefaultTTLSeconds = 300; // 5 minutes

        /// <inheritdoc />
        protected override string KeyPrefix => CacheKeys.Ephemeral.MasterPrefix;

        /// <inheritdoc />
        protected override string TokenPrefix => CacheKeys.Ephemeral.MasterTokenPrefix;

        /// <inheritdoc />
        protected override int TTLSeconds => DefaultTTLSeconds;

        /// <summary>
        /// Initializes a new instance of the <see cref="EphemeralMasterKeyService"/> class.
        /// </summary>
        /// <param name="cache">The distributed cache</param>
        /// <param name="logger">The logger</param>
        public EphemeralMasterKeyService(
            IDistributedCache cache,
            ILogger<EphemeralMasterKeyService> logger)
            : base(cache, logger, EphemeralMasterKeyJsonContext.Default.EphemeralMasterKeyData)
        {
        }

        /// <inheritdoc />
        protected override bool IsKeyConsumed(EphemeralMasterKeyData keyData) => keyData.IsConsumed;

        /// <inheritdoc />
        protected override DateTimeOffset GetKeyExpiration(EphemeralMasterKeyData keyData) => keyData.ExpiresAt;

        /// <inheritdoc />
        protected override bool IsKeyValid(EphemeralMasterKeyData keyData) => keyData.IsValid;

        /// <inheritdoc />
        protected override void MarkKeyAsConsumed(EphemeralMasterKeyData keyData) => keyData.IsConsumed = true;

        /// <inheritdoc />
        public async Task<EphemeralMasterKeyResponse> CreateEphemeralMasterKeyAsync()
        {
            var key = GenerateSecureToken();
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(TTLSeconds);

            var keyData = new EphemeralMasterKeyData
            {
                Key = key,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
                IsConsumed = false,
                IsValid = true
            };

            await StoreKeyDataAsync(key, keyData);

            Logger.LogInformation("Created ephemeral master key, expires at {ExpiresAt}", expiresAt);

            return new EphemeralMasterKeyResponse
            {
                EphemeralMasterKey = key,
                ExpiresAt = expiresAt,
                ExpiresInSeconds = TTLSeconds
            };
        }

        /// <inheritdoc />
        public async Task<bool> ValidateAndConsumeKeyAsync(string key)
        {
            var keyData = await ValidateAndConsumeKeyInternalAsync(key);
            if (keyData == null)
            {
                Logger.LogWarning("Failed to validate/consume ephemeral master key: {Key}",
                    SanitizeKeyForLogging(key));
                return false;
            }

            Logger.LogInformation("Consumed ephemeral master key");
            return true;
        }

        /// <inheritdoc />
        public async Task<bool> IsKeyValidAsync(string key)
        {
            return await ValidateKeyAsync(key) is not null;
        }

    }

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(EphemeralMasterKeyData))]
    internal partial class EphemeralMasterKeyJsonContext : JsonSerializerContext;
}
