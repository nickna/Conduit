using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Interfaces;

public enum MediaReconciliationAcceptance { Accepted, AlreadyAccepted, Missing, Conflict, Invalid }
public sealed record MediaReconciliationTask(string TaskId, string TaskType, TaskMetadata Metadata,
    MediaTaskReconciliation Decision);

/// <summary>Atomically reserves indeterminate tasks and dispatches verified output recovery.</summary>
public interface IMediaTaskReconciliationStore
{
    Task<MediaReconciliationAcceptance> AcceptAsync(string taskId, MediaTaskReconciliation decision, CancellationToken cancellationToken = default);
    Task<MediaReconciliationTask?> GetAsync(string taskId, string dispatchId, CancellationToken cancellationToken = default);
    Task BlockAsync(string taskId, string dispatchId, CancellationToken cancellationToken = default);
    Task RecordInvocationAsync(string taskId, string workerId, MediaProviderOperation provider, CancellationToken cancellationToken = default);
    Task RecordOperationAsync(string taskId, string workerId, MediaProviderOperation provider, CancellationToken cancellationToken = default);
}
