using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Utilities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Repositories
{
    /// <summary>
    /// Repository implementation for managing async tasks.
    /// Extends RepositoryBase for standard CRUD operations.
    /// </summary>
    public class AsyncTaskRepository : RepositoryBase<AsyncTask, string>, IAsyncTaskRepository
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncTaskRepository"/> class.
        /// </summary>
        /// <param name="dbContextFactory">The database context factory.</param>
        /// <param name="logger">The logger instance.</param>
        public AsyncTaskRepository(
            IDbContextFactory<ConduitDbContext> dbContextFactory,
            ILogger<AsyncTaskRepository> logger)
            : base(dbContextFactory, logger)
        {
        }

        /// <inheritdoc/>
        protected override DbSet<AsyncTask> GetDbSet(ConduitDbContext context)
        {
            return context.AsyncTasks;
        }

        /// <inheritdoc/>
        protected override IQueryable<AsyncTask> ApplyDefaultOrdering(IQueryable<AsyncTask> query)
        {
            return query.OrderByDescending(t => t.CreatedAt);
        }

        /// <inheritdoc/>
        public override async Task<string> CreateAsync(AsyncTask entity, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entity);

            var taskId = await base.CreateAsync(entity, cancellationToken);

            Logger.LogInformation("Created async task: {TaskId} of type {TaskType} for virtual key {VirtualKeyId}",
                entity.Id, entity.Type, entity.VirtualKeyId);

            return taskId;

        }

        /// <inheritdoc/>
        public override async Task<bool> UpdateAsync(AsyncTask entity, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entity);

            try
            {
                var result = await base.UpdateAsync(entity, cancellationToken);

                if (result)
                {
                    Logger.LogInformation("Updated async task: {TaskId} with state {State}",
                        entity.Id, entity.State);
                }
                else
                {
                    Logger.LogWarning("No rows affected when updating async task: {TaskId}", entity.Id);
                }

                return result;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                Logger.LogWarning(ex, "Concurrency conflict updating async task: {TaskId}", entity.Id);
                return false;
            }


        }

        /// <inheritdoc/>
        public override async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentNullException(nameof(id));
            }

            var result = await base.DeleteAsync(id, cancellationToken);

            if (result)
            {
                Logger.LogInformation("Deleted async task: {TaskId}", id);
            }

            return result;

        }

        /// <inheritdoc/>
        public async Task<List<AsyncTask>> GetByVirtualKeyAsync(int virtualKeyId, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                return await context.AsyncTasks
                    .AsNoTracking()
                    .Where(t => t.VirtualKeyId == virtualKeyId)
                    .OrderByDescending(t => t.CreatedAt)
                    .ToListAsync(cancellationToken);
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<List<AsyncTask>> GetActiveByVirtualKeyAsync(int virtualKeyId, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                return await context.AsyncTasks
                    .AsNoTracking()
                    .Where(t => t.VirtualKeyId == virtualKeyId && !t.IsArchived)
                    .OrderByDescending(t => t.CreatedAt)
                    .ToListAsync(cancellationToken);
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<int> ArchiveOldTasksAsync(
            TimeSpan completedOlderThan,
            TimeSpan? staleActiveOlderThan = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                var completedCutoff = now.Subtract(completedOlderThan);
                var staleCutoff = staleActiveOlderThan.HasValue
                    ? now.Subtract(staleActiveOlderThan.Value)
                    : (DateTime?)null;
                var completedStates = new[] { 2, 3, 4, 5 }; // Completed, Failed, Cancelled, TimedOut

                var tasksToArchive = await context.AsyncTasks
                    .Where(t => !t.IsArchived &&
                               ((t.CompletedAt.HasValue &&
                                 t.CompletedAt.Value < completedCutoff &&
                                 completedStates.Contains(t.State)) ||
                                (staleCutoff.HasValue &&
                                 (t.State == 0 || t.State == 1) &&
                                 t.UpdatedAt < staleCutoff.Value &&
                                 (!t.LeaseExpiryTime.HasValue || t.LeaseExpiryTime < now) &&
                                 (!t.ProviderInvocationStartedAt.HasValue ||
                                  t.ProviderInvocationCompletedAt.HasValue))))
                    .ToListAsync(cancellationToken);

                foreach (var task in tasksToArchive)
                {
                    if (task.State is 0 or 1)
                    {
                        task.State = 5; // TimedOut
                        task.CompletedAt = now;
                        task.Error ??= "Task expired during retention cleanup.";
                        task.LeasedBy = null;
                        task.LeaseExpiryTime = null;
                    }
                    task.IsArchived = true;
                    task.ArchivedAt = now;
                    task.UpdatedAt = now;
                }

                var affected = await context.SaveChangesAsync(cancellationToken);

                if (affected > 0)
                {
                    Logger.LogInformation(
                        "Archived {Count} completed or stale tasks older than configured thresholds",
                        affected);
                }

                return affected;
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<List<AsyncTask>> GetTasksForCleanupAsync(TimeSpan archivedOlderThan, int limit = 100, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                var cutoffDate = DateTime.UtcNow.Subtract(archivedOlderThan);

                return await context.AsyncTasks
                    .AsNoTracking()
                    .Where(t => t.IsArchived && t.ArchivedAt.HasValue && t.ArchivedAt.Value < cutoffDate)
                    .OrderBy(t => t.ArchivedAt)
                    .Take(limit)
                    .ToListAsync(cancellationToken);
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<int> BulkDeleteAsync(IEnumerable<string> taskIds, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(taskIds);

            var taskIdList = taskIds.ToList();
            if (taskIdList.Count == 0)
            {
                return 0;
            }

            return await ExecuteAsync(async context =>
            {
                var tasksToDelete = await context.AsyncTasks
                    .Where(t => taskIdList.Contains(t.Id))
                    .ToListAsync(cancellationToken);

                context.AsyncTasks.RemoveRange(tasksToDelete);
                var affected = await context.SaveChangesAsync(cancellationToken);

                if (affected > 0)
                {
                    Logger.LogInformation("Bulk deleted {Count} async tasks", affected);
                }

                return affected;
            }, cancellationToken);

        }

        /// <inheritdoc/>
        public async Task<List<AsyncTask>> GetPendingTasksAsync(string? taskType = null, int limit = 100, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                var query = context.AsyncTasks
                    .AsNoTracking()
                    .Where(t => t.State == 0 && !t.IsArchived &&
                               (t.LeasedBy == null || t.LeaseExpiryTime == null || t.LeaseExpiryTime < now) &&
                               (t.NextRetryAt == null || t.NextRetryAt <= now));

                if (!string.IsNullOrEmpty(taskType))
                {
                    query = query.Where(t => t.Type == taskType);
                }

                return await query
                    .OrderBy(t => t.CreatedAt)
                    .Take(limit)
                    .ToListAsync(cancellationToken);
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public Task<(List<AsyncTask> Tasks, int TotalCount)> GetByStateAsync(
            int state,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            return ExecuteAsync(async context =>
            {
                var query = context.AsyncTasks.AsNoTracking()
                    .Where(task => !task.IsArchived && task.State == state);
                var totalCount = await query.CountAsync(cancellationToken);
                var tasks = await query
                    .OrderByDescending(task => task.UpdatedAt)
                    .ThenBy(task => task.Id)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(cancellationToken);
                return (tasks, totalCount);
            }, cancellationToken, nameof(GetByStateAsync));
        }

        /// <inheritdoc/>
        public async Task<AsyncTask?> LeaseNextPendingTaskAsync(string workerId, TimeSpan leaseDuration, string? taskType = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workerId))
            {
                throw new ArgumentNullException(nameof(workerId));
            }

            try
            {
                return await ExecuteAsync(async context =>
                {
                    // No explicit transaction: the single SaveChangesAsync is atomic on
                    // its own, and racing workers are arbitrated by the Version
                    // concurrency token.
                    var now = DateTime.UtcNow;
                    var query = context.AsyncTasks
                        .Where(t => t.State == 0 && !t.IsArchived &&
                                   (t.LeasedBy == null || t.LeaseExpiryTime == null || t.LeaseExpiryTime < now) &&
                                   (t.NextRetryAt == null || t.NextRetryAt <= now));

                    if (!string.IsNullOrEmpty(taskType))
                    {
                        query = query.Where(t => t.Type == taskType);
                    }

                    // Use row-level locking to prevent concurrent access
                    var task = await query
                        .OrderBy(t => t.CreatedAt)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (task != null)
                    {
                        task.LeasedBy = workerId;
                        task.LeaseExpiryTime = now.Add(leaseDuration);
                        task.UpdatedAt = now;
                        task.Version++;

                        await context.SaveChangesAsync(cancellationToken);

                        Logger.LogInformation("Worker {WorkerId} leased task {TaskId} until {ExpiryTime}",
                            workerId, task.Id, task.LeaseExpiryTime);
                    }

                    return task;
                }, cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                Logger.LogDebug(ex, "Concurrency conflict while leasing a pending task");
                return null;
            }

        }

        /// <inheritdoc/>
        public async Task<bool> ReleaseLeaseAsync(string taskId, string workerId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                throw new ArgumentNullException(nameof(taskId));
            }

            if (string.IsNullOrWhiteSpace(workerId))
            {
                throw new ArgumentNullException(nameof(workerId));
            }

            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                var task = await context.AsyncTasks
                    .FirstOrDefaultAsync(t => t.Id == taskId &&
                                             t.LeasedBy == workerId &&
                                             t.LeaseExpiryTime != null &&
                                             t.LeaseExpiryTime > now,
                                             cancellationToken);

                if (task == null)
                {
                    Logger.LogWarning("Task {TaskId} not found or not leased by worker {WorkerId}", taskId, workerId);
                    return false;
                }

                task.LeasedBy = null;
                task.LeaseExpiryTime = null;
                task.UpdatedAt = DateTime.UtcNow;
                task.Version++;

                var affected = await context.SaveChangesAsync(cancellationToken);

                if (affected > 0)
                {
                    Logger.LogInformation("Released lease on task {TaskId} by worker {WorkerId}", taskId, workerId);
                }

                return affected > 0;
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<bool> ExtendLeaseAsync(string taskId, string workerId, TimeSpan extension, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                throw new ArgumentNullException(nameof(taskId));
            }

            if (string.IsNullOrWhiteSpace(workerId))
            {
                throw new ArgumentNullException(nameof(workerId));
            }

            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                var task = await context.AsyncTasks
                    .FirstOrDefaultAsync(t => t.Id == taskId && t.LeasedBy == workerId &&
                                             t.State == 1 && !t.IsArchived &&
                                             t.LeaseExpiryTime != null && t.LeaseExpiryTime > now,
                                             cancellationToken);

                if (task == null)
                {
                    Logger.LogWarning("Task {TaskId} not found, not leased by worker {WorkerId}, or lease expired",
                        taskId, workerId);
                    return false;
                }

                task.LeaseExpiryTime = now.Add(extension);
                task.UpdatedAt = now;
                task.Version++;

                var affected = await context.SaveChangesAsync(cancellationToken);

                if (affected > 0)
                {
                    Logger.LogInformation("Extended lease on task {TaskId} by worker {WorkerId} until {ExpiryTime}",
                        taskId, workerId, task.LeaseExpiryTime);
                }

                return affected > 0;
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<List<AsyncTask>> GetExpiredLeaseTasksAsync(int limit = 100, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                return await context.AsyncTasks
                    .AsNoTracking()
                    .Where(t => t.LeasedBy != null &&
                               t.LeaseExpiryTime != null &&
                               t.LeaseExpiryTime < now &&
                               t.State == 1) // Processing state
                    .OrderBy(t => t.LeaseExpiryTime)
                    .Take(limit)
                    .ToListAsync(cancellationToken);
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateWithVersionCheckAsync(AsyncTask task, int expectedVersion, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(task);

            try
            {
                return await ExecuteAsync(async context =>
                {
                    task.UpdatedAt = DateTime.UtcNow;
                    task.Version = expectedVersion + 1;

                    context.AsyncTasks.Attach(task);
                    context.Entry(task).State = EntityState.Modified;
                    context.Entry(task).Property(t => t.Version).OriginalValue = expectedVersion;
                    var affected = await context.SaveChangesAsync(cancellationToken);

                    if (affected > 0)
                    {
                        Logger.LogInformation("Updated task {TaskId} with version check (version {OldVersion} -> {NewVersion})",
                            task.Id, expectedVersion, task.Version);
                    }

                    return affected > 0;
                }, cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                Logger.LogWarning(ex, "Concurrency conflict updating task {TaskId} with version check", task.Id);
                return false;
            }

        }

        /// <inheritdoc/>
        public async Task<AsyncTaskClaimResult> TryClaimTaskAsync(
            string taskId,
            string workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
            ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                if (context.Database.IsRelational())
                {
                    var affected = await context.AsyncTasks
                        .Where(t => t.Id == taskId && !t.IsArchived && t.ProviderInvocationStartedAt == null && t.ProviderInvocationCompletedAt == null &&
                            (t.State == 0 || (t.State == 1 &&
                                t.ProviderInvocationStartedAt == null &&
                                t.LeaseExpiryTime < now)))
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(t => t.State, 1)
                            .SetProperty(t => t.LeasedBy, workerId)
                            .SetProperty(t => t.LeaseExpiryTime, now.Add(leaseDuration))
                            .SetProperty(t => t.UpdatedAt, now)
                            .SetProperty(t => t.Version, t => t.Version + 1), cancellationToken);
                    if (affected == 1)
                    {
                        return AsyncTaskClaimResult.Claimed;
                    }
                }
                else
                {
                    var pending = await context.AsyncTasks.SingleOrDefaultAsync(
                        t => t.Id == taskId && !t.IsArchived && t.ProviderInvocationStartedAt == null && t.ProviderInvocationCompletedAt == null &&
                            (t.State == 0 || (t.State == 1 &&
                                t.ProviderInvocationStartedAt == null &&
                                t.LeaseExpiryTime < now)), cancellationToken);
                    if (pending != null)
                    {
                        pending.State = 1;
                        pending.LeasedBy = workerId;
                        pending.LeaseExpiryTime = now.Add(leaseDuration);
                        pending.UpdatedAt = now;
                        pending.Version++;
                        await context.SaveChangesAsync(cancellationToken);
                        return AsyncTaskClaimResult.Claimed;
                    }
                }

                var uncertain = await context.AsyncTasks.SingleOrDefaultAsync(t =>
                    t.Id == taskId && !t.IsArchived && (t.State == 0 || t.State == 1 && t.LeaseExpiryTime < now) &&
                    (t.ProviderInvocationStartedAt != null || t.ProviderInvocationCompletedAt != null),
                    cancellationToken);
                if (uncertain != null)
                {
                    uncertain.State = 6;
                    uncertain.IsRetryable = false;
                    uncertain.Error = "Provider outcome is unknown after the processing lease expired.";
                    uncertain.LeasedBy = null;
                    uncertain.LeaseExpiryTime = null;
                    uncertain.CompletedAt = now;
                    uncertain.UpdatedAt = now;
                    uncertain.Version++;
                    await context.SaveChangesAsync(cancellationToken);
                    return AsyncTaskClaimResult.Indeterminate;
                }

                var current = await context.AsyncTasks.AsNoTracking()
                    .Where(t => t.Id == taskId)
                    .Select(t => (int?)t.State)
                    .SingleOrDefaultAsync(cancellationToken);
                return current switch
                {
                    null => AsyncTaskClaimResult.Missing,
                    6 => AsyncTaskClaimResult.Indeterminate,
                    2 or 3 or 4 or 5 => AsyncTaskClaimResult.Terminal,
                    _ => AsyncTaskClaimResult.AlreadyClaimed
                };
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public Task<bool> MarkProviderInvocationStartedAsync(
            string taskId,
            string workerId,
            CancellationToken cancellationToken = default)
            => UpdateProviderPhaseAsync(taskId, workerId, completed: false, null, cancellationToken);

        /// <inheritdoc/>
        public Task<bool> MarkProviderInvocationCompletedAsync(
            string taskId,
            string workerId,
            string? providerOperationId = null,
            CancellationToken cancellationToken = default)
            => UpdateProviderPhaseAsync(taskId, workerId, completed: true, providerOperationId, cancellationToken);

        private async Task<bool> UpdateProviderPhaseAsync(
            string taskId,
            string workerId,
            bool completed,
            string? providerOperationId,
            CancellationToken cancellationToken)
        {
            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                var query = context.AsyncTasks.Where(t => t.Id == taskId && !t.IsArchived
                    && t.State == 1 && t.LeasedBy == workerId && t.LeaseExpiryTime > now
                    && (completed ? t.ProviderInvocationStartedAt != null :
                        t.ProviderInvocationStartedAt == null && t.ProviderInvocationCompletedAt == null));
                if (context.Database.IsRelational())
                {
                    var affected = completed
                        ? await query.ExecuteUpdateAsync(setters => setters
                            .SetProperty(t => t.ProviderInvocationCompletedAt, now)
                            .SetProperty(t => t.ProviderOperationId, providerOperationId)
                            .SetProperty(t => t.UpdatedAt, now), cancellationToken)
                        : await query.ExecuteUpdateAsync(setters => setters
                            .SetProperty(t => t.ProviderInvocationStartedAt, now)
                            .SetProperty(t => t.UpdatedAt, now), cancellationToken);
                    return affected == 1;
                }

                var task = await query.SingleOrDefaultAsync(cancellationToken);
                if (task == null) return false;
                if (completed)
                {
                    task.ProviderInvocationCompletedAt = now;
                    task.ProviderOperationId = providerOperationId;
                }
                else
                {
                    task.ProviderInvocationStartedAt = now;
                }
                task.UpdatedAt = now;
                return await context.SaveChangesAsync(cancellationToken) == 1;
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<bool> FailIndeterminateTaskWithoutChargeAsync(
            string taskId,
            string reason,
            string? providerOperationId = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                var query = context.AsyncTasks.Where(task => task.Id == taskId && task.State == 6);
                var affected = providerOperationId == null
                    ? await query.ExecuteUpdateAsync(setters => setters
                        .SetProperty(task => task.State, 3)
                        .SetProperty(task => task.IsRetryable, false)
                        .SetProperty(task => task.Error, reason)
                        .SetProperty(task => task.LeasedBy, (string?)null)
                        .SetProperty(task => task.LeaseExpiryTime, (DateTime?)null)
                        .SetProperty(task => task.UpdatedAt, now)
                        .SetProperty(task => task.CompletedAt, now)
                        .SetProperty(task => task.Version, task => task.Version + 1),
                        cancellationToken)
                    : await query.ExecuteUpdateAsync(setters => setters
                        .SetProperty(task => task.State, 3)
                        .SetProperty(task => task.IsRetryable, false)
                        .SetProperty(task => task.Error, reason)
                        .SetProperty(task => task.ProviderOperationId, providerOperationId)
                        .SetProperty(task => task.LeasedBy, (string?)null)
                        .SetProperty(task => task.LeaseExpiryTime, (DateTime?)null)
                        .SetProperty(task => task.UpdatedAt, now)
                        .SetProperty(task => task.CompletedAt, now)
                        .SetProperty(task => task.Version, task => task.Version + 1),
                        cancellationToken);
                return affected == 1;
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<IndeterminateTaskRetryPreparation> PrepareIndeterminateTaskRetryAsync(
            string taskId,
            string dispatchId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
            ArgumentException.ThrowIfNullOrWhiteSpace(dispatchId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (dispatchId.Length > 64)
                throw new ArgumentOutOfRangeException(nameof(dispatchId));

            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                var mediaTypes = new[] { "image_generation", "video_generation" };
                var affected = await context.AsyncTasks
                    .Where(task => task.Id == taskId && task.State == 6 &&
                        mediaTypes.Contains(task.Type) && task.RetryCount < task.MaxRetries)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(task => task.State, 0)
                        .SetProperty(task => task.IsRetryable, true)
                        .SetProperty(task => task.Error, reason)
                        .SetProperty(task => task.RetryDispatchId, dispatchId)
                        .SetProperty(task => task.LeasedBy, (string?)null)
                        .SetProperty(task => task.LeaseExpiryTime, (DateTime?)null)
                        .SetProperty(task => task.ProviderInvocationStartedAt, (DateTime?)null)
                        .SetProperty(task => task.ProviderInvocationCompletedAt, (DateTime?)null)
                        .SetProperty(task => task.ProviderOperationId, (string?)null)
                        .SetProperty(task => task.CompletedAt, (DateTime?)null)
                        .SetProperty(task => task.NextRetryAt, (DateTime?)null)
                        .SetProperty(task => task.UpdatedAt, now)
                        .SetProperty(task => task.RetryCount, task => task.RetryCount + 1)
                        .SetProperty(task => task.Version, task => task.Version + 1),
                        cancellationToken);

                var task = await context.AsyncTasks.AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);
                if (affected == 1)
                {
                    return new IndeterminateTaskRetryPreparation(
                        IndeterminateTaskRetryPreparationStatus.Prepared, task);
                }
                if (task == null)
                {
                    return new IndeterminateTaskRetryPreparation(
                        IndeterminateTaskRetryPreparationStatus.Missing);
                }
                if (task.State == 0 && task.RetryDispatchId == dispatchId)
                {
                    return new IndeterminateTaskRetryPreparation(
                        IndeterminateTaskRetryPreparationStatus.AlreadyPrepared, task);
                }
                if (!mediaTypes.Contains(task.Type))
                {
                    return new IndeterminateTaskRetryPreparation(
                        IndeterminateTaskRetryPreparationStatus.UnsupportedTaskType, task);
                }
                if (task.State == 6 && task.RetryCount >= task.MaxRetries)
                {
                    return new IndeterminateTaskRetryPreparation(
                        IndeterminateTaskRetryPreparationStatus.RetryLimitExceeded, task);
                }
                return new IndeterminateTaskRetryPreparation(
                    IndeterminateTaskRetryPreparationStatus.NotIndeterminate, task);
            }, cancellationToken, nameof(PrepareIndeterminateTaskRetryAsync));
        }

        /// <inheritdoc/>
        public async Task<ExpiredTaskRecoveryResult> RecoverExpiredMediaTasksAsync(
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                var now = DateTime.UtcNow;
                var mediaTypes = new[] { "image_generation", "video_generation" };
                if (context.Database.IsRelational())
                {
                    var reset = await context.AsyncTasks
                        .Where(t => t.State == 1 && mediaTypes.Contains(t.Type) &&
                            t.LeaseExpiryTime < now && t.ProviderInvocationStartedAt == null)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(t => t.State, 0)
                            .SetProperty(t => t.LeasedBy, (string?)null)
                            .SetProperty(t => t.LeaseExpiryTime, (DateTime?)null)
                            .SetProperty(t => t.UpdatedAt, now)
                            .SetProperty(t => t.Version, t => t.Version + 1), cancellationToken);
                    var uncertain = await context.AsyncTasks
                        .Where(t => t.State == 1 && mediaTypes.Contains(t.Type) &&
                            t.LeaseExpiryTime < now && t.ProviderInvocationStartedAt != null)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(t => t.State, 6)
                            .SetProperty(t => t.IsRetryable, false)
                            .SetProperty(t => t.Error, "Provider outcome is unknown after the processing lease expired.")
                            .SetProperty(t => t.LeasedBy, (string?)null)
                            .SetProperty(t => t.LeaseExpiryTime, (DateTime?)null)
                            .SetProperty(t => t.CompletedAt, now)
                            .SetProperty(t => t.UpdatedAt, now)
                            .SetProperty(t => t.Version, t => t.Version + 1), cancellationToken);
                    return new ExpiredTaskRecoveryResult(reset, uncertain);
                }

                var expired = await context.AsyncTasks.Where(t => t.State == 1 &&
                    mediaTypes.Contains(t.Type) && t.LeaseExpiryTime < now).ToListAsync(cancellationToken);
                var resetCount = 0;
                var uncertainCount = 0;
                foreach (var task in expired)
                {
                    task.LeasedBy = null;
                    task.LeaseExpiryTime = null;
                    task.UpdatedAt = now;
                    task.Version++;
                    if (task.ProviderInvocationStartedAt == null)
                    {
                        task.State = 0;
                        resetCount++;
                    }
                    else
                    {
                        task.State = 6;
                        task.IsRetryable = false;
                        task.Error = "Provider outcome is unknown after the processing lease expired.";
                        task.CompletedAt = now;
                        uncertainCount++;
                    }
                }
                await context.SaveChangesAsync(cancellationToken);
                return new ExpiredTaskRecoveryResult(resetCount, uncertainCount);
            }, cancellationToken);
        }
    }
}
