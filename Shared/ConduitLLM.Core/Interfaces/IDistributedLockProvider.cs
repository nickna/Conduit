namespace ConduitLLM.Core.Interfaces;

/// <summary>Coordinates work using session ownership, with no automatic lease expiry.</summary>
public interface IDistributedLockProvider
{
    /// <summary>
    /// Zero timeout attempts immediate acquisition; positive timeout bounds the wait.
    /// Null means contention only. Cancellation and infrastructure failures propagate.
    /// The caller owns disposal and must await all protected work before releasing.
    /// </summary>
    Task<IDistributedLockOwnership?> TryAcquireAsync(
        string key, TimeSpan acquisitionTimeout = default, CancellationToken cancellationToken = default);
}
