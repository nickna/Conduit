using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Request-time virtual-key service backed by a fixed-shape persistence store.
/// </summary>
/// <remarks>
/// The runtime contract deliberately maps persistence snapshots into the legacy
/// request entity. This lets existing authentication, rate-limit, and media code
/// keep its stable in-memory contract without depending on EF repositories.
/// </remarks>
public sealed class StoreBackedVirtualKeyRuntimeService : IVirtualKeyRuntimeService
{
    private readonly IVirtualKeyRuntimeStore _store;
    private readonly IBatchSpendUpdateService? _batchSpendService;
    private readonly ILogger<StoreBackedVirtualKeyRuntimeService> _logger;

    public StoreBackedVirtualKeyRuntimeService(
        IVirtualKeyRuntimeStore store,
        ILogger<StoreBackedVirtualKeyRuntimeService> logger,
        IBatchSpendUpdateService? batchSpendService = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _batchSpendService = batchSpendService;
    }

    /// <inheritdoc />
    public Task<VirtualKeyValidationOutcome> ValidateVirtualKeyForAuthenticationAsync(
        string key,
        string? requestedModel = null) =>
        ValidateInternalAsync(key, requestedModel, checkBalance: false);

    /// <inheritdoc />
    public Task<VirtualKeyValidationOutcome> ValidateVirtualKeyAsync(
        string key,
        string? requestedModel = null) =>
        ValidateInternalAsync(key, requestedModel, checkBalance: true);

    /// <inheritdoc />
    public async Task<bool> UpdateSpendAsync(int keyId, decimal cost)
    {
        if (cost <= 0)
        {
            _logger.LogDebug(
                "Spend update for key {KeyId} has zero or negative cost {Cost} - skipping",
                keyId,
                cost);
            return true;
        }

        try
        {
            var record = await _store.GetByIdAsync(keyId);
            if (record == null)
            {
                _logger.LogWarning("Virtual key {KeyId} not found for spend update", keyId);
                return false;
            }

            var billingTimestamp = DateTime.UtcNow;
            var adjustment = await _store.AdjustBalanceAsync(
                new VirtualKeyBalanceAdjustment(
                    record.Group.Id,
                    -cost,
                    $"API usage by virtual key #{keyId}",
                    "System",
                    VirtualKeyBalanceReferenceType.VirtualKey,
                    keyId.ToString(),
                    BillingWindowStartUtc: billingTimestamp.Date.AddHours(billingTimestamp.Hour)));

            // The charge is already durable at this point. Timestamp maintenance is
            // best-effort so a concurrent key deletion cannot turn a successful debit
            // into a reported failure and cause the fallback queue to debit it again.
            try
            {
                if (!await _store.TouchAsync(keyId, DateTime.UtcNow))
                {
                    _logger.LogWarning(
                        "Virtual key {KeyId} disappeared after its spend was persisted",
                        keyId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Spend was persisted for virtual key {KeyId}, but its timestamp could not be updated",
                    keyId);
            }

            _logger.LogInformation(
                "Updated spend for key ID {KeyId} in group {GroupId}. New balance: {Balance}",
                keyId,
                record.Group.Id,
                adjustment.NewBalance);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating spend for key ID {KeyId}.", keyId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<VirtualKey?> GetVirtualKeyInfoForValidationAsync(
        int keyId,
        CancellationToken cancellationToken = default)
    {
        var record = await _store.GetByIdAsync(keyId, cancellationToken);
        return record == null ? null : Map(record);
    }

    private Task<VirtualKeyValidationOutcome> ValidateInternalAsync(
        string key,
        string? requestedModel,
        bool checkBalance)
    {
        Func<VirtualKey, Task<(VirtualKeyGroup? Group, decimal PendingSpend)>>? getBalanceSnapshotAsync = null;
        if (checkBalance)
        {
            getBalanceSnapshotAsync = async virtualKey =>
            {
                var pendingSpend = _batchSpendService == null
                    ? 0m
                    : await _batchSpendService.GetPendingSpendAsync(virtualKey.Id);
                return (virtualKey.VirtualKeyGroup, pendingSpend);
            };
        }

        return VirtualKeyValidationHelper.ValidateKeyAsync(
            key,
            requestedModel,
            checkBalance,
            async keyHash =>
            {
                var record = await _store.GetByHashAsync(keyHash);
                return record == null ? null : Map(record);
            },
            getBalanceSnapshotAsync,
            _logger);
    }

    private static VirtualKey Map(VirtualKeyRuntimeRecord record)
    {
        var group = new VirtualKeyGroup
        {
            Id = record.Group.Id,
            ExternalGroupId = record.Group.ExternalGroupId,
            GroupName = record.Group.GroupName,
            Balance = record.Group.Balance,
            LifetimeCreditsAdded = record.Group.LifetimeCreditsAdded,
            LifetimeSpent = record.Group.LifetimeSpent,
            CreatedAt = record.Group.CreatedAt,
            UpdatedAt = record.Group.UpdatedAt,
            MediaRetentionPolicyId = record.Group.MediaRetentionPolicyId,
            RateLimitRpm = record.Group.RateLimitRpm,
            RateLimitRpd = record.Group.RateLimitRpd,
            RateLimitTpm = record.Group.RateLimitTpm,
            MaxParallelRequests = record.Group.MaxParallelRequests,
            RowVersion = record.Group.RowVersion
        };

        var key = new VirtualKey
        {
            Id = record.Id,
            KeyName = record.KeyName,
            KeyHash = record.KeyHash,
            Description = record.Description,
            IsEnabled = record.IsEnabled,
            VirtualKeyGroupId = record.VirtualKeyGroupId,
            VirtualKeyGroup = group,
            ExpiresAt = record.ExpiresAt,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            Metadata = record.Metadata,
            AllowedModels = record.AllowedModels,
            RateLimitRpm = record.RateLimitRpm,
            RateLimitRpd = record.RateLimitRpd,
            RateLimitTpm = record.RateLimitTpm,
            MaxParallelRequests = record.MaxParallelRequests,
            RateLimitPriority = record.RateLimitPriority,
            ModelRateLimits = record.ModelRateLimits,
            RowVersion = record.RowVersion
        };
        group.VirtualKeys.Add(key);
        return key;
    }
}
