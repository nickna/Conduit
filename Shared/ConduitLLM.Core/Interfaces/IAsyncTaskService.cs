using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Interfaces
{
    /// <summary>
    /// Service for managing asynchronous tasks that may take a long time to complete.
    /// This is used for operations like image/video generation that require polling for results.
    /// </summary>
    public interface IAsyncTaskService
    {
        /// <summary>
        /// Creates a new async task and returns a task ID for tracking.
        /// </summary>
        /// <param name="taskType">The type of task (e.g., "image_generation", "video_generation")</param>
        /// <param name="metadata">Additional metadata about the task</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>A unique task ID for tracking the task</returns>
        Task<string> CreateTaskAsync(string taskType, object metadata, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a new async task with explicit virtual key ID and returns a task ID for tracking.
        /// </summary>
        /// <param name="taskType">The type of task (e.g., "image_generation", "video_generation")</param>
        /// <param name="virtualKeyId">The virtual key ID associated with this task</param>
        /// <param name="metadata">Additional metadata about the task</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>A unique task ID for tracking the task</returns>
        Task<string> CreateTaskAsync(string taskType, int virtualKeyId, object metadata, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the current status of a task.
        /// </summary>
        /// <param name="taskId">The ID of the task to check</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The current task status</returns>
        Task<AsyncTaskStatus?> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates the status of a task.
        /// </summary>
        /// <param name="taskId">The ID of the task to update</param>
        /// <param name="status">The new status</param>
        /// <param name="result">Optional result data</param>
        /// <param name="error">Optional error message</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task UpdateTaskStatusAsync(string taskId, TaskState status, int? progress = null, object? result = null, string? error = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Polls a task until it completes, fails, or times out.
        /// </summary>
        /// <param name="taskId">The ID of the task to poll</param>
        /// <param name="pollingInterval">How often to check the status</param>
        /// <param name="timeout">Maximum time to wait for completion</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The final task status with result</returns>
        Task<AsyncTaskStatus> PollTaskUntilCompletedAsync(
            string taskId, 
            TimeSpan pollingInterval, 
            TimeSpan timeout, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancels a running task.
        /// </summary>
        /// <param name="taskId">The ID of the task to cancel</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task CancelTaskAsync(string taskId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a task and its associated data.
        /// </summary>
        /// <param name="taskId">The ID of the task to delete</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default);

        /// <summary>Archives and deletes tasks according to the supplied retention policy.</summary>
        Task<AsyncTaskCleanupResult> CleanupOldTasksAsync(
            AsyncTaskRetentionPolicy policy,
            CancellationToken cancellationToken = default);

        Task<ConduitLLM.Configuration.Interfaces.AsyncTaskClaimResult> TryClaimTaskAsync(
            string taskId,
            string workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default);

        Task<bool> MarkProviderInvocationStartedAsync(
            string taskId,
            string workerId,
            CancellationToken cancellationToken = default);

        Task<bool> MarkProviderInvocationCompletedAsync(
            string taskId,
            string workerId,
            string? providerOperationId = null,
            CancellationToken cancellationToken = default);

        Task<bool> ExtendTaskLeaseAsync(
            string taskId,
            string workerId,
            TimeSpan extension,
            CancellationToken cancellationToken = default);

        /// <summary>Returns a sanitized page of tasks in the requested state.</summary>
        Task<AsyncTaskSummaryPage> GetTasksByStateAsync(
            TaskState state,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default);

        /// <summary>Marks an indeterminate task failed without charging the customer.</summary>
        Task<bool> FailIndeterminateTaskWithoutChargeAsync(
            string taskId,
            string reason,
            string? providerOperationId = null,
            CancellationToken cancellationToken = default);

        /// <summary>Idempotently prepares a media task for a durable operator retry.</summary>
        Task<MediaTaskRetryPreparation> PrepareIndeterminateTaskRetryAsync(
            string taskId,
            string dispatchId,
            string reason,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents the status of an async task.
    /// </summary>
    public class AsyncTaskStatus
    {
        /// <summary>
        /// Unique identifier for the task.
        /// </summary>
        public string TaskId { get; set; } = "";

        /// <summary>
        /// The type of task (e.g., "image_generation", "video_generation").
        /// </summary>
        public string TaskType { get; set; } = "";

        /// <summary>
        /// Current state of the task.
        /// </summary>
        public TaskState State { get; set; }

        /// <summary>
        /// When the task was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When the task was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// When the task completed (if applicable).
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// The result of the task (if completed successfully).
        /// </summary>
        public object? Result { get; set; }

        /// <summary>
        /// Error message if the task failed.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Additional metadata about the task.
        /// </summary>
        public TaskMetadata? Metadata { get; set; }

        /// <summary>
        /// Progress percentage (0-100) if available.
        /// </summary>
        public int Progress { get; set; }

        /// <summary>
        /// Progress message if available.
        /// </summary>
        public string? ProgressMessage { get; set; }

        /// <summary>
        /// Number of retry attempts made for this task.
        /// </summary>
        public int RetryCount { get; set; }

        /// <summary>
        /// Maximum number of retry attempts allowed for this task.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Whether the task is retryable if it fails.
        /// </summary>
        public bool IsRetryable { get; set; } = true;

        /// <summary>
        /// When the task should be retried next (null if not scheduled for retry).
        /// </summary>
        public DateTime? NextRetryAt { get; set; }
    }

    /// <summary>
    /// Possible states for an async task.
    /// </summary>
    public enum TaskState
    {
        /// <summary>
        /// Task has been created but not started.
        /// </summary>
        Pending,

        /// <summary>
        /// Task is currently being processed.
        /// </summary>
        Processing,

        /// <summary>
        /// Task completed successfully.
        /// </summary>
        Completed,

        /// <summary>
        /// Task failed with an error.
        /// </summary>
        Failed,

        /// <summary>
        /// Task was cancelled.
        /// </summary>
        Cancelled,

        /// <summary>
        /// Task timed out.
        /// </summary>
        TimedOut,

        /// <summary>The provider may have accepted work; automatic retry is unsafe.</summary>
        Indeterminate
    }

    /// <summary>Safe task data intended for operator listings; excludes payloads and secrets.</summary>
    public sealed record AsyncTaskSummary(
        string TaskId,
        string TaskType,
        TaskState State,
        int VirtualKeyId,
        string? Model,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? CompletedAt,
        string? Error,
        int RetryCount,
        int MaxRetries,
        DateTime? ProviderInvocationStartedAt,
        DateTime? ProviderInvocationCompletedAt,
        string? ProviderOperationId);

    public sealed record AsyncTaskSummaryPage(
        IReadOnlyList<AsyncTaskSummary> Tasks,
        int Page,
        int PageSize,
        int TotalCount);

    public enum MediaTaskRetryPreparationStatus
    {
        Prepared = 0,
        AlreadyPrepared = 1,
        Missing = 2,
        NotIndeterminate = 3,
        UnsupportedTaskType = 4,
        RetryLimitExceeded = 5
    }

    public sealed record MediaTaskRetryPreparation(
        MediaTaskRetryPreparationStatus Status,
        string? TaskType = null,
        TaskMetadata? Metadata = null);

    /// <summary>Thresholds used for one async-task retention pass.</summary>
    public sealed record AsyncTaskRetentionPolicy(
        TimeSpan ArchiveCompletedAfter,
        TimeSpan DeleteArchivedAfter,
        TimeSpan ArchiveStaleAfter,
        int BatchSize = 500);

    /// <summary>Counts produced by one async-task retention pass.</summary>
    public sealed record AsyncTaskCleanupResult(int Archived, int Deleted)
    {
        public int Total => Archived + Deleted;
    }
}
