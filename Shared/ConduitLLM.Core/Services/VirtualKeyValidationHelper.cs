using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Helper class containing shared virtual key validation logic
    /// </summary>
    public static class VirtualKeyValidationHelper
    {
        /// <summary>
        /// Resolves and validates a virtual key without coupling the validation policy
        /// to a particular persistence backend.
        /// </summary>
        /// <param name="key">The plaintext virtual key supplied by the caller.</param>
        /// <param name="requestedModel">The requested model, if any.</param>
        /// <param name="checkBalance">Whether to check the resolved group balance.</param>
        /// <param name="lookupByHashAsync">Fixed-shape lookup supplied by the active backend.</param>
        /// <param name="getBalanceSnapshotAsync">
        /// Optional lazy balance resolver. It is invoked only after the key passes the
        /// enabled and expiration checks.
        /// </param>
        /// <param name="logger">Logger for diagnostic output.</param>
        public static async Task<VirtualKeyValidationOutcome> ValidateKeyAsync(
            string key,
            string? requestedModel,
            bool checkBalance,
            Func<string, Task<VirtualKey?>> lookupByHashAsync,
            Func<VirtualKey, Task<(VirtualKeyGroup? Group, decimal PendingSpend)>>? getBalanceSnapshotAsync,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                logger.LogWarning("Empty key provided for virtual key validation");
                return VirtualKeyValidationOutcome.Failure(
                    VirtualKeyValidationFailureCodes.MissingKey,
                    401,
                    "Virtual key is required.");
            }

            try
            {
                var keyHash = ConduitLLM.Configuration.Utilities.VirtualKeyUtilities.HashKey(key);
                logger.LogDebug("Validating key ({ValidationMode}): {KeyPrefix}, Hash: {Hash}",
                    checkBalance ? "balance" : "authentication",
                    LoggingSanitizer.S(ConduitLLM.Core.Utilities.SpanHelper.MaskSecret(key)),
                    keyHash);

                var virtualKey = await lookupByHashAsync(keyHash);
                if (virtualKey == null)
                {
                    logger.LogWarning("No matching virtual key found for hash: {Hash}", keyHash);
                    return VirtualKeyValidationOutcome.Failure(
                        VirtualKeyValidationFailureCodes.KeyNotFound,
                        401,
                        "Virtual key was not found.");
                }

                var result = await ValidateVirtualKeyAsync(
                    virtualKey,
                    requestedModel,
                    checkBalance,
                    getBalanceSnapshotAsync,
                    logger);

                if (!result.IsValid)
                {
                    logger.LogWarning("Virtual key {KeyId} validation failed: {Reason}",
                        virtualKey.Id, result.Reason ?? "unknown");
                }
                else
                {
                    logger.LogDebug("Virtual key {KeyId} validated successfully for model: {Model}",
                        virtualKey.Id, LoggingSanitizer.S(requestedModel ?? "any"));
                }

                return result;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error validating virtual key");
                return VirtualKeyValidationOutcome.Failure(
                    VirtualKeyValidationFailureCodes.ValidationError,
                    500,
                    "Virtual key validation failed.");
            }
        }

        /// <summary>
        /// Validates a virtual key with common checks
        /// </summary>
        /// <param name="virtualKey">The virtual key to validate</param>
        /// <param name="requestedModel">The requested model, if any</param>
        /// <param name="checkBalance">Whether to check the group balance</param>
        /// <param name="getBalanceSnapshotAsync">Lazy provider for the persisted balance and pending spend.</param>
        /// <param name="logger">Logger for diagnostic output</param>
        /// <returns>Validation result with status and error message if failed</returns>
        public static async Task<VirtualKeyValidationOutcome> ValidateVirtualKeyAsync(
            VirtualKey virtualKey,
            string? requestedModel,
            bool checkBalance,
            Func<VirtualKey, Task<(VirtualKeyGroup? Group, decimal PendingSpend)>>? getBalanceSnapshotAsync,
            ILogger logger)
        {
            // Check if key is enabled
            if (!virtualKey.IsEnabled)
            {
                logger.LogWarning("Virtual key is disabled: {KeyName} (ID: {KeyId})",
                    LoggingSanitizer.S(virtualKey.KeyName), virtualKey.Id);
                return VirtualKeyValidationOutcome.Failure(
                    VirtualKeyValidationFailureCodes.KeyDisabled,
                    401,
                    "Virtual key is disabled.",
                    virtualKey);
            }

            // Check expiration
            if (virtualKey.ExpiresAt.HasValue && virtualKey.ExpiresAt.Value < DateTime.UtcNow)
            {
                logger.LogWarning("Virtual key has expired: {KeyName} (ID: {KeyId}), expired at {ExpiryDate}",
                    LoggingSanitizer.S(virtualKey.KeyName), virtualKey.Id, virtualKey.ExpiresAt);
                return VirtualKeyValidationOutcome.Failure(
                    VirtualKeyValidationFailureCodes.KeyExpired,
                    401,
                    "Virtual key has expired.",
                    virtualKey);
            }

            // Check group balance if requested
            if (checkBalance && getBalanceSnapshotAsync != null)
            {
                var (group, pendingSpend) = await getBalanceSnapshotAsync(virtualKey);
                var availableBalance = group?.Balance - pendingSpend;
                if (group != null && availableBalance <= 0)
                {
                    logger.LogWarning("Virtual key group budget depleted: {KeyName} (ID: {KeyId}), group {GroupId} has database balance {Balance} and pending spend {PendingSpend}",
                        LoggingSanitizer.S(virtualKey.KeyName), virtualKey.Id, group.Id, group.Balance, pendingSpend);

                    return VirtualKeyValidationOutcome.Failure(
                        VirtualKeyValidationFailureCodes.InsufficientBalance,
                        402,
                        "Your account balance is insufficient to perform this operation.",
                        virtualKey);
                }
            }

            // Check if model is allowed
            if (!string.IsNullOrEmpty(requestedModel) && !string.IsNullOrEmpty(virtualKey.AllowedModels))
            {
                bool isModelAllowed = ConduitLLM.Configuration.Utilities.VirtualKeyUtilities.IsModelAllowed(requestedModel, virtualKey.AllowedModels);
                if (!isModelAllowed)
                {
                    logger.LogWarning("Virtual key {KeyName} (ID: {KeyId}) attempted to access restricted model: {RequestedModel}",
                        LoggingSanitizer.S(virtualKey.KeyName), virtualKey.Id, LoggingSanitizer.S(requestedModel));
                    return VirtualKeyValidationOutcome.Failure(
                        VirtualKeyValidationFailureCodes.ModelNotAllowed,
                        403,
                        "The requested model is not allowed for this virtual key.",
                        virtualKey);
                }
            }

            // All validations passed
            var logLevel = checkBalance ? LogLevel.Information : LogLevel.Debug;
            if (logLevel == LogLevel.Information)
            {
                logger.LogInformation("Validated virtual key successfully: {KeyName} (ID: {KeyId})",
                    LoggingSanitizer.S(virtualKey.KeyName), virtualKey.Id);
            }
            else
            {
                logger.LogDebug("Virtual key authenticated successfully: {KeyName} (ID: {KeyId})",
                    LoggingSanitizer.S(virtualKey.KeyName), virtualKey.Id);
            }

            return VirtualKeyValidationOutcome.Success(virtualKey);
        }
    }
}
