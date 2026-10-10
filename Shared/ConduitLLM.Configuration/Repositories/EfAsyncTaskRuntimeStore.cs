using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// EF reference adapter for the Gateway async-task runtime contract.
/// </summary>
public sealed class EfAsyncTaskRuntimeStore : IAsyncTaskRuntimeStore
{
    private readonly IAsyncTaskRepository _repository;

    public EfAsyncTaskRuntimeStore(IAsyncTaskRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<string> CreateAsync(
        AsyncTaskRuntimeRecord task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var entity = ToEntity(task);
        var id = await _repository.CreateAsync(entity, cancellationToken);
        task.CreatedAt = entity.CreatedAt;
        task.UpdatedAt = entity.UpdatedAt;
        return id;
    }

    public async Task<AsyncTaskRuntimeRecord?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var task = await _repository.GetByIdAsync(id, cancellationToken);
        return task is null ? null : ToRecord(task);
    }

    public async Task<bool> UpdateAsync(
        AsyncTaskRuntimeRecord task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var entity = ToEntity(task);
        var updated = await _repository.UpdateAsync(entity, cancellationToken);
        task.UpdatedAt = entity.UpdatedAt;
        return updated;
    }

    public Task<bool> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(id, cancellationToken);

    public Task<int> ArchiveOldTasksAsync(
        TimeSpan completedOlderThan,
        TimeSpan? staleActiveOlderThan = null,
        CancellationToken cancellationToken = default) =>
        _repository.ArchiveOldTasksAsync(
            completedOlderThan,
            staleActiveOlderThan,
            cancellationToken);

    public async Task<IReadOnlyList<string>> GetTaskIdsForCleanupAsync(
        TimeSpan archivedOlderThan,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        (await _repository.GetTasksForCleanupAsync(
            archivedOlderThan,
            limit,
            cancellationToken))
        .Select(task => task.Id)
        .ToArray();

    public Task<int> BulkDeleteAsync(
        IReadOnlyCollection<string> taskIds,
        CancellationToken cancellationToken = default) =>
        _repository.BulkDeleteAsync(taskIds, cancellationToken);

    public async Task<AsyncTaskRuntimePage> GetByStateAsync(
        int state,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (tasks, totalCount) = await _repository.GetByStateAsync(
            state,
            page,
            pageSize,
            cancellationToken);
        return new AsyncTaskRuntimePage(tasks.Select(ToRecord).ToArray(), totalCount);
    }

    public async Task<AsyncTaskRuntimeClaimStatus> TryClaimTaskAsync(
        string taskId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        (AsyncTaskRuntimeClaimStatus)(int)await _repository.TryClaimTaskAsync(
            taskId,
            workerId,
            leaseDuration,
            cancellationToken);

    public Task<bool> MarkProviderInvocationStartedAsync(
        string taskId,
        string workerId,
        CancellationToken cancellationToken = default) =>
        _repository.MarkProviderInvocationStartedAsync(taskId, workerId, cancellationToken);

    public Task<bool> MarkProviderInvocationCompletedAsync(
        string taskId,
        string workerId,
        string? providerOperationId = null,
        CancellationToken cancellationToken = default) =>
        _repository.MarkProviderInvocationCompletedAsync(
            taskId,
            workerId,
            providerOperationId,
            cancellationToken);

    public Task<bool> ExtendLeaseAsync(
        string taskId,
        string workerId,
        TimeSpan extension,
        CancellationToken cancellationToken = default) =>
        _repository.ExtendLeaseAsync(taskId, workerId, extension, cancellationToken);

    public Task<bool> FailIndeterminateTaskWithoutChargeAsync(
        string taskId,
        string reason,
        string? providerOperationId = null,
        CancellationToken cancellationToken = default) =>
        _repository.FailIndeterminateTaskWithoutChargeAsync(
            taskId,
            reason,
            providerOperationId,
            cancellationToken);

    public async Task<AsyncTaskRuntimeRetryPreparation> PrepareIndeterminateTaskRetryAsync(
        string taskId,
        string dispatchId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var result = await _repository.PrepareIndeterminateTaskRetryAsync(
            taskId,
            dispatchId,
            reason,
            cancellationToken);
        return new AsyncTaskRuntimeRetryPreparation(
            (AsyncTaskRuntimeRetryStatus)(int)result.Status,
            result.Task is null ? null : ToRecord(result.Task));
    }

    private static AsyncTaskRuntimeRecord ToRecord(AsyncTask task) => new()
    {
        Id = task.Id,
        Type = task.Type,
        State = task.State,
        Payload = task.Payload,
        Progress = task.Progress,
        ProgressMessage = task.ProgressMessage,
        Result = task.Result,
        Error = task.Error,
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt,
        CompletedAt = task.CompletedAt,
        VirtualKeyId = task.VirtualKeyId,
        Metadata = task.Metadata,
        IsArchived = task.IsArchived,
        ArchivedAt = task.ArchivedAt,
        LeasedBy = task.LeasedBy,
        LeaseExpiryTime = task.LeaseExpiryTime,
        ProviderInvocationStartedAt = task.ProviderInvocationStartedAt,
        ProviderInvocationCompletedAt = task.ProviderInvocationCompletedAt,
        ProviderOperationId = task.ProviderOperationId,
        RetryDispatchId = task.RetryDispatchId,
        Version = task.Version,
        RetryCount = task.RetryCount,
        MaxRetries = task.MaxRetries,
        IsRetryable = task.IsRetryable,
        NextRetryAt = task.NextRetryAt
    };

    private static AsyncTask ToEntity(AsyncTaskRuntimeRecord task) => new()
    {
        Id = task.Id,
        Type = task.Type,
        State = task.State,
        Payload = task.Payload,
        Progress = task.Progress,
        ProgressMessage = task.ProgressMessage,
        Result = task.Result,
        Error = task.Error,
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt,
        CompletedAt = task.CompletedAt,
        VirtualKeyId = task.VirtualKeyId,
        Metadata = task.Metadata,
        IsArchived = task.IsArchived,
        ArchivedAt = task.ArchivedAt,
        LeasedBy = task.LeasedBy,
        LeaseExpiryTime = task.LeaseExpiryTime,
        ProviderInvocationStartedAt = task.ProviderInvocationStartedAt,
        ProviderInvocationCompletedAt = task.ProviderInvocationCompletedAt,
        ProviderOperationId = task.ProviderOperationId,
        RetryDispatchId = task.RetryDispatchId,
        Version = task.Version,
        RetryCount = task.RetryCount,
        MaxRetries = task.MaxRetries,
        IsRetryable = task.IsRetryable,
        NextRetryAt = task.NextRetryAt
    };
}
