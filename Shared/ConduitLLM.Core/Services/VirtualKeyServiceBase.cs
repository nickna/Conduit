using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Utilities;

using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

using Microsoft.Extensions.Logging;
using System.Text.Json;
using VirtualKeyUtilities = ConduitLLM.Configuration.Utilities.VirtualKeyUtilities;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Base class for virtual key services providing shared CRUD operations with
    /// event publishing and extensibility hooks for caching and media cleanup.
    /// </summary>
    public abstract class VirtualKeyServiceBase : EventPublishingServiceBase
    {
        protected readonly IVirtualKeyRepository VirtualKeyRepository;
        protected readonly IVirtualKeyGroupRepository GroupRepository;
        protected readonly IVirtualKeySpendHistoryRepository SpendHistoryRepository;

        protected VirtualKeyServiceBase(
            IVirtualKeyRepository virtualKeyRepository,
            IVirtualKeyGroupRepository groupRepository,
            IVirtualKeySpendHistoryRepository spendHistoryRepository,
            IEventBus? eventBus,
            ILogger logger)
            : base(eventBus, logger)
        {
            VirtualKeyRepository = virtualKeyRepository ?? throw new ArgumentNullException(nameof(virtualKeyRepository));
            GroupRepository = groupRepository ?? throw new ArgumentNullException(nameof(groupRepository));
            SpendHistoryRepository = spendHistoryRepository ?? throw new ArgumentNullException(nameof(spendHistoryRepository));
        }

        /// <summary>
        /// Shared key-validation flow: hash the key, resolve the entity via
        /// <paramref name="lookupByHashAsync"/>, then run <see cref="VirtualKeyValidationHelper"/>.
        /// Subclasses supply the lookup (direct repository or cache-backed).
        /// </summary>
        protected async Task<VirtualKeyValidationOutcome> ValidateVirtualKeyInternalAsync(
            string key,
            string? requestedModel,
            bool checkBalance,
            Func<string, Task<VirtualKey?>> lookupByHashAsync,
            IBatchSpendUpdateService? batchSpendService)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                Logger.LogWarning("Empty key provided for virtual key validation");
                return VirtualKeyValidationOutcome.Failure(
                    VirtualKeyValidationFailureCodes.MissingKey,
                    401,
                    "Virtual key is required.");
            }

            try
            {
                var keyHash = VirtualKeyUtilities.HashKey(key);
                Logger.LogDebug("Validating key ({ValidationMode}): {KeyPrefix}, Hash: {Hash}",
                    checkBalance ? "balance" : "authentication",
                    LoggingSanitizer.S(ConduitLLM.Core.Utilities.SpanHelper.MaskSecret(key)),
                    keyHash);

                var virtualKey = await lookupByHashAsync(keyHash);
                if (virtualKey == null)
                {
                    Logger.LogWarning("No matching virtual key found for hash: {Hash}", keyHash);
                    return VirtualKeyValidationOutcome.Failure(
                        VirtualKeyValidationFailureCodes.KeyNotFound,
                        401,
                        "Virtual key was not found.");
                }

                var result = await VirtualKeyValidationHelper.ValidateVirtualKeyAsync(
                    virtualKey,
                    requestedModel,
                    checkBalance,
                    checkBalance ? GroupRepository : null,
                    Logger,
                    checkBalance ? batchSpendService : null);

                if (!result.IsValid)
                {
                    Logger.LogWarning("Virtual key {KeyId} validation failed: {Reason}",
                        virtualKey.Id, result.Reason ?? "unknown");
                }
                else
                {
                    Logger.LogDebug("Virtual key {KeyId} validated successfully for model: {Model}",
                        virtualKey.Id, LoggingSanitizer.S(requestedModel ?? "any"));
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error validating virtual key");
                return VirtualKeyValidationOutcome.Failure(
                    VirtualKeyValidationFailureCodes.ValidationError,
                    500,
                    "Virtual key validation failed.");
            }
        }

        #region Virtual Hooks

        /// <summary>Called after a virtual key is created and saved to the database.</summary>
        protected virtual Task OnVirtualKeyCreatedAsync(VirtualKey key) => Task.CompletedTask;

        /// <summary>Called after a virtual key is updated. Subclasses can use this for cache invalidation.</summary>
        protected virtual Task OnVirtualKeyUpdatedAsync(VirtualKey key, string[] changedProperties) => Task.CompletedTask;

        /// <summary>Called before a virtual key is deleted. Subclasses can use this for media cleanup.</summary>
        protected virtual Task OnBeforeVirtualKeyDeleteAsync(int keyId) => Task.CompletedTask;

        protected virtual Task OnBeforeVirtualKeyDeleteAsync(int keyId, CancellationToken cancellationToken)
            => OnBeforeVirtualKeyDeleteAsync(keyId);

        /// <summary>Called after a virtual key is deleted. Subclasses can use this for cache invalidation.</summary>
        protected virtual Task OnVirtualKeyDeletedAsync(VirtualKey key) => Task.CompletedTask;

        #endregion

        #region CRUD Operations

        public virtual async Task<CreateVirtualKeyResponseDto> GenerateVirtualKeyAsync(CreateVirtualKeyRequestDto request)
        {
            var keyValue = VirtualKeyUtilities.GenerateSecureKey();
            var keyWithPrefix = VirtualKeyConstants.KeyPrefix + keyValue;
            var keyHash = VirtualKeyUtilities.HashKey(keyWithPrefix);

            // Verify the group exists
            var existingGroup = await GroupRepository.GetByIdAsync(request.VirtualKeyGroupId);
            if (existingGroup == null)
            {
                throw new InvalidOperationException(
                    $"Virtual key group {request.VirtualKeyGroupId} not found. Ensure the group exists before creating keys.");
            }

            if (existingGroup.Balance <= 0)
            {
                Logger.LogWarning(
                    "Virtual key group {GroupId} has zero balance. Keys in this group cannot make API calls until funded.",
                    request.VirtualKeyGroupId);
            }

            var virtualKey = new VirtualKey
            {
                KeyName = request.KeyName ?? string.Empty,
                KeyHash = keyHash,
                AllowedModels = request.AllowedModels is { Count: > 0 }
                    ? string.Join(',', request.AllowedModels)
                    : null,
                VirtualKeyGroupId = existingGroup.Id,
                IsEnabled = true,
                ExpiresAt = request.ExpiresAt,
                Metadata = request.Metadata is null
                    ? null
                    : JsonSerializer.Serialize(request.Metadata, CoreHttpJsonContext.Default.DictionaryStringJsonElement),
                RateLimitRpm = request.RateLimitRpm,
                RateLimitRpd = request.RateLimitRpd,
                RateLimitTpm = request.RateLimitTpm,
                MaxParallelRequests = request.MaxParallelRequests,
                RateLimitPriority = request.RateLimitPriority,
                ModelRateLimits = VirtualKeyUtilities.SerializeModelRateLimits(request.ModelRateLimits),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var createdId = await VirtualKeyRepository.CreateAsync(virtualKey);

            virtualKey = ReadBackGuard.RequireCreated(
                await VirtualKeyRepository.GetByIdAsync(createdId),
                "virtual key",
                createdId);

            Logger.LogInformation("Created new virtual key: {KeyName} (ID: {KeyId})",
                LoggingSanitizer.S(virtualKey.KeyName), virtualKey.Id);

            await PublishEventAsync(
                new VirtualKeyCreated
                {
                    KeyId = virtualKey.Id,
                    KeyHash = virtualKey.KeyHash,
                    KeyName = virtualKey.KeyName,
                    CreatedAt = virtualKey.CreatedAt,
                    IsEnabled = virtualKey.IsEnabled,
                    AllowedModels = virtualKey.AllowedModels,
                    VirtualKeyGroupId = virtualKey.VirtualKeyGroupId,
                    CorrelationId = Guid.NewGuid().ToString()
                },
                $"create virtual key {virtualKey.Id}",
                new { KeyName = virtualKey.KeyName });

            await OnVirtualKeyCreatedAsync(virtualKey);

            return new CreateVirtualKeyResponseDto
            {
                VirtualKey = keyWithPrefix,
                KeyInfo = VirtualKeyUtilities.MapToDto(virtualKey)
            };
        }

        public virtual async Task<VirtualKeyDto?> GetVirtualKeyInfoAsync(int id)
        {
            var virtualKey = await VirtualKeyRepository.GetByIdAsync(id);
            if (virtualKey == null)
            {
                Logger.LogWarning("Virtual key with ID {KeyId} not found", id);
                return null;
            }

            return VirtualKeyUtilities.MapToDto(virtualKey);
        }

        public virtual async Task<List<VirtualKeyDto>> ListVirtualKeysAsync()
        {
            var virtualKeys = await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
                VirtualKeyRepository.GetPaginatedAsync);
            Logger.LogDebug("Listed {Count} virtual keys", virtualKeys.Count);
            return [.. virtualKeys.Select(VirtualKeyUtilities.MapToDto)];
        }

        public virtual async Task<bool> UpdateVirtualKeyAsync(int id, UpdateVirtualKeyRequestDto request)
        {
            var key = await VirtualKeyRepository.GetByIdAsync(id);
            if (key == null)
            {
                Logger.LogWarning("Virtual key with ID {KeyId} not found for update", id);
                return false;
            }

            // Track actual changes
            var changedProperties = new List<string>();

            if (request.HasKeyName)
            {
                if (string.IsNullOrWhiteSpace(request.KeyName))
                    throw new InvalidOperationException("Virtual key name cannot be null or empty.");
                if (key.KeyName != request.KeyName)
                {
                    key.KeyName = request.KeyName;
                    changedProperties.Add(nameof(key.KeyName));
                }
            }

            if (request.HasAllowedModels)
            {
                var allowedModels = request.AllowedModels is null or { Count: 0 }
                    ? null
                    : string.Join(',', request.AllowedModels);
                if (key.AllowedModels != allowedModels)
                {
                    key.AllowedModels = allowedModels;
                    changedProperties.Add(nameof(key.AllowedModels));
                }
            }

            if (request.HasVirtualKeyGroupId)
            {
                if (!request.VirtualKeyGroupId.HasValue)
                    throw new InvalidOperationException("Virtual key group ID cannot be null.");
                if (key.VirtualKeyGroupId != request.VirtualKeyGroupId.Value)
                {
                    var newGroup = await GroupRepository.GetByIdAsync(request.VirtualKeyGroupId.Value);
                    if (newGroup == null)
                    {
                        throw new InvalidOperationException(
                            $"Virtual key group with ID {request.VirtualKeyGroupId.Value} not found");
                    }
                    key.VirtualKeyGroupId = request.VirtualKeyGroupId.Value;
                    changedProperties.Add(nameof(key.VirtualKeyGroupId));
                }
            }

            if (request.HasIsEnabled)
            {
                if (!request.IsEnabled.HasValue)
                    throw new InvalidOperationException("Virtual key enabled state cannot be null.");
                if (key.IsEnabled != request.IsEnabled.Value)
                {
                    key.IsEnabled = request.IsEnabled.Value;
                    changedProperties.Add(nameof(key.IsEnabled));
                }
            }

            if (request.HasExpiresAt && key.ExpiresAt != request.ExpiresAt)
            {
                key.ExpiresAt = request.ExpiresAt;
                changedProperties.Add(nameof(key.ExpiresAt));
            }

            if (request.HasMetadata)
            {
                var metadata = request.Metadata is null or { Count: 0 }
                    ? null
                    : JsonSerializer.Serialize(request.Metadata, CoreHttpJsonContext.Default.DictionaryStringJsonElement);
                if (key.Metadata != metadata)
                {
                    key.Metadata = metadata;
                    changedProperties.Add(nameof(key.Metadata));
                }
            }

            if (request.HasRateLimitRpm && key.RateLimitRpm != request.RateLimitRpm)
            {
                key.RateLimitRpm = request.RateLimitRpm;
                changedProperties.Add(nameof(key.RateLimitRpm));
            }

            if (request.HasRateLimitRpd && key.RateLimitRpd != request.RateLimitRpd)
            {
                key.RateLimitRpd = request.RateLimitRpd;
                changedProperties.Add(nameof(key.RateLimitRpd));
            }

            if (request.HasRateLimitTpm && key.RateLimitTpm != request.RateLimitTpm)
            {
                key.RateLimitTpm = request.RateLimitTpm;
                changedProperties.Add(nameof(key.RateLimitTpm));
            }

            if (request.HasMaxParallelRequests && key.MaxParallelRequests != request.MaxParallelRequests)
            {
                key.MaxParallelRequests = request.MaxParallelRequests;
                changedProperties.Add(nameof(key.MaxParallelRequests));
            }

            if (request.HasRateLimitPriority && key.RateLimitPriority != request.RateLimitPriority)
            {
                key.RateLimitPriority = request.RateLimitPriority;
                changedProperties.Add(nameof(key.RateLimitPriority));
            }

            if (request.HasModelRateLimits)
            {
                // The Admin merge-patch adapter has already merged object members. Null or an
                // empty object clears every override.
                var serialized = VirtualKeyUtilities.SerializeModelRateLimits(request.ModelRateLimits);
                if (key.ModelRateLimits != serialized)
                {
                    key.ModelRateLimits = serialized;
                    changedProperties.Add(nameof(key.ModelRateLimits));
                }
            }

            if (!changedProperties.Any())
            {
                Logger.LogDebug("No changes detected for virtual key {KeyId} — skipping update", id);
                return true;
            }

            key.UpdatedAt = DateTime.UtcNow;
            var success = await VirtualKeyRepository.UpdateAsync(key);

            if (success)
            {
                var changed = changedProperties.ToArray();

                await PublishEventAsync(
                    new VirtualKeyUpdated
                    {
                        KeyId = key.Id,
                        KeyHash = key.KeyHash,
                        ChangedProperties = changed,
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"update virtual key {id}",
                    new { ChangedProperties = string.Join(", ", changed) });

                await OnVirtualKeyUpdatedAsync(key, changed);

                Logger.LogInformation("Updated virtual key {KeyId} ({KeyName}), changed: [{ChangedProperties}]",
                    id, LoggingSanitizer.S(key.KeyName), string.Join(", ", changed));
            }

            return success;
        }

        public virtual Task<bool> DeleteVirtualKeyAsync(int id) => DeleteVirtualKeyCoreAsync(id, CancellationToken.None);

        protected async Task<bool> DeleteVirtualKeyCoreAsync(int id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await VirtualKeyRepository.GetByIdAsync(id);
            if (key == null)
            {
                Logger.LogWarning("Virtual key with ID {KeyId} not found for deletion", id);
                return false;
            }

            await OnBeforeVirtualKeyDeleteAsync(id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var success = await VirtualKeyRepository.DeleteAsync(id);

            if (success)
            {
                await PublishEventAsync(
                    new VirtualKeyDeleted
                    {
                        KeyId = key.Id,
                        KeyHash = key.KeyHash,
                        KeyName = key.KeyName,
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"delete virtual key {key.Id}",
                    new { KeyName = key.KeyName });

                await OnVirtualKeyDeletedAsync(key);

                Logger.LogInformation("Deleted virtual key {KeyId} ({KeyName})",
                    id, LoggingSanitizer.S(key.KeyName));
            }

            return success;
        }

        public virtual async Task<bool> ResetSpendAsync(int id)
        {
            var virtualKey = await VirtualKeyRepository.GetByIdAsync(id);
            if (virtualKey == null) return false;

            var group = await GroupRepository.GetByIdAsync(virtualKey.VirtualKeyGroupId);
            if (group == null)
            {
                Logger.LogError("Virtual key {KeyId} has invalid group ID {GroupId}",
                    id, virtualKey.VirtualKeyGroupId);
                return false;
            }

            if (group.LifetimeSpent > 0)
            {
                var spendHistory = new VirtualKeySpendHistory
                {
                    VirtualKeyId = virtualKey.Id,
                    Amount = group.LifetimeSpent,
                    Date = DateTime.UtcNow
                };
                await SpendHistoryRepository.CreateAsync(spendHistory);

                group.LifetimeSpent = 0;
                group.UpdatedAt = DateTime.UtcNow;
                await GroupRepository.UpdateAsync(group);
            }

            virtualKey.UpdatedAt = DateTime.UtcNow;
            return await VirtualKeyRepository.UpdateAsync(virtualKey);
        }

        #endregion
    }
}
