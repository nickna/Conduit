using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Services;

using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Core.Models;

using VirtualKeyUtilities = ConduitLLM.Configuration.Utilities.VirtualKeyUtilities;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Service for managing virtual keys through the Admin API - Validation functionality
    /// </summary>
    public partial class AdminVirtualKeyService
    {
        /// <inheritdoc />
        public async Task<VirtualKeyValidationResult> ValidateVirtualKeyAsync(string key, string? requestedModel = null)
        {
            _logger.LogInformation("Validating virtual key and checking if model {Model} is allowed", (LoggingSanitizer.S(requestedModel ?? "any")));

            var result = new VirtualKeyValidationResult { IsValid = false };

            if (string.IsNullOrEmpty(key))
            {
                result.ErrorMessage = "Key cannot be empty";
                return result;
            }

            if (!key.StartsWith(VirtualKeyConstants.KeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                result.ErrorMessage = "Invalid key format: doesn't start with required prefix";
                return result;
            }

            // Hash the key for lookup
            string keyHash = VirtualKeyUtilities.HashKey(key);

            try
            {
                // Look up the key in the database
                var virtualKey = await _virtualKeyRepository.GetByKeyHashAsync(keyHash);
                if (virtualKey == null)
                {
                    result.ErrorMessage = "Key not found";
                    return result;
                }

                // Delegate core validation to shared helper
                var validationResult = await VirtualKeyValidationHelper.ValidateVirtualKeyAsync(
                    virtualKey,
                    requestedModel,
                    checkBalance: true,
                    async value =>
                    {
                        var group = await _groupRepository.GetByIdAsync(value.VirtualKeyGroupId);
                        return (group, PendingSpend: 0m);
                    },
                    _logger);

                if (!validationResult.IsValid)
                {
                    // Map helper reasons to admin-specific error messages
                    result.ErrorMessage = validationResult.FailureCode switch
                    {
                        VirtualKeyValidationFailureCodes.KeyDisabled => "Key is disabled",
                        VirtualKeyValidationFailureCodes.KeyExpired => "Key has expired",
                        VirtualKeyValidationFailureCodes.InsufficientBalance => "Budget depleted",
                        VirtualKeyValidationFailureCodes.ModelNotAllowed when !string.IsNullOrEmpty(requestedModel)
                            => $"Model {requestedModel} is not allowed for this key",
                        _ => validationResult.Reason
                    };
                    return result;
                }

                // All validations passed
                result.IsValid = true;
                result.VirtualKeyId = virtualKey.Id;
                result.KeyName = virtualKey.KeyName;
                result.AllowedModels = VirtualKeyUtilities.ParseAllowedModels(virtualKey.AllowedModels);
                // Budget info is now at group level, not included in validation result

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate virtual key for model {Model}", LoggingSanitizer.S(requestedModel ?? "any"));
                result.ErrorMessage = "Validation failed due to an internal error";
                return result;
            }
        }

        /// <inheritdoc />
        public async Task<VirtualKeyValidationInfoDto?> GetValidationInfoAsync(int id)
        {
            _logger.LogDebug("Getting validation info for virtual key ID {KeyId}", id);

            var key = await _virtualKeyRepository.GetByIdAsync(id);
            if (key == null)
            {
                return null;
            }

            return new VirtualKeyValidationInfoDto
            {
                Id = key.Id,
                KeyName = key.KeyName,
                AllowedModels = VirtualKeyUtilities.ParseAllowedModels(key.AllowedModels),
                VirtualKeyGroupId = key.VirtualKeyGroupId,
                IsEnabled = key.IsEnabled,
                ExpiresAt = key.ExpiresAt,
                RateLimitRpm = key.RateLimitRpm,
                RateLimitRpd = key.RateLimitRpd,
                RateLimitTpm = key.RateLimitTpm,
                MaxParallelRequests = key.MaxParallelRequests,
                RateLimitPriority = key.RateLimitPriority
            };
        }
    }
}
