using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using VirtualKeyUtilities = ConduitLLM.Configuration.Utilities.VirtualKeyUtilities;

using ConduitLLM.Configuration.Messaging;
using IVirtualKeyService = ConduitLLM.Core.Interfaces.IVirtualKeyService;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Lightweight Gateway implementation of IVirtualKeyService that extends VirtualKeyServiceBase
    /// for shared CRUD and adds Gateway-specific validation and spend tracking.
    /// </summary>
    /// <remarks>
    /// All CRUD operations (Generate, Get, List, Update, Delete, ResetSpend) are inherited
    /// from <see cref="VirtualKeyServiceBase"/>. This class only implements the four
    /// Gateway-specific methods: authentication validation, full validation, spend updates,
    /// and raw entity retrieval for validation.
    /// </remarks>
    public class DirectApiVirtualKeyService : VirtualKeyServiceBase, IVirtualKeyService
    {
        private readonly IBatchSpendUpdateService? _batchSpendService;

        /// <summary>
        /// Initializes a new instance of the DirectApiVirtualKeyService
        /// </summary>
        public DirectApiVirtualKeyService(
            IVirtualKeyRepository virtualKeyRepository,
            IVirtualKeyGroupRepository groupRepository,
            IVirtualKeySpendHistoryRepository spendHistoryRepository,
            IEventBus? eventBus,
            ILogger<DirectApiVirtualKeyService> logger,
            IBatchSpendUpdateService? batchSpendService = null)
            : base(virtualKeyRepository, groupRepository, spendHistoryRepository, eventBus, logger)
        {
            _batchSpendService = batchSpendService;
        }

        /// <inheritdoc />
        public Task<VirtualKeyValidationOutcome> ValidateVirtualKeyForAuthenticationAsync(
            string key,
            string? requestedModel = null)
        {
            return ValidateInternalAsync(key, requestedModel, checkBalance: false);
        }

        /// <inheritdoc />
        public Task<VirtualKeyValidationOutcome> ValidateVirtualKeyAsync(
            string key,
            string? requestedModel = null)
        {
            return ValidateInternalAsync(key, requestedModel, checkBalance: true);
        }

        private Task<VirtualKeyValidationOutcome> ValidateInternalAsync(
            string key,
            string? requestedModel,
            bool checkBalance)
        {
            return ValidateVirtualKeyInternalAsync(
                key,
                requestedModel,
                checkBalance,
                hash => VirtualKeyRepository.GetByKeyHashAsync(hash),
                _batchSpendService);
        }

        /// <inheritdoc />
        public Task<bool> UpdateSpendAsync(int keyId, decimal cost) =>
            UpdateSpendAsync(keyId, cost, billedAtUtc: null);

        /// <inheritdoc />
        public async Task<bool> UpdateSpendAsync(int keyId, decimal cost, DateTime? billedAtUtc)
        {
            if (cost <= 0) return true; // No cost to add, consider it successful

            var virtualKey = await VirtualKeyRepository.GetByIdAsync(keyId);
            if (virtualKey == null) return false;

            try
            {
                // Get the key's group
                var group = await GroupRepository.GetByIdAsync(virtualKey.VirtualKeyGroupId);
                if (group == null)
                {
                    Logger.LogError("Virtual key {KeyId} has invalid group ID {GroupId}", keyId, virtualKey.VirtualKeyGroupId);
                    return false;
                }

                // Update the group balance
                var newBalance = await GroupRepository.AdjustBalanceAsync(
                    group.Id,
                    -cost,
                    $"API usage by virtual key #{keyId}",
                    "System",
                    ReferenceType.VirtualKey,
                    keyId.ToString(),
                    ConduitLLM.Configuration.Utilities.BillingTimeUtilities.GetBillingWindowStartUtc(billedAtUtc));

                // Update virtual key timestamp
                virtualKey.UpdatedAt = DateTime.UtcNow;
                bool success = await VirtualKeyRepository.UpdateAsync(virtualKey);

                if (success)
                {
                    Logger.LogInformation("Updated spend for key ID {KeyId} in group {GroupId}. New balance: {Balance}",
                        keyId, group.Id, newBalance);
                }

                return success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                "Error updating spend for key ID {KeyId}.",
                keyId);
                return false;
            }
        }

        /// <inheritdoc />
        public async Task<VirtualKey?> GetVirtualKeyInfoForValidationAsync(int keyId, CancellationToken cancellationToken = default)
        {
            return await VirtualKeyRepository.GetByIdAsync(keyId, cancellationToken);
        }
    }
}
