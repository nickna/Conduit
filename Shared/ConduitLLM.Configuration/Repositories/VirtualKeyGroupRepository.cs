using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Exceptions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// Repository implementation for managing virtual key groups.
/// Extends RepositoryBase for standard CRUD operations and implements domain-specific methods.
/// </summary>
public class VirtualKeyGroupRepository : RepositoryBase<VirtualKeyGroup, int>, IVirtualKeyGroupRepository
{
    /// <summary>
    /// Creates a new instance of the VirtualKeyGroupRepository.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory</param>
    /// <param name="logger">The logger</param>
    public VirtualKeyGroupRepository(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        ILogger<VirtualKeyGroupRepository> logger)
        : base(dbContextFactory, logger)
    {
    }

    /// <inheritdoc/>
    protected override DbSet<VirtualKeyGroup> GetDbSet(ConduitDbContext context)
        => context.VirtualKeyGroups;

    /// <inheritdoc/>
    protected override IQueryable<VirtualKeyGroup> ApplyDefaultIncludes(IQueryable<VirtualKeyGroup> query)
    {
        // The response version includes the member count; keep both in one SQL snapshot.
        return query.Include(g => g.VirtualKeys).AsSingleQuery();
    }

    /// <inheritdoc/>
    protected override IQueryable<VirtualKeyGroup> ApplyDefaultOrdering(IQueryable<VirtualKeyGroup> query)
    {
        return query.OrderBy(g => g.GroupName);
    }

    /// <summary>
    /// Overrides CreateAsync to handle initial balance transaction creation.
    /// </summary>
    public override async Task<int> CreateAsync(VirtualKeyGroup entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return await ExecuteAsync(async context =>
        {
            OnBeforeCreate(entity);

            GetDbSet(context).Add(entity);
            await context.SaveChangesAsync(cancellationToken);

            // If group was created with initial balance, create a transaction record
            if (entity.Balance > 0)
            {
                var transaction = CreateTransaction(
                    entity.Id,
                    entity.Balance,
                    entity.Balance,
                    TransactionType.Credit,
                    ReferenceType.Initial,
                    "Initial balance"
                );

                context.VirtualKeyGroupTransactions.Add(transaction);
                await context.SaveChangesAsync(cancellationToken);
            }

            Logger.LogInformation("Created virtual key group {GroupId} with name {GroupName}",
                entity.Id, entity.GroupName);

            return entity.Id;
        }, cancellationToken);

    }

    /// <summary>
    /// Updates only the group's scalar values and logs the completed update.
    /// </summary>
    public override async Task<bool> UpdateAsync(VirtualKeyGroup entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var result = await ExecuteWriteAsync(async context =>
        {
            OnBeforeUpdate(entity);

            // Reads include member keys. Marking the whole detached graph modified would
            // overwrite changes to those keys when only the group was edited.
            context.Entry(entity).State = EntityState.Modified;
            return await context.SaveChangesAsync(cancellationToken) > 0;
        }, $"updating ID {entity.Id}", cancellationToken);

        if (result)
        {
            Logger.LogInformation("Updated virtual key group {GroupId}", entity.Id);
        }

        return result;
    }

    /// <summary>
    /// Overrides DeleteAsync to provide logging.
    /// </summary>
    public override async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var result = await base.DeleteAsync(id, cancellationToken);

        if (result)
        {
            Logger.LogInformation("Deleted virtual key group {GroupId}", id);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<VirtualKeyGroup?> GetByIdWithKeysAsync(int id)
    {
        return await ExecuteAsync(async context =>
            await GetDbSet(context)
                .Include(g => g.VirtualKeys)
                .AsSingleQuery()
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == id),
            operationName: $"getting by ID {id} with keys");
    }

    /// <inheritdoc />
    public async Task<VirtualKeyGroup?> GetByKeyIdAsync(int virtualKeyId)
    {
        return await ExecuteAsync(async context =>
        {
            var key = await context.VirtualKeys
                .Include(k => k.VirtualKeyGroup)
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.Id == virtualKeyId);

            return key?.VirtualKeyGroup;
        }, operationName: $"getting by key ID {virtualKeyId}");
    }

    /// <inheritdoc />
    public async Task<VirtualKeyGroup?> GetByKeyIdWithKeysAsync(int virtualKeyId)
    {
        return await ExecuteAsync(async context =>
            await context.VirtualKeyGroups.AsNoTracking()
                .Include(group => group.VirtualKeys)
                .AsSingleQuery()
                .FirstOrDefaultAsync(group => group.VirtualKeys.Any(key => key.Id == virtualKeyId)),
            operationName: $"getting by key ID {virtualKeyId} with keys");
    }

    /// <inheritdoc />
    public async Task<decimal> AdjustBalanceAsync(int groupId, decimal amount)
    {
        return await AdjustBalanceAsync(groupId, amount, null, null, ReferenceType.Manual, null);
    }

    /// <inheritdoc />
    public async Task<decimal> AdjustBalanceAsync(int groupId, decimal amount, string? description, string? initiatedBy)
    {
        return await AdjustBalanceAsync(groupId, amount, description, initiatedBy, ReferenceType.Manual, null);
    }

    public async Task<VirtualKeyGroup?> AdjustBalanceConditionalAsync(int groupId, decimal amount,
        string? description, string? initiatedBy, ResourcePrecondition precondition, CancellationToken cancellationToken)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ResourceMutationGuard.ExecuteAsync<VirtualKeyGroup, VirtualKeyGroup>(db, groupId, precondition,
            async (context, group, ct) =>
            {
                // The guard already holds the row lock: counters and ledger use this same transaction.
                group.Balance += amount;
                if (amount > 0) group.LifetimeCreditsAdded += amount;
                else group.LifetimeSpent += Math.Abs(amount);
                group.UpdatedAt = DateTime.UtcNow;
                context.VirtualKeyGroupTransactions.Add(CreateTransaction(groupId, amount, group.Balance,
                    amount > 0 ? TransactionType.Credit : TransactionType.Debit, ReferenceType.Manual,
                    description ?? (amount > 0 ? "Credits added" : "Usage deducted"), initiatedBy: initiatedBy ?? "System"));
                await context.Entry(group).Collection(entity => entity.VirtualKeys).LoadAsync(ct);
            }, group => group, cancellationToken);
    }

    /// <inheritdoc />
    public Task<decimal> AdjustBalanceAsync(int groupId, decimal amount, string? description, string? initiatedBy, ReferenceType referenceType, string? referenceId = null)
        => AdjustBalanceInternalAsync(groupId, amount, description, initiatedBy, referenceType, referenceId, null);

    /// <inheritdoc />
    public Task<decimal> AdjustBalanceAsync(int groupId, decimal amount, string? description, string? initiatedBy, ReferenceType referenceType, string? referenceId, DateTime billingWindowStartUtc)
        => AdjustBalanceInternalAsync(groupId, amount, description, initiatedBy, referenceType, referenceId, billingWindowStartUtc);

    private async Task<decimal> AdjustBalanceInternalAsync(int groupId, decimal amount, string? description, string? initiatedBy, ReferenceType referenceType, string? referenceId, DateTime? billingWindowStartUtc)
    {
        try
        {
            return await ExecuteAsync(async context =>
            {
                var group = await ApplyBalanceAdjustmentAsync(
                    context, groupId, amount, description, initiatedBy, referenceType, referenceId, idempotencyKey: null, billingWindowStartUtc);
                return group.Balance;
            });
        }
        catch (InvalidOperationException)
        {
            throw;
        }

    }

    /// <inheritdoc />
    public Task<BalanceAdjustmentResult> AdjustBalanceIdempotentAsync(
        int groupId,
        decimal amount,
        string idempotencyKey,
        string? description,
        string? initiatedBy,
        ReferenceType referenceType,
        string? referenceId = null)
        => AdjustBalanceIdempotentInternalAsync(groupId, amount, idempotencyKey, description, initiatedBy, referenceType, referenceId, null);

    /// <inheritdoc />
    public Task<BalanceAdjustmentResult> AdjustBalanceIdempotentAsync(
        int groupId,
        decimal amount,
        string idempotencyKey,
        string? description,
        string? initiatedBy,
        ReferenceType referenceType,
        string? referenceId,
        DateTime billingWindowStartUtc)
        => AdjustBalanceIdempotentInternalAsync(groupId, amount, idempotencyKey, description, initiatedBy, referenceType, referenceId, billingWindowStartUtc);

    private async Task<BalanceAdjustmentResult> AdjustBalanceIdempotentInternalAsync(
        int groupId,
        decimal amount,
        string idempotencyKey,
        string? description,
        string? initiatedBy,
        ReferenceType referenceType,
        string? referenceId,
        DateTime? billingWindowStartUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        VirtualKeyBalanceReplayEvidence.ValidateAmount(amount, idempotencyKey);

        try
        {
            return await ExecuteAsync(async context =>
            {
                // Includes soft-deleted rows: a deleted ledger entry still proves the
                // adjustment was applied once.
                var duplicate = await context.VirtualKeyGroupTransactions
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey);

                if (duplicate != null)
                {
                    var expectedType = amount > 0 ? TransactionType.Credit : TransactionType.Debit;
                    if (duplicate.VirtualKeyGroupId != groupId ||
                        duplicate.TransactionType != expectedType ||
                        !VirtualKeyBalanceReplayEvidence.AmountMatches(duplicate.Amount, duplicate.OriginalAmount, amount) ||
                        duplicate.ReferenceType != referenceType ||
                        duplicate.ReferenceId != referenceId ||
                        duplicate.BillingWindowStartUtc != billingWindowStartUtc)
                    {
                        throw new IdempotencyConflictException(
                            $"Idempotency key '{idempotencyKey}' was reused with different balance-adjustment data.");
                    }

                    Logger.LogWarning(
                        "Duplicate balance adjustment for group {GroupId} with idempotency key {IdempotencyKey} - skipping",
                        groupId, idempotencyKey);
                    return await GetCurrentStateAsync(context, groupId, applied: false);
                }

                var group = await ApplyBalanceAdjustmentAsync(
                    context, groupId, amount, description, initiatedBy, referenceType, referenceId, idempotencyKey, billingWindowStartUtc);
                return new BalanceAdjustmentResult(group.Balance, group.LifetimeSpent, Applied: true);
            });
        }
        catch (DbUpdateException ex) when (IsIdempotencyKeyViolation(ex))
        {
            // Race backstop: a concurrent delivery inserted the key between the check
            // and the save. The unique index guarantees single application.
            Logger.LogWarning(
                "Concurrent duplicate balance adjustment for group {GroupId} with idempotency key {IdempotencyKey} - skipping",
                groupId, idempotencyKey);
            return await ExecuteAsync(async context =>
            {
                var winner = await context.VirtualKeyGroupTransactions
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(t => t.IdempotencyKey == idempotencyKey);
                var expectedType = amount > 0 ? TransactionType.Credit : TransactionType.Debit;
                if (winner.VirtualKeyGroupId != groupId ||
                    winner.TransactionType != expectedType ||
                    !VirtualKeyBalanceReplayEvidence.AmountMatches(winner.Amount, winner.OriginalAmount, amount) ||
                    winner.ReferenceType != referenceType ||
                    winner.ReferenceId != referenceId ||
                    winner.BillingWindowStartUtc != billingWindowStartUtc)
                {
                    throw new IdempotencyConflictException(
                        $"Idempotency key '{idempotencyKey}' was reused concurrently with different balance-adjustment data.");
                }

                return await GetCurrentStateAsync(context, groupId, applied: false);
            });
        }
        catch (InvalidOperationException)
        {
            throw;
        }

    }

    /// <summary>
    /// Applies a balance adjustment and its ledger row in one atomic save on the
    /// supplied context. The optional idempotency key is stored on the ledger row,
    /// whose unique index enforces exactly-once application (#927).
    /// </summary>
    private async Task<VirtualKeyGroup> ApplyBalanceAdjustmentAsync(
        ConduitDbContext context,
        int groupId,
        decimal amount,
        string? description,
        string? initiatedBy,
        ReferenceType referenceType,
        string? referenceId,
        string? idempotencyKey,
        DateTime? billingWindowStartUtc)
    {
        if (context.Database.IsRelational())
        {
            return await ApplyRelationalBalanceAdjustmentAsync(
                context, groupId, amount, description, initiatedBy, referenceType, referenceId, idempotencyKey, billingWindowStartUtc);
        }

        // The in-memory provider does not support ExecuteUpdate. Keep the tracked
        // implementation for unit tests and other non-relational providers.
        var group = await GetDbSet(context).FirstOrDefaultAsync(g => g.Id == groupId);
        if (group == null)
        {
            throw new InvalidOperationException($"Virtual key group {groupId} not found");
        }

        var previousBalance = group.Balance;
        group.Balance += amount;

        if (amount > 0)
        {
            group.LifetimeCreditsAdded += amount;
        }
        else
        {
            group.LifetimeSpent += Math.Abs(amount);
        }

        group.UpdatedAt = DateTime.UtcNow;

        // Create transaction record
        var transaction = CreateTransaction(
            groupId,
            amount,
            group.Balance,
            amount > 0 ? TransactionType.Credit : TransactionType.Debit,
            referenceType,
            description ?? (amount > 0 ? "Credits added" : "Usage deducted"),
            referenceId,
            initiatedBy ?? "System"
        );
        transaction.IdempotencyKey = idempotencyKey;
        transaction.OriginalAmount = idempotencyKey is null ? null : Math.Abs(amount);
        transaction.BillingWindowStartUtc = billingWindowStartUtc;

        context.VirtualKeyGroupTransactions.Add(transaction);

        await context.SaveChangesAsync();

        Logger.LogInformation("Adjusted balance for group {GroupId} by {Amount}. Previous: {PreviousBalance}, New: {Balance}, ReferenceType: {ReferenceType}",
            groupId, amount, previousBalance, group.Balance, referenceType);

        return group;
    }

    /// <summary>
    /// Atomically increments all balance counters in the database. The transaction
    /// keeps the increment and its ledger row together, while the database-side
    /// expression prevents concurrent writers from overwriting one another (#1000).
    /// </summary>
    private async Task<VirtualKeyGroup> ApplyRelationalBalanceAdjustmentAsync(
        ConduitDbContext context,
        int groupId,
        decimal amount,
        string? description,
        string? initiatedBy,
        ReferenceType referenceType,
        string? referenceId,
        string? idempotencyKey,
        DateTime? billingWindowStartUtc)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry must not retain entities or ledger rows from the failed attempt.
            context.ChangeTracker.Clear();
            await using var databaseTransaction = await context.Database.BeginTransactionAsync();

            var updatedAt = DateTime.UtcNow;
            var rowsAffected = await GetDbSet(context)
                .Where(g => g.Id == groupId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(g => g.Balance, g => g.Balance + amount)
                    .SetProperty(
                        g => g.LifetimeCreditsAdded,
                        g => g.LifetimeCreditsAdded + (amount > 0 ? amount : 0m))
                    .SetProperty(
                        g => g.LifetimeSpent,
                        g => g.LifetimeSpent + (amount <= 0 ? Math.Abs(amount) : 0m))
                    .SetProperty(g => g.UpdatedAt, updatedAt));

            if (rowsAffected == 0)
            {
                throw new InvalidOperationException($"Virtual key group {groupId} not found");
            }

            // This read occurs in the same transaction after the row-level update,
            // so it is the exact balance produced by this adjustment.
            var group = await GetDbSet(context)
                .AsNoTracking()
                .SingleAsync(g => g.Id == groupId);
            var previousBalance = group.Balance - amount;

            var transaction = CreateTransaction(
                groupId,
                amount,
                group.Balance,
                amount > 0 ? TransactionType.Credit : TransactionType.Debit,
                referenceType,
                description ?? (amount > 0 ? "Credits added" : "Usage deducted"),
                referenceId,
                initiatedBy ?? "System");
            transaction.IdempotencyKey = idempotencyKey;
            transaction.OriginalAmount = idempotencyKey is null ? null : Math.Abs(amount);
            transaction.BillingWindowStartUtc = billingWindowStartUtc;

            context.VirtualKeyGroupTransactions.Add(transaction);
            await context.SaveChangesAsync();
            await databaseTransaction.CommitAsync();

            Logger.LogInformation(
                "Atomically adjusted balance for group {GroupId} by {Amount}. Previous: {PreviousBalance}, New: {Balance}, ReferenceType: {ReferenceType}",
                groupId, amount, previousBalance, group.Balance, referenceType);

            return group;
        });
    }

    private async Task<BalanceAdjustmentResult> GetCurrentStateAsync(ConduitDbContext context, int groupId, bool applied)
    {
        var group = await GetDbSet(context).AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
        if (group == null)
        {
            throw new InvalidOperationException($"Virtual key group {groupId} not found");
        }

        return new BalanceAdjustmentResult(group.Balance, group.LifetimeSpent, applied);
    }

    private static bool IsIdempotencyKeyViolation(DbUpdateException ex)
        => DbUpdateExceptions.IsUniqueViolation(ex, "IdempotencyKey");

    /// <inheritdoc />
    public async Task<(List<VirtualKeyGroup> Items, int TotalCount)> GetLowBalanceGroupsPaginatedAsync(
        decimal threshold,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return await GetFilteredPaginatedAsync(
            g => g.Balance < threshold,
            pageNumber,
            pageSize,
            q => q.OrderBy(g => g.Balance),
            cancellationToken,
            $"getting low balance groups (threshold: {threshold})");
    }

    /// <summary>
    /// Creates a transaction record for a virtual key group
    /// </summary>
    private static VirtualKeyGroupTransaction CreateTransaction(
        int groupId,
        decimal amount,
        decimal balanceAfter,
        TransactionType transactionType,
        ReferenceType referenceType,
        string? description = null,
        string? referenceId = null,
        string initiatedBy = "System",
        string? initiatedByUserId = null)
    {
        return new VirtualKeyGroupTransaction
        {
            VirtualKeyGroupId = groupId,
            TransactionType = transactionType,
            Amount = Math.Abs(amount), // Always store positive
            BalanceAfter = balanceAfter,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            Description = description,
            InitiatedBy = initiatedBy,
            InitiatedByUserId = initiatedByUserId,
            CreatedAt = DateTime.UtcNow
        };
    }
}
