using System.Text.Json;

using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Configuration.Interfaces;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Advanced operations for HybridAsyncTaskService.
    /// </summary>
    public partial class HybridAsyncTaskService
    {
        /// <inheritdoc/>
        public async Task<AsyncTaskClaimResult> TryClaimTaskAsync(
            string taskId,
            string workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            var result = await _repository.TryClaimTaskAsync(
                taskId, workerId, leaseDuration, cancellationToken);
            if (result == AsyncTaskClaimResult.Claimed)
            {
                await _cache.RemoveAsync(GetTaskKey(taskId), cancellationToken);
            }
            return result;
        }

        /// <inheritdoc/>
        public Task<bool> MarkProviderInvocationStartedAsync(
            string taskId,
            string workerId,
            CancellationToken cancellationToken = default)
            => _repository.MarkProviderInvocationStartedAsync(taskId, workerId, cancellationToken);

        /// <inheritdoc/>
        public Task<bool> MarkProviderInvocationCompletedAsync(
            string taskId,
            string workerId,
            string? providerOperationId = null,
            CancellationToken cancellationToken = default)
            => _repository.MarkProviderInvocationCompletedAsync(
                taskId, workerId, providerOperationId, cancellationToken);

        /// <inheritdoc/>
        public Task<bool> ExtendTaskLeaseAsync(
            string taskId,
            string workerId,
            TimeSpan extension,
            CancellationToken cancellationToken = default)
            => _repository.ExtendLeaseAsync(taskId, workerId, extension, cancellationToken);

        /// <inheritdoc/>
        public async Task<AsyncTaskSummaryPage> GetTasksByStateAsync(
            TaskState state,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var (tasks, totalCount) = await _repository.GetByStateAsync(
                (int)state, page, pageSize, cancellationToken);
            var summaries = new List<AsyncTaskSummary>(tasks.Count);
            foreach (var task in tasks)
            {
                ConduitLLM.Core.Models.TaskMetadata? metadata = null;
                if (!string.IsNullOrEmpty(task.Metadata))
                {
                    try
                    {
                        metadata = JsonSerializer.Deserialize(
                            task.Metadata,
                            AsyncTaskJsonContext.Default.TaskMetadata);
                    }
                    catch (JsonException exception)
                    {
                        _logger.LogWarning(exception,
                            "Ignoring malformed metadata while listing async task {TaskId}", task.Id);
                    }
                }
                summaries.Add(new AsyncTaskSummary(
                    task.Id,
                    task.Type,
                    (TaskState)task.State,
                    task.VirtualKeyId,
                    metadata?.Model,
                    task.CreatedAt,
                    task.UpdatedAt,
                    task.CompletedAt,
                    task.Error,
                    task.RetryCount,
                    task.MaxRetries,
                    task.ProviderInvocationStartedAt,
                    task.ProviderInvocationCompletedAt,
                    task.ProviderOperationId));
            }
            return new AsyncTaskSummaryPage(summaries, page, pageSize, totalCount);
        }

        /// <inheritdoc/>
        public async Task<bool> FailIndeterminateTaskWithoutChargeAsync(
            string taskId,
            string reason,
            string? providerOperationId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            var updated = await _repository.FailIndeterminateTaskWithoutChargeAsync(
                taskId, reason, providerOperationId, cancellationToken);
            if (updated)
            {
                await _cache.RemoveAsync(GetTaskKey(taskId), cancellationToken);
                if (_eventBus != null)
                {
                    await _eventBus.PublishAsync(new AsyncTaskUpdated
                    {
                        TaskId = taskId,
                        State = TaskState.Failed.ToString(),
                        IsCompleted = true
                    }, cancellationToken);
                }
            }
            return updated;
        }

        /// <inheritdoc/>
        public async Task<MediaTaskRetryPreparation> PrepareIndeterminateTaskRetryAsync(
            string taskId,
            string dispatchId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            var result = await _repository.PrepareIndeterminateTaskRetryAsync(
                taskId, dispatchId, reason, cancellationToken);
            if (result.Task != null)
            {
                await _cache.RemoveAsync(GetTaskKey(taskId), cancellationToken);
            }

            var status = result.Status switch
            {
                IndeterminateTaskRetryPreparationStatus.Prepared => MediaTaskRetryPreparationStatus.Prepared,
                IndeterminateTaskRetryPreparationStatus.AlreadyPrepared => MediaTaskRetryPreparationStatus.AlreadyPrepared,
                IndeterminateTaskRetryPreparationStatus.Missing => MediaTaskRetryPreparationStatus.Missing,
                IndeterminateTaskRetryPreparationStatus.NotIndeterminate => MediaTaskRetryPreparationStatus.NotIndeterminate,
                IndeterminateTaskRetryPreparationStatus.UnsupportedTaskType => MediaTaskRetryPreparationStatus.UnsupportedTaskType,
                IndeterminateTaskRetryPreparationStatus.RetryLimitExceeded => MediaTaskRetryPreparationStatus.RetryLimitExceeded,
                _ => throw new ArgumentOutOfRangeException()
            };
            var metadata = result.Task?.Metadata is { Length: > 0 } json
                ? JsonSerializer.Deserialize(json, AsyncTaskJsonContext.Default.TaskMetadata)
                : null;
            return new MediaTaskRetryPreparation(status, result.Task?.Type, metadata);
        }

        /// <inheritdoc/>
        public async Task CancelTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            await UpdateTaskStatusAsync(taskId, TaskState.Cancelled, error: "Task was cancelled", cancellationToken: cancellationToken);
        }

        /// <inheritdoc/>
        public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            // Delete from cache first
            var key = GetTaskKey(taskId);
            await _cache.RemoveAsync(key, cancellationToken);
            
            // Delete from database
            await _repository.DeleteAsync(taskId, cancellationToken);
            
            // Publish event if event bus is available
            if (_eventBus != null)
            {
                await _eventBus.PublishAsync(new AsyncTaskDeleted
                {
                    TaskId = taskId
                }, cancellationToken);
            }
            
            _logger.LogInformation("Deleted task {TaskId} from both cache and database", taskId);
        }

        /// <inheritdoc/>
        public async Task<AsyncTaskCleanupResult> CleanupOldTasksAsync(
            AsyncTaskRetentionPolicy policy,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(policy);
            if (policy.ArchiveCompletedAfter <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(policy.ArchiveCompletedAfter));
            if (policy.DeleteArchivedAfter <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(policy.DeleteArchivedAfter));
            if (policy.ArchiveStaleAfter <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(policy.ArchiveStaleAfter));

            var batchSize = Math.Clamp(policy.BatchSize, 1, 10_000);
            var archivedCount = await _repository.ArchiveOldTasksAsync(
                policy.ArchiveCompletedAfter,
                policy.ArchiveStaleAfter,
                cancellationToken);

            var deletedTotal = 0;
            while (true)
            {
                var tasksToDelete = await _repository.GetTasksForCleanupAsync(
                    policy.DeleteArchivedAfter,
                    batchSize,
                    cancellationToken);
                if (tasksToDelete.Count == 0)
                    break;

                var taskIds = tasksToDelete.Select(task => task.Id).ToArray();
                var deletedCount = await _repository.BulkDeleteAsync(taskIds, cancellationToken);
                if (deletedCount == 0)
                    break;

                deletedTotal += deletedCount;
                foreach (var taskId in taskIds)
                {
                    await _cache.RemoveAsync(GetTaskKey(taskId), cancellationToken);
                }
            }

            _logger.LogInformation(
                "Async task retention archived {ArchivedCount} and deleted {DeletedCount} tasks",
                archivedCount,
                deletedTotal);
            return new AsyncTaskCleanupResult(archivedCount, deletedTotal);
        }

        /// <inheritdoc/>
        public async Task<IList<AsyncTaskStatus>> GetPendingTasksAsync(string? taskType = null, int limit = 100, CancellationToken cancellationToken = default)
        {
            // Query database for pending tasks
            var pendingTasks = await _repository.GetPendingTasksAsync(taskType, limit, cancellationToken);
            var taskStatuses = new List<AsyncTaskStatus>();

            foreach (var task in pendingTasks)
            {
                var taskStatus = ConvertToTaskStatus(task);
                taskStatuses.Add(taskStatus);

                // Update cache with pending tasks
                try
                {
                    var cacheKey = GetTaskKey(task.Id);
                    var json = JsonSerializer.Serialize(
                        taskStatus,
                        AsyncTaskJsonContext.Default.AsyncTaskStatus);
                    await _cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromHours(24)
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cache pending task {TaskId}", task.Id);
                }
            }

            return taskStatuses;
        }
    }
}
