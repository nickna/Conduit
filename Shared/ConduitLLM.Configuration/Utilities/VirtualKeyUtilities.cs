using System.Text.Json;
using ConduitLLM.Configuration.Serialization;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Configuration.Utilities
{
    /// <summary>
    /// Static utility methods for virtual key operations
    /// </summary>
    public static class VirtualKeyUtilities
    {
        /// <summary>
        /// Hashes a key using SHA256
        /// </summary>
        /// <param name="key">The key to hash</param>
        /// <returns>Hexadecimal string representation of the hash</returns>
        public static string HashKey(string key)
        {
            return Sha256Hash.LowerHex(key);
        }

        /// <summary>
        /// Generates a secure random key
        /// </summary>
        /// <returns>A 32-character secure random string</returns>
        public static string GenerateSecureKey()
        {
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var bytes = new byte[32]; // 256 bits
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .Replace("+", "")
                .Replace("/", "")
                .Replace("=", "")
                .Substring(0, 32); // Take first 32 characters for consistency
        }

        /// <summary>
        /// Checks if a requested model is allowed based on the allowed models list
        /// </summary>
        /// <param name="requestedModel">The model being requested</param>
        /// <param name="allowedModels">Comma-separated list of allowed models, supports wildcards</param>
        /// <returns>True if the model is allowed, false otherwise</returns>
        public static bool IsModelAllowed(string requestedModel, string allowedModels)
        {
            if (string.IsNullOrEmpty(allowedModels))
                return true; // No restrictions

            var allowedModelsList = allowedModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // First check for exact match
            if (allowedModelsList.Any(m => string.Equals(m, requestedModel, StringComparison.OrdinalIgnoreCase)))
                return true;

            // Then check for wildcard/prefix matches
            foreach (var allowedModel in allowedModelsList)
            {
                // Handle wildcards like "gpt-4*" to match any GPT-4 model
                if (allowedModel.EndsWith("*", StringComparison.OrdinalIgnoreCase) &&
                    allowedModel.Length > 1)
                {
                    string prefix = allowedModel.Substring(0, allowedModel.Length - 1);
                    if (requestedModel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Maps VirtualKey entity to VirtualKeyDto
        /// </summary>
        /// <param name="virtualKey">The entity to map</param>
        /// <returns>The mapped DTO</returns>
        public static VirtualKeyDto MapToDto(VirtualKey virtualKey)
        {
            return new VirtualKeyDto
            {
                Id = virtualKey.Id,
                KeyName = virtualKey.KeyName,
                KeyPrefix = GenerateKeyPrefix(virtualKey.KeyHash),
                AllowedModels = ParseAllowedModels(virtualKey.AllowedModels),
                VirtualKeyGroupId = virtualKey.VirtualKeyGroupId,
                IsEnabled = virtualKey.IsEnabled,
                ExpiresAt = virtualKey.ExpiresAt,
                CreatedAt = virtualKey.CreatedAt,
                UpdatedAt = virtualKey.UpdatedAt,
                Metadata = ParseMetadata(virtualKey.Metadata),
                RateLimitRpm = virtualKey.RateLimitRpm,
                RateLimitRpd = virtualKey.RateLimitRpd,
                RateLimitTpm = virtualKey.RateLimitTpm,
                MaxParallelRequests = virtualKey.MaxParallelRequests,
                RateLimitPriority = virtualKey.RateLimitPriority,
                ModelRateLimits = ParseModelRateLimits(virtualKey.ModelRateLimits),
                Description = virtualKey.Description,
            };
        }

        /// <summary>
        /// Reads the stored per-model override document. Malformed JSON is surfaced as no
        /// overrides rather than an error: it must not make the key unreadable.
        /// </summary>
        public static Dictionary<string, ModelRateLimitDto>? ParseModelRateLimits(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize(
                    json,
                    ConfigurationJsonContext.Default.DictionaryStringModelRateLimitDto);
                return parsed is { Count: > 0 } ? parsed : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>Serialises per-model overrides for storage, collapsing an empty map to null.</summary>
        public static string? SerializeModelRateLimits(Dictionary<string, ModelRateLimitDto>? limits) =>
            limits is { Count: > 0 }
                ? System.Text.Json.JsonSerializer.Serialize(
                    limits,
                    ConfigurationJsonContext.Default.DictionaryStringModelRateLimitDto)
                : null;

        public static List<string>? ParseAllowedModels(string? allowedModels) =>
            string.IsNullOrWhiteSpace(allowedModels)
                ? null
                : allowedModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        private static Dictionary<string, JsonElement>? ParseMetadata(string? metadata) =>
            JsonMetadataParser.Parse(metadata);

        /// <summary>
        /// Generates a masked key prefix for display purposes using the hash
        /// </summary>
        public static string GenerateKeyPrefix(string? keyHash)
        {
            if (string.IsNullOrEmpty(keyHash))
            {
                return "condt_******...";
            }

            var prefixLength = Math.Min(6, keyHash.Length);
            var shortPrefix = keyHash.Substring(0, prefixLength).ToLower();
            return $"condt_{shortPrefix}...";
        }
    }
}
