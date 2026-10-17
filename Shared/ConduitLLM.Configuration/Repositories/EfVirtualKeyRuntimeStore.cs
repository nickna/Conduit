using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// Fixed-query EF reference adapter for Gateway virtual-key authentication and billing.
/// </summary>
public sealed class EfVirtualKeyRuntimeStore : IVirtualKeyRuntimeStore
{
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfVirtualKeyRuntimeStore(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    /// <inheritdoc />
    public async Task<VirtualKeyRuntimeRecord?> GetByHashAsync(
        string keyHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var queryKeyHash = keyHash;
        var queryCancellationToken = cancellationToken;
        var snapshot = await context.VirtualKeys
            .AsNoTracking()
            .Where(key => key.KeyHash == queryKeyHash)
            .Select(key => new
            {
                Id = key.Id,
                KeyName = key.KeyName,
                KeyHash = key.KeyHash,
                Description = key.Description,
                IsEnabled = key.IsEnabled,
                VirtualKeyGroupId = key.VirtualKeyGroupId,
                ExpiresAt = key.ExpiresAt,
                CreatedAt = key.CreatedAt,
                UpdatedAt = key.UpdatedAt,
                Metadata = key.Metadata,
                AllowedModels = key.AllowedModels,
                RateLimitRpm = key.RateLimitRpm,
                RateLimitRpd = key.RateLimitRpd,
                RateLimitTpm = key.RateLimitTpm,
                MaxParallelRequests = key.MaxParallelRequests,
                RateLimitPriority = key.RateLimitPriority,
                ModelRateLimits = key.ModelRateLimits,
                RowVersion = key.RowVersion,
                GroupId = key.VirtualKeyGroup.Id,
                GroupExternalGroupId = key.VirtualKeyGroup.ExternalGroupId,
                GroupName = key.VirtualKeyGroup.GroupName,
                GroupBalance = key.VirtualKeyGroup.Balance,
                GroupLifetimeCreditsAdded = key.VirtualKeyGroup.LifetimeCreditsAdded,
                GroupLifetimeSpent = key.VirtualKeyGroup.LifetimeSpent,
                GroupCreatedAt = key.VirtualKeyGroup.CreatedAt,
                GroupUpdatedAt = key.VirtualKeyGroup.UpdatedAt,
                GroupMediaRetentionPolicyId = key.VirtualKeyGroup.MediaRetentionPolicyId,
                GroupRateLimitRpm = key.VirtualKeyGroup.RateLimitRpm,
                GroupRateLimitRpd = key.VirtualKeyGroup.RateLimitRpd,
                GroupRateLimitTpm = key.VirtualKeyGroup.RateLimitTpm,
                GroupMaxParallelRequests = key.VirtualKeyGroup.MaxParallelRequests,
                GroupRowVersion = key.VirtualKeyGroup.RowVersion
            })
            .SingleOrDefaultAsync(queryCancellationToken);
        return snapshot is null
            ? null
            : new VirtualKeyRuntimeRecord
            {
                Id = snapshot.Id,
                KeyName = snapshot.KeyName,
                KeyHash = snapshot.KeyHash,
                Description = snapshot.Description,
                IsEnabled = snapshot.IsEnabled,
                VirtualKeyGroupId = snapshot.VirtualKeyGroupId,
                ExpiresAt = snapshot.ExpiresAt,
                CreatedAt = snapshot.CreatedAt,
                UpdatedAt = snapshot.UpdatedAt,
                Metadata = snapshot.Metadata,
                AllowedModels = snapshot.AllowedModels,
                RateLimitRpm = snapshot.RateLimitRpm,
                RateLimitRpd = snapshot.RateLimitRpd,
                RateLimitTpm = snapshot.RateLimitTpm,
                MaxParallelRequests = snapshot.MaxParallelRequests,
                RateLimitPriority = snapshot.RateLimitPriority,
                ModelRateLimits = snapshot.ModelRateLimits,
                RowVersion = snapshot.RowVersion,
                Group = new VirtualKeyGroupRuntimeRecord
                {
                    Id = snapshot.GroupId,
                    ExternalGroupId = snapshot.GroupExternalGroupId,
                    GroupName = snapshot.GroupName,
                    Balance = snapshot.GroupBalance,
                    LifetimeCreditsAdded = snapshot.GroupLifetimeCreditsAdded,
                    LifetimeSpent = snapshot.GroupLifetimeSpent,
                    CreatedAt = snapshot.GroupCreatedAt,
                    UpdatedAt = snapshot.GroupUpdatedAt,
                    MediaRetentionPolicyId = snapshot.GroupMediaRetentionPolicyId,
                    RateLimitRpm = snapshot.GroupRateLimitRpm,
                    RateLimitRpd = snapshot.GroupRateLimitRpd,
                    RateLimitTpm = snapshot.GroupRateLimitTpm,
                    MaxParallelRequests = snapshot.GroupMaxParallelRequests,
                    RowVersion = snapshot.GroupRowVersion
                }
            };
    }

    /// <inheritdoc />
    public async Task<VirtualKeyRuntimeRecord?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var queryKeyId = id;
        var queryCancellationToken = cancellationToken;
        var snapshot = await context.VirtualKeys
            .AsNoTracking()
            .Where(key => key.Id == queryKeyId)
            .Select(key => new
            {
                Id = key.Id,
                KeyName = key.KeyName,
                KeyHash = key.KeyHash,
                Description = key.Description,
                IsEnabled = key.IsEnabled,
                VirtualKeyGroupId = key.VirtualKeyGroupId,
                ExpiresAt = key.ExpiresAt,
                CreatedAt = key.CreatedAt,
                UpdatedAt = key.UpdatedAt,
                Metadata = key.Metadata,
                AllowedModels = key.AllowedModels,
                RateLimitRpm = key.RateLimitRpm,
                RateLimitRpd = key.RateLimitRpd,
                RateLimitTpm = key.RateLimitTpm,
                MaxParallelRequests = key.MaxParallelRequests,
                RateLimitPriority = key.RateLimitPriority,
                ModelRateLimits = key.ModelRateLimits,
                RowVersion = key.RowVersion,
                GroupId = key.VirtualKeyGroup.Id,
                GroupExternalGroupId = key.VirtualKeyGroup.ExternalGroupId,
                GroupName = key.VirtualKeyGroup.GroupName,
                GroupBalance = key.VirtualKeyGroup.Balance,
                GroupLifetimeCreditsAdded = key.VirtualKeyGroup.LifetimeCreditsAdded,
                GroupLifetimeSpent = key.VirtualKeyGroup.LifetimeSpent,
                GroupCreatedAt = key.VirtualKeyGroup.CreatedAt,
                GroupUpdatedAt = key.VirtualKeyGroup.UpdatedAt,
                GroupMediaRetentionPolicyId = key.VirtualKeyGroup.MediaRetentionPolicyId,
                GroupRateLimitRpm = key.VirtualKeyGroup.RateLimitRpm,
                GroupRateLimitRpd = key.VirtualKeyGroup.RateLimitRpd,
                GroupRateLimitTpm = key.VirtualKeyGroup.RateLimitTpm,
                GroupMaxParallelRequests = key.VirtualKeyGroup.MaxParallelRequests,
                GroupRowVersion = key.VirtualKeyGroup.RowVersion
            })
            .SingleOrDefaultAsync(queryCancellationToken);
        return snapshot is null
            ? null
            : new VirtualKeyRuntimeRecord
            {
                Id = snapshot.Id,
                KeyName = snapshot.KeyName,
                KeyHash = snapshot.KeyHash,
                Description = snapshot.Description,
                IsEnabled = snapshot.IsEnabled,
                VirtualKeyGroupId = snapshot.VirtualKeyGroupId,
                ExpiresAt = snapshot.ExpiresAt,
                CreatedAt = snapshot.CreatedAt,
                UpdatedAt = snapshot.UpdatedAt,
                Metadata = snapshot.Metadata,
                AllowedModels = snapshot.AllowedModels,
                RateLimitRpm = snapshot.RateLimitRpm,
                RateLimitRpd = snapshot.RateLimitRpd,
                RateLimitTpm = snapshot.RateLimitTpm,
                MaxParallelRequests = snapshot.MaxParallelRequests,
                RateLimitPriority = snapshot.RateLimitPriority,
                ModelRateLimits = snapshot.ModelRateLimits,
                RowVersion = snapshot.RowVersion,
                Group = new VirtualKeyGroupRuntimeRecord
                {
                    Id = snapshot.GroupId,
                    ExternalGroupId = snapshot.GroupExternalGroupId,
                    GroupName = snapshot.GroupName,
                    Balance = snapshot.GroupBalance,
                    LifetimeCreditsAdded = snapshot.GroupLifetimeCreditsAdded,
                    LifetimeSpent = snapshot.GroupLifetimeSpent,
                    CreatedAt = snapshot.GroupCreatedAt,
                    UpdatedAt = snapshot.GroupUpdatedAt,
                    MediaRetentionPolicyId = snapshot.GroupMediaRetentionPolicyId,
                    RateLimitRpm = snapshot.GroupRateLimitRpm,
                    RateLimitRpd = snapshot.GroupRateLimitRpd,
                    RateLimitTpm = snapshot.GroupRateLimitTpm,
                    MaxParallelRequests = snapshot.GroupMaxParallelRequests,
                    RowVersion = snapshot.GroupRowVersion
                }
            };
    }

    /// <inheritdoc />
    public async Task<VirtualKeyBalanceAdjustmentResult> AdjustBalanceAsync(
        VirtualKeyBalanceAdjustment adjustment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        ValidateAdjustment(adjustment);

        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var strategy = context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async strategyToken =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(strategyToken);

                if (adjustment.IdempotencyKey is not null)
                {
                    var duplicateIdempotencyKey = adjustment.IdempotencyKey;
                    var duplicateCancellationToken = strategyToken;
                    var duplicate = await context.VirtualKeyGroupTransactions
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .SingleOrDefaultAsync(
                            row => row.IdempotencyKey == duplicateIdempotencyKey,
                            duplicateCancellationToken);
                    if (duplicate is not null)
                    {
                        ValidateDuplicate(duplicate, adjustment);
                        var duplicateStateGroupId = adjustment.GroupId;
                        var duplicateStateCancellationToken = strategyToken;
                        var duplicateStateSnapshot = await context.VirtualKeyGroups
                            .AsNoTracking()
                            .Where(group => group.Id == duplicateStateGroupId)
                            .Select(group => new { group.Balance, group.LifetimeSpent })
                            .SingleOrDefaultAsync(duplicateStateCancellationToken);
                        var current = duplicateStateSnapshot is null
                            ? throw new InvalidOperationException($"Virtual key group {duplicateStateGroupId} not found")
                            : new VirtualKeyBalanceAdjustmentResult(
                                duplicateStateSnapshot.Balance,
                                duplicateStateSnapshot.LifetimeSpent,
                                false);
                        await transaction.CommitAsync(strategyToken);
                        return current;
                    }
                }

                var updatedAt = DateTime.UtcNow;
                var updateGroupId = adjustment.GroupId;
                var updateAmount = adjustment.Amount;
                var updateCancellationToken = strategyToken;
                var rowsAffected = await context.VirtualKeyGroups
                    .Where(group => group.Id == updateGroupId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(group => group.Balance, group => group.Balance + updateAmount)
                        .SetProperty(
                            group => group.LifetimeCreditsAdded,
                            group => group.LifetimeCreditsAdded +
                                (updateAmount > 0m ? updateAmount : 0m))
                        .SetProperty(
                            group => group.LifetimeSpent,
                            group => group.LifetimeSpent +
                                (updateAmount <= 0m ? Math.Abs(updateAmount) : 0m))
                        .SetProperty(group => group.UpdatedAt, group => updatedAt),
                        updateCancellationToken);
                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException(
                        $"Virtual key group {adjustment.GroupId} not found");
                }

                var updatedStateGroupId = adjustment.GroupId;
                var updatedStateCancellationToken = strategyToken;
                var updatedStateSnapshot = await context.VirtualKeyGroups
                    .AsNoTracking()
                    .Where(group => group.Id == updatedStateGroupId)
                    .Select(group => new { group.Balance, group.LifetimeSpent })
                    .SingleOrDefaultAsync(updatedStateCancellationToken);
                var state = updatedStateSnapshot is null
                    ? throw new InvalidOperationException($"Virtual key group {updatedStateGroupId} not found")
                    : new VirtualKeyBalanceAdjustmentResult(
                        updatedStateSnapshot.Balance,
                        updatedStateSnapshot.LifetimeSpent,
                        true);
                context.VirtualKeyGroupTransactions.Add(CreateLedgerEntry(adjustment, state.NewBalance));
                await context.SaveChangesAsync(strategyToken);
                await transaction.CommitAsync(strategyToken);
                return state;
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (
            adjustment.IdempotencyKey is not null &&
            DbUpdateExceptions.IsUniqueViolation(exception, "IdempotencyKey"))
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var winnerIdempotencyKey = adjustment.IdempotencyKey;
            var winnerCancellationToken = cancellationToken;
            var winner = await context.VirtualKeyGroupTransactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(
                    row => row.IdempotencyKey == winnerIdempotencyKey,
                    winnerCancellationToken);
            ValidateDuplicate(winner, adjustment);
            var winnerStateGroupId = adjustment.GroupId;
            var winnerStateCancellationToken = cancellationToken;
            var winnerStateSnapshot = await context.VirtualKeyGroups
                .AsNoTracking()
                .Where(group => group.Id == winnerStateGroupId)
                .Select(group => new { group.Balance, group.LifetimeSpent })
                .SingleOrDefaultAsync(winnerStateCancellationToken);
            return winnerStateSnapshot is null
                ? throw new InvalidOperationException($"Virtual key group {winnerStateGroupId} not found")
                : new VirtualKeyBalanceAdjustmentResult(
                    winnerStateSnapshot.Balance,
                    winnerStateSnapshot.LifetimeSpent,
                    false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetKeyHashesByGroupIdAsync(
        int groupId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var queryGroupId = groupId;
        var queryCancellationToken = cancellationToken;
        return await context.VirtualKeys
            .AsNoTracking()
            .Where(key => key.VirtualKeyGroupId == queryGroupId)
            .OrderBy(key => key.Id)
            .Select(key => key.KeyHash)
            .ToListAsync(queryCancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TouchAsync(
        int id,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var queryKeyId = id;
        var queryUpdatedAt = updatedAt;
        var queryCancellationToken = cancellationToken;
        return await context.VirtualKeys
            .Where(key => key.Id == queryKeyId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(key => key.UpdatedAt, key => queryUpdatedAt),
                queryCancellationToken) > 0;
    }

    private static VirtualKeyGroupTransaction CreateLedgerEntry(
        VirtualKeyBalanceAdjustment adjustment,
        decimal balanceAfter) => new()
    {
        VirtualKeyGroupId = adjustment.GroupId,
        TransactionType = adjustment.Amount > 0 ? TransactionType.Credit : TransactionType.Debit,
        Amount = Math.Abs(adjustment.Amount),
        OriginalAmount = adjustment.IdempotencyKey is null ? null : Math.Abs(adjustment.Amount),
        BalanceAfter = balanceAfter,
        ReferenceType = (ReferenceType)(int)adjustment.ReferenceType,
        ReferenceId = adjustment.ReferenceId,
        Description = adjustment.Description ??
            (adjustment.Amount > 0 ? "Credits added" : "Usage deducted"),
        InitiatedBy = adjustment.InitiatedBy ?? "System",
        IdempotencyKey = adjustment.IdempotencyKey,
        BillingWindowStartUtc = adjustment.BillingWindowStartUtc,
        CreatedAt = DateTime.UtcNow
    };

    private static void ValidateAdjustment(VirtualKeyBalanceAdjustment adjustment)
    {
        if (adjustment.GroupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(adjustment), "Group ID must be positive.");
        }

        VirtualKeyBalanceReplayEvidence.ValidateAmount(adjustment.Amount, adjustment.IdempotencyKey);

        if (adjustment.IdempotencyKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(adjustment.IdempotencyKey);
        }
    }

    private static void ValidateDuplicate(
        VirtualKeyGroupTransaction existing,
        VirtualKeyBalanceAdjustment adjustment)
    {
        var expectedType = adjustment.Amount > 0 ? TransactionType.Credit : TransactionType.Debit;
        if (existing.VirtualKeyGroupId != adjustment.GroupId ||
            existing.TransactionType != expectedType ||
            !VirtualKeyBalanceReplayEvidence.AmountMatches(existing.Amount, existing.OriginalAmount, adjustment.Amount) ||
            (int)existing.ReferenceType != (int)adjustment.ReferenceType ||
            existing.ReferenceId != adjustment.ReferenceId ||
            existing.BillingWindowStartUtc != adjustment.BillingWindowStartUtc)
        {
            throw new VirtualKeyBalanceConflictException(
                $"Idempotency key '{adjustment.IdempotencyKey}' was reused with different balance-adjustment data.");
        }
    }
}
