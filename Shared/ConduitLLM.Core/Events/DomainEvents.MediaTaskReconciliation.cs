namespace ConduitLLM.Core.Events;

/// <summary>Recovers verified output of the original operation without invoking generation.</summary>
public sealed record IndeterminateMediaTaskRecoveryRequested : DomainEvent
{
    public string TaskId { get; init; } = string.Empty;
    public string DispatchId { get; init; } = string.Empty;
}

/// <summary>
/// Durable operator command requesting a retry after the provider confirmed that
/// repeating an indeterminate media operation is safe.
/// </summary>
public sealed record IndeterminateMediaTaskRetryRequested : DomainEvent
{
    public string TaskId { get; init; } = string.Empty;
    public string DispatchId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string RequestedBy { get; init; } = "Admin";
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
}
