using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// Repository implementation for function executions using Entity Framework Core.
/// Extends RepositoryBase for standard CRUD operations and adds domain-specific methods.
/// Includes distributed execution support via leasing mechanism.
/// </summary>
public class FunctionExecutionRepository : RepositoryBase<FunctionExecution, Guid>, IFunctionExecutionRepository
{
    /// <summary>
    /// Creates a new instance of the repository.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory</param>
    /// <param name="logger">The logger instance</param>
    public FunctionExecutionRepository(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        ILogger<FunctionExecutionRepository> logger)
        : base(dbContextFactory, logger)
    {
    }

    /// <inheritdoc/>
    protected override DbSet<FunctionExecution> GetDbSet(ConduitDbContext context)
        => context.FunctionExecutions;

    /// <inheritdoc/>
    protected override IQueryable<FunctionExecution> ApplyDefaultIncludes(IQueryable<FunctionExecution> query)
    {
        return query.Include(e => e.FunctionConfiguration);
    }

    /// <inheritdoc/>
    protected override IQueryable<FunctionExecution> ApplyDefaultOrdering(IQueryable<FunctionExecution> query)
    {
        return query.OrderByDescending(e => e.RequestedAt);
    }

    #region Query Methods

    /// <inheritdoc/>
    public async Task<List<FunctionExecution>> GetByVirtualKeyIdAsync(int virtualKeyId, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
            await GetDbSet(context)
                .AsNoTracking()
                .Include(e => e.FunctionConfiguration)
                .Where(e => e.VirtualKeyId == virtualKeyId)
                .OrderByDescending(e => e.RequestedAt)
                .ToListAsync(cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<FunctionExecution>> GetByFunctionConfigurationIdAsync(int functionConfigurationId, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
            await GetDbSet(context)
                .AsNoTracking()
                .Where(e => e.FunctionConfigurationId == functionConfigurationId)
                .OrderByDescending(e => e.RequestedAt)
                .ToListAsync(cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<FunctionExecution>> GetByStateAsync(ExecutionState state, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
            await GetDbSet(context)
                .AsNoTracking()
                .Include(e => e.FunctionConfiguration)
                .Where(e => e.State == state)
                .OrderBy(e => e.RequestedAt)
                .ToListAsync(cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<FunctionExecution>> GetExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            var now = DateTime.UtcNow;
            return await GetDbSet(context)
                .AsNoTracking()
                .Include(e => e.FunctionConfiguration)
                .Where(e => e.LeasedBy != null
                    && e.LeaseExpiryTime < now
                    && (e.State == ExecutionState.Pending || e.State == ExecutionState.Running))
                .ToListAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<FunctionExecution>> GetReadyForRetryAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            var now = DateTime.UtcNow;
            return await GetDbSet(context)
                .AsNoTracking()
                .Include(e => e.FunctionConfiguration)
                .Where(e => e.State == ExecutionState.Failed
                    && e.NextRetryAt != null
                    && e.NextRetryAt <= now)
                .ToListAsync(cancellationToken);
        }, cancellationToken);
    }

    #endregion

    #region Leasing Operations

    /// <inheritdoc/>
    public async Task<FunctionExecution?> LeaseNextPendingAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("Worker ID cannot be null or empty", nameof(workerId));
        }

        await using var context = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        // No explicit transaction: the single SaveChangesAsync is atomic on its own,
        // and racing workers are arbitrated by the Version concurrency token.
        try
        {
            var now = DateTime.UtcNow;
            var leaseExpiry = now.Add(leaseDuration);

            // Find the next pending execution that is not leased or has an expired lease
            var execution = await GetDbSet(context)
                .Where(e => e.State == ExecutionState.Pending
                    && (e.LeasedBy == null || e.LeaseExpiryTime < now))
                .OrderBy(e => e.RequestedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (execution == null)
            {
                return null;
            }

            // Lease the execution
            execution.LeasedBy = workerId;
            execution.LeaseExpiryTime = leaseExpiry;
            execution.Version++;

            await context.SaveChangesAsync(cancellationToken);

            Logger.LogInformation("Leased execution {ExecutionId} to worker {WorkerId} until {LeaseExpiry}",
                execution.Id, workerId, leaseExpiry);

            return execution;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Another worker grabbed this execution, that's okay
            Logger.LogDebug(ex, "Concurrency conflict while leasing execution (another worker may have claimed it)");
            return null;
        }

    }

    #endregion

    #region Create/Update Operations

    /// <inheritdoc/>
    public override async Task<Guid> CreateAsync(FunctionExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return await ExecuteWriteAsync(async context =>
        {
            if (execution.Id == Guid.Empty)
            {
                execution.Id = Guid.NewGuid();
            }

            GetDbSet(context).Add(execution);
            await context.SaveChangesAsync(cancellationToken);

            return execution.Id;
        }, "creating", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<bool> UpdateAsync(FunctionExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return await ExecuteWriteAsync(async context =>
        {
            var originalVersion = execution.Version;
            var saved = false;
            try
            {
                // Increment version for optimistic concurrency
                execution.Version = unchecked(originalVersion + 1);

                // Attach and update
                GetDbSet(context).Attach(execution);
                context.Entry(execution).State = EntityState.Modified;

                // Set original version for concurrency check
                context.Entry(execution).Property(e => e.Version).OriginalValue = originalVersion;

                int rowsAffected = await context.SaveChangesAsync(cancellationToken);

                saved = rowsAffected > 0;
                return saved;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                Logger.LogWarning(ex, "Concurrency conflict updating execution {ExecutionId}", execution.Id);
                return false;
            }
            finally
            {
                // A stale snapshot must retain its stale version, even when retried.
                if (!saved) execution.Version = originalVersion;
            }
        }, $"updating ID {execution.Id}", cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateStateAsync(Guid executionId, ExecutionState state, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        await using var context = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var execution = await GetDbSet(context)
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);

        if (execution != null)
        {
            execution.State = state;
            execution.ErrorMessage = errorMessage;
            execution.Version++;

            if (state == ExecutionState.Running && !execution.StartedAt.HasValue)
            {
                execution.StartedAt = DateTime.UtcNow;
            }
            else if (state == ExecutionState.Completed ||
                     state == ExecutionState.Failed ||
                     state == ExecutionState.Cancelled ||
                     state == ExecutionState.TimedOut)
            {
                execution.CompletedAt = DateTime.UtcNow;
                if (execution.StartedAt.HasValue)
                {
                    execution.Duration = execution.CompletedAt.Value - execution.StartedAt.Value;
                }
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task UpdateProgressAsync(Guid executionId, int progressPercentage, string? statusMessage = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await DbContextFactory.CreateDbContextAsync(cancellationToken);

            var execution = await GetDbSet(context)
                .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);

            if (execution != null)
            {
                execution.ProgressPercentage = progressPercentage;
                execution.StatusMessage = statusMessage;
                execution.Version++;

                await context.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating progress for execution {ExecutionId}",
                LoggingSanitizer.S(executionId));
            // Don't throw - progress updates are non-critical
        }
    }

    #endregion

    #region Cleanup Operations

    /// <inheritdoc/>
    public async Task<int> DeleteOldExecutionsAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        await using var context = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var oldExecutions = await GetDbSet(context)
            .Where(e => e.RequestedAt < olderThan)
            .ToListAsync(cancellationToken);

        GetDbSet(context).RemoveRange(oldExecutions);
        int count = await context.SaveChangesAsync(cancellationToken);

        Logger.LogInformation("Deleted {Count} old function executions older than {OlderThan}",
            count, olderThan);

        return count;
    }

    #endregion
}
