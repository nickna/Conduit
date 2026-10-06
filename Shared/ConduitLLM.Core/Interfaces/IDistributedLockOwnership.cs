namespace ConduitLLM.Core.Interfaces;

/// <summary>
/// Ownership lasts until asynchronous disposal, independently of acquisition waiting
/// and operation deadlines. Loss notification is asynchronous and is not fencing.
/// </summary>
public interface IDistributedLockOwnership : IAsyncDisposable
{
    CancellationToken HandleLostToken { get; }
}
