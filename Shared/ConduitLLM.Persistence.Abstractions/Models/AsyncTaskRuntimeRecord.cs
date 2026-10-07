namespace ConduitLLM.Persistence;

/// <summary>
/// Backend-neutral snapshot of the durable async-task fields used by Gateway runtime flows.
/// </summary>
public sealed class AsyncTaskRuntimeRecord
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int State { get; set; }
    public string? Payload { get; set; }
    public int Progress { get; set; }
    public string? ProgressMessage { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int VirtualKeyId { get; set; }
    public string? Metadata { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public string? LeasedBy { get; set; }
    public DateTime? LeaseExpiryTime { get; set; }
    public DateTime? ProviderInvocationStartedAt { get; set; }
    public DateTime? ProviderInvocationCompletedAt { get; set; }
    public string? ProviderOperationId { get; set; }
    public string? RetryDispatchId { get; set; }
    public int Version { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public bool IsRetryable { get; set; } = true;
    public DateTime? NextRetryAt { get; set; }
}

public sealed record AsyncTaskRuntimePage(
    IReadOnlyList<AsyncTaskRuntimeRecord> Tasks,
    int TotalCount);

public enum AsyncTaskRuntimeClaimStatus
{
    Claimed = 0,
    AlreadyClaimed = 1,
    Terminal = 2,
    Missing = 3,
    Indeterminate = 4
}

public enum AsyncTaskRuntimeRetryStatus
{
    Prepared = 0,
    AlreadyPrepared = 1,
    Missing = 2,
    NotIndeterminate = 3,
    UnsupportedTaskType = 4,
    RetryLimitExceeded = 5
}

public sealed record AsyncTaskRuntimeRetryPreparation(
    AsyncTaskRuntimeRetryStatus Status,
    AsyncTaskRuntimeRecord? Task = null);
