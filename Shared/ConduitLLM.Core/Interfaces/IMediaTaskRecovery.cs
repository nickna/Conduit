namespace ConduitLLM.Core.Interfaces;

/// <summary>Reconciles a bounded set of media tasks with durable replacement dispatch.</summary>
public interface IMediaTaskRecovery
{
    Task<MediaTaskRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default);
}

public sealed record MediaTaskRecoveryResult(int ResetToPending, int Redispatched, int MarkedIndeterminate, int Blocked);
