using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Configuration.Interfaces
{
    /// <summary>
    /// Repository interface for managing async tasks.
    /// Extends IRepositoryBase for standard CRUD operations.
    /// </summary>
    public interface IAsyncTaskRepository : IRepositoryBase<AsyncTask, string>
    {
        /// <summary>
        /// Archives completed tasks older than the specified timespan.
        /// </summary>
        /// <param name="olderThan">The age threshold for archiving.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of tasks archived.</returns>
        Task<int> ArchiveOldTasksAsync(
            TimeSpan completedOlderThan,
            TimeSpan? staleActiveOlderThan = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets tasks that need to be cleaned up (archived and older than specified timespan).
        /// </summary>
        /// <param name="archivedOlderThan">The age threshold for archived tasks.</param>
        /// <param name="limit">Maximum number of tasks to return.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>List of tasks to clean up.</returns>
        Task<List<AsyncTask>> GetTasksForCleanupAsync(TimeSpan archivedOlderThan, int limit = 100, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes multiple tasks by their IDs.
        /// </summary>
        /// <param name="taskIds">The task IDs to delete.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of tasks deleted.</returns>
        Task<int> BulkDeleteAsync(IEnumerable<string> taskIds, CancellationToken cancellationToken = default);

        /// <summary>Gets one page of non-archived tasks in the requested state.</summary>
        Task<(List<AsyncTask> Tasks, int TotalCount)> GetByStateAsync(
            int state,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Extends the lease on a task.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="workerId">The worker ID that holds the lease.</param>
        /// <param name="extension">How long to extend the lease.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if the lease was extended, false otherwise.</returns>
        Task<bool> ExtendLeaseAsync(string taskId, string workerId, TimeSpan extension, CancellationToken cancellationToken = default);

        /// <summary>Atomically claims a specific pending task for one media consumer.</summary>
        Task<AsyncTaskClaimResult> TryClaimTaskAsync(
            string taskId,
            string workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default);

        /// <summary>Records that the external provider may now have accepted work.</summary>
        Task<bool> MarkProviderInvocationStartedAsync(
            string taskId,
            string workerId,
            CancellationToken cancellationToken = default);

        /// <summary>Records that the provider returned a definitive result.</summary>
        Task<bool> MarkProviderInvocationCompletedAsync(
            string taskId,
            string workerId,
            string? providerOperationId = null,
            CancellationToken cancellationToken = default);

        Task<bool> FailIndeterminateTaskWithoutChargeAsync(
            string taskId,
            string reason,
            string? providerOperationId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically prepares an indeterminate media task for an operator-approved retry.
        /// Repeating the same dispatch ID is idempotent so a redelivered reconciliation
        /// command can safely publish the follow-on generation event.
        /// </summary>
        Task<IndeterminateTaskRetryPreparation> PrepareIndeterminateTaskRetryAsync(
            string taskId,
            string dispatchId,
            string reason,
            CancellationToken cancellationToken = default);

    }

    public enum AsyncTaskClaimResult
    {
        Claimed = 0,
        AlreadyClaimed = 1,
        Terminal = 2,
        Missing = 3,
        Indeterminate = 4
    }

    public enum IndeterminateTaskRetryPreparationStatus
    {
        Prepared = 0,
        AlreadyPrepared = 1,
        Missing = 2,
        NotIndeterminate = 3,
        UnsupportedTaskType = 4,
        RetryLimitExceeded = 5
    }

    public sealed record IndeterminateTaskRetryPreparation(
        IndeterminateTaskRetryPreparationStatus Status,
        AsyncTask? Task = null);
}
