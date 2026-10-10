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
        return await context.VirtualKeys
            .AsNoTracking()
            .Where(key => key.KeyHash == keyHash)
            .Select(key => new VirtualKeyRuntimeRecord
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
                Group = new VirtualKeyGroupRuntimeRecord
                {
                    Id = key.VirtualKeyGroup.Id,
                    ExternalGroupId = key.VirtualKeyGroup.ExternalGroupId,
                    GroupName = key.VirtualKeyGroup.GroupName,
                    Balance = key.VirtualKeyGroup.Balance,
                    LifetimeCreditsAdded = key.VirtualKeyGroup.LifetimeCreditsAdded,
                    LifetimeSpent = key.VirtualKeyGroup.LifetimeSpent,
                    CreatedAt = key.VirtualKeyGroup.CreatedAt,
                    UpdatedAt = key.VirtualKeyGroup.UpdatedAt,
                    MediaRetentionPolicyId = key.VirtualKeyGroup.MediaRetentionPolicyId,
                    RateLimitRpm = key.VirtualKeyGroup.RateLimitRpm,
                    RateLimitRpd = key.VirtualKeyGroup.RateLimitRpd,
                    RateLimitTpm = key.VirtualKeyGroup.RateLimitTpm,
                    MaxParallelRequests = key.VirtualKeyGroup.MaxParallelRequests,
                    RowVersion = key.VirtualKeyGroup.RowVersion
                }
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<VirtualKeyRuntimeRecord?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.VirtualKeys
            .AsNoTracking()
            .Where(key => key.Id == id)
            .Select(key => new VirtualKeyRuntimeRecord
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
                Group = new VirtualKeyGroupRuntimeRecord
                {
                    Id = key.VirtualKeyGroup.Id,
                    ExternalGroupId = key.VirtualKeyGroup.ExternalGroupId,
                    GroupName = key.VirtualKeyGroup.GroupName,
                    Balance = key.VirtualKeyGroup.Balance,
                    LifetimeCreditsAdded = key.VirtualKeyGroup.LifetimeCreditsAdded,
                    LifetimeSpent = key.VirtualKeyGroup.LifetimeSpent,
                    CreatedAt = key.VirtualKeyGroup.CreatedAt,
                    UpdatedAt = key.VirtualKeyGroup.UpdatedAt,
                    MediaRetentionPolicyId = key.VirtualKeyGroup.MediaRetentionPolicyId,
                    RateLimitRpm = key.VirtualKeyGroup.RateLimitRpm,
                    RateLimitRpd = key.VirtualKeyGroup.RateLimitRpd,
                    RateLimitTpm = key.VirtualKeyGroup.RateLimitTpm,
                    MaxParallelRequests = key.VirtualKeyGroup.MaxParallelRequests,
                    RowVersion = key.VirtualKeyGroup.RowVersion
                }
            })
            .SingleOrDefaultAsync(cancellationToken);
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
            return await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                if (adjustment.IdempotencyKey is not null)
                {
                    var duplicate = await context.VirtualKeyGroupTransactions
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .SingleOrDefaultAsync(
                            row => row.IdempotencyKey == adjustment.IdempotencyKey,
                            cancellationToken);
                    if (duplicate is not null)
                    {
                        ValidateDuplicate(duplicate, adjustment);
                        var current = await ReadCurrentStateAsync(
                            context,
                            adjustment.GroupId,
                            applied: false,
                            cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return current;
                    }
                }

                var updatedAt = DateTime.UtcNow;
                var rowsAffected = await context.VirtualKeyGroups
                    .Where(group => group.Id == adjustment.GroupId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(group => group.Balance, group => group.Balance + adjustment.Amount)
                        .SetProperty(
                            group => group.LifetimeCreditsAdded,
                            group => group.LifetimeCreditsAdded +
                                (adjustment.Amount > 0 ? adjustment.Amount : 0m))
                        .SetProperty(
                            group => group.LifetimeSpent,
                            group => group.LifetimeSpent +
                                (adjustment.Amount <= 0 ? Math.Abs(adjustment.Amount) : 0m))
                        .SetProperty(group => group.UpdatedAt, updatedAt),
                        cancellationToken);
                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException(
                        $"Virtual key group {adjustment.GroupId} not found");
                }

                var state = await ReadCurrentStateAsync(
                    context,
                    adjustment.GroupId,
                    applied: true,
                    cancellationToken);
                context.VirtualKeyGroupTransactions.Add(CreateLedgerEntry(adjustment, state.NewBalance));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return state;
            });
        }
        catch (DbUpdateException exception) when (
            adjustment.IdempotencyKey is not null &&
            DbUpdateExceptions.IsUniqueViolation(exception, "IdempotencyKey"))
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var winner = await context.VirtualKeyGroupTransactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(
                    row => row.IdempotencyKey == adjustment.IdempotencyKey,
                    cancellationToken);
            ValidateDuplicate(winner, adjustment);
            return await ReadCurrentStateAsync(
                context,
                adjustment.GroupId,
                applied: false,
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetKeyHashesByGroupIdAsync(
        int groupId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.VirtualKeys
            .AsNoTracking()
            .Where(key => key.VirtualKeyGroupId == groupId)
            .OrderBy(key => key.Id)
            .Select(key => key.KeyHash)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TouchAsync(
        int id,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.VirtualKeys
            .Where(key => key.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(key => key.UpdatedAt, updatedAt),
                cancellationToken) > 0;
    }

    private static async Task<VirtualKeyBalanceAdjustmentResult> ReadCurrentStateAsync(
        ConduitDbContext context,
        int groupId,
        bool applied,
        CancellationToken cancellationToken)
    {
        var state = await context.VirtualKeyGroups
            .AsNoTracking()
            .Where(group => group.Id == groupId)
            .Select(group => new { group.Balance, group.LifetimeSpent })
            .SingleOrDefaultAsync(cancellationToken);
        return state is null
            ? throw new InvalidOperationException($"Virtual key group {groupId} not found")
            : new VirtualKeyBalanceAdjustmentResult(
                state.Balance,
                state.LifetimeSpent,
                applied);
    }

    private static VirtualKeyGroupTransaction CreateLedgerEntry(
        VirtualKeyBalanceAdjustment adjustment,
        decimal balanceAfter) => new()
    {
        VirtualKeyGroupId = adjustment.GroupId,
        TransactionType = adjustment.Amount > 0 ? TransactionType.Credit : TransactionType.Debit,
        Amount = Math.Abs(adjustment.Amount),
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
            existing.Amount != Math.Abs(adjustment.Amount) ||
            (int)existing.ReferenceType != (int)adjustment.ReferenceType ||
            existing.ReferenceId != adjustment.ReferenceId ||
            existing.BillingWindowStartUtc != adjustment.BillingWindowStartUtc)
        {
            throw new VirtualKeyBalanceConflictException(
                $"Idempotency key '{adjustment.IdempotencyKey}' was reused with different balance-adjustment data.");
        }
    }
}
