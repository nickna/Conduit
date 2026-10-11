using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using VirtualKeyUtilities = ConduitLLM.Configuration.Utilities.VirtualKeyUtilities;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Configuration.Messaging;

using IVirtualKeyService = ConduitLLM.Core.Interfaces.IVirtualKeyService;
namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// High-performance Virtual Key service with Redis caching and immediate invalidation
    /// Maintains security guarantees while providing ~50x performance improvement
    /// </summary>
    public class CachedApiVirtualKeyService : VirtualKeyServiceBase, IVirtualKeyService
    {
        private readonly ConduitLLM.Core.Interfaces.IVirtualKeyCache _cache;
        private readonly ILogger<CachedApiVirtualKeyService> _logger;
        private readonly IBatchSpendUpdateService? _batchSpendService;

        public CachedApiVirtualKeyService(
            IVirtualKeyRepository virtualKeyRepository,
            IVirtualKeySpendHistoryRepository spendHistoryRepository,
            IVirtualKeyGroupRepository groupRepository,
            ConduitLLM.Core.Interfaces.IVirtualKeyCache cache,
            IEventBus? eventBus,
            ILogger<CachedApiVirtualKeyService> logger,
            IBatchSpendUpdateService? batchSpendService = null)
            : base(virtualKeyRepository, groupRepository, spendHistoryRepository, eventBus, logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _batchSpendService = batchSpendService;
        }

        #region Virtual Key Hooks (Cache Invalidation)

        protected override async Task OnVirtualKeyUpdatedAsync(VirtualKey key, string[] changedProperties)
        {
            await _cache.InvalidateVirtualKeyAsync(key.KeyHash);
        }

        protected override async Task OnVirtualKeyDeletedAsync(VirtualKey key)
        {
            await _cache.InvalidateVirtualKeyAsync(key.KeyHash);
        }

        #endregion

        /// <summary>
        /// Validates virtual key for authentication only (no balance check)
        /// </summary>
        /// <param name="key">The virtual key to validate</param>
        /// <param name="requestedModel">Optional model to check against allowed models</param>
        /// <returns>A typed validation outcome. Balance is never checked by this method.</returns>
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
                keyHash => _cache.GetVirtualKeyAsync(keyHash, async hash =>
                {
                    var dbKey = await VirtualKeyRepository.GetByKeyHashAsync(hash);
                    _logger.LogDebug("Database fallback executed for Virtual Key validation");
                    return dbKey;
                }),
                _batchSpendService);
        }

        /// <inheritdoc />
        public Task<bool> UpdateSpendAsync(int keyId, decimal cost) =>
            UpdateSpendAsync(keyId, cost, billedAtUtc: null);

        /// <inheritdoc />
        public async Task<bool> UpdateSpendAsync(int keyId, decimal cost, DateTime? billedAtUtc)
        {
            if (cost <= 0)
            {
                _logger.LogDebug("Spend update for key {KeyId} has zero or negative cost {Cost} - skipping", keyId, cost);
                return true; // No cost to add, consider it successful
            }

            try
            {
                var billingTimestamp = ConduitLLM.Configuration.Utilities.BillingTimeUtilities.NormalizeUtc(billedAtUtc);
                if (IsEventPublishingEnabled)
                {
                    // Event-driven approach - publish SpendUpdateRequested event
                    var requestId = Guid.NewGuid().ToString();

                    var published = await TryPublishEventAsync(
                        new SpendUpdateRequested
                        {
                            KeyId = keyId,
                            Amount = cost,
                            BilledAtUtc = billingTimestamp,
                            RequestId = requestId,
                            CorrelationId = Guid.NewGuid().ToString()
                        },
                        $"spend update for key {keyId}",
                        new { KeyId = keyId, Amount = cost, RequestId = requestId });

                    if (published)
                    {
                        // Event-driven approach returns true immediately - processing happens asynchronously
                        // The SpendUpdateProcessor will handle the actual database update and cache invalidation
                        return true;
                    }

                    // Publish failure fallback (#927): charge directly instead of losing the
                    // spend. The same RequestId-derived idempotency key is written to the
                    // ledger, so if the event WAS actually delivered despite the reported
                    // failure, the consumer's dedup makes it a no-op — no double charge.
                    _logger.LogError(
                        "Spend update event for key {KeyId} (requestId {RequestId}) could not be published - falling back to direct balance adjustment",
                        keyId, requestId);

                    return await UpdateSpendDirectAsync(keyId, cost, SpendIdempotency.KeyFor(requestId), billingTimestamp);
                }
                else
                {
                    // FALLBACK: Direct database update approach when event bus not configured
                    _logger.LogDebug("Event publishing not configured - using direct database update for key {KeyId}", keyId);

                    return await UpdateSpendDirectAsync(keyId, cost, idempotencyKey: null, billingTimestamp);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating spend for key ID {KeyId}.", keyId);
                return false;
            }
        }

        /// <summary>
        /// Adjusts the key's group balance directly, bypassing the event bus. Used when
        /// event publishing is not configured, and as the durability fallback when a
        /// spend publish fails (#927) — in the latter case with the RequestId-derived
        /// idempotency key so the adjustment applies at most once across both paths.
        /// </summary>
        private async Task<bool> UpdateSpendDirectAsync(
            int keyId, decimal cost, string? idempotencyKey, DateTime billedAtUtc)
        {
            var virtualKey = await VirtualKeyRepository.GetByIdAsync(keyId);
            if (virtualKey == null)
            {
                _logger.LogWarning("Virtual key {KeyId} not found for spend update", keyId);
                return false;
            }

            // Get the key's group and adjust its balance
            var group = await GroupRepository.GetByKeyIdAsync(keyId);
            if (group == null)
            {
                _logger.LogWarning("No group found for virtual key with ID {KeyId}", keyId);
                return false;
            }

            decimal newBalance;
            var billingWindow = ConduitLLM.Configuration.Utilities.BillingTimeUtilities.GetBillingWindowStartUtc(billedAtUtc);
            if (idempotencyKey != null)
            {
                var result = await GroupRepository.AdjustBalanceIdempotentAsync(
                    group.Id,
                    -cost,
                    idempotencyKey,
                    $"API usage by virtual key #{keyId}",
                    "System",
                    ReferenceType.VirtualKey,
                    keyId.ToString(),
                    billingWindow);
                newBalance = result.NewBalance;
            }
            else
            {
                newBalance = await GroupRepository.AdjustBalanceAsync(
                    group.Id,
                    -cost,
                    $"API usage by virtual key #{keyId}",
                    "System",
                    ReferenceType.VirtualKey,
                    keyId.ToString(),
                    billingWindow);
            }

            // Invalidate cache after spend update
            await _cache.InvalidateVirtualKeyAsync(virtualKey.KeyHash);

            _logger.LogInformation("Updated spend for key ID {KeyId} in group {GroupId}. New balance: {NewBalance}",
                keyId, group.Id, newBalance);

            return true;
        }

        /// <inheritdoc />
        public async Task<VirtualKey?> GetVirtualKeyInfoForValidationAsync(int keyId, CancellationToken cancellationToken = default)
        {
            return await VirtualKeyRepository.GetByIdAsync(keyId, cancellationToken);
        }

        /// <summary>
        /// Get cache performance statistics
        /// </summary>
        public async Task<CacheStats> GetCacheStatsAsync()
        {
            return await _cache.GetStatsAsync();
        }
    }
}
