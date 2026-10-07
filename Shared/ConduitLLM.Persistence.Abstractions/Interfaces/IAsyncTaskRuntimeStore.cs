using ConduitLLM.Persistence;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>
/// Fixed-shape persistence required by Gateway asynchronous task workflows.
/// </summary>
public interface IAsyncTaskRuntimeStore
{
    Task<string> CreateAsync(
        AsyncTaskRuntimeRecord task,
        CancellationToken cancellationToken = default);

    Task<AsyncTaskRuntimeRecord?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        AsyncTaskRuntimeRecord task,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<int> ArchiveOldTasksAsync(
        TimeSpan completedOlderThan,
        TimeSpan? staleActiveOlderThan = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetTaskIdsForCleanupAsync(
        TimeSpan archivedOlderThan,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<int> BulkDeleteAsync(
        IReadOnlyCollection<string> taskIds,
        CancellationToken cancellationToken = default);

    Task<AsyncTaskRuntimePage> GetByStateAsync(
        int state,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<AsyncTaskRuntimeClaimStatus> TryClaimTaskAsync(
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

    Task<bool> ExtendLeaseAsync(
        string taskId,
        string workerId,
        TimeSpan extension,
        CancellationToken cancellationToken = default);

    Task<bool> FailIndeterminateTaskWithoutChargeAsync(
        string taskId,
        string reason,
        string? providerOperationId = null,
        CancellationToken cancellationToken = default);

    Task<AsyncTaskRuntimeRetryPreparation> PrepareIndeterminateTaskRetryAsync(
        string taskId,
        string dispatchId,
        string reason,
        CancellationToken cancellationToken = default);

}
