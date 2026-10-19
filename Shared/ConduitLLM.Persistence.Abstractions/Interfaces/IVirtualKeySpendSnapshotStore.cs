namespace ConduitLLM.Persistence.Interfaces;

/// <summary>
/// Optional fresh projection for spend operations that need only current key membership and balance.
/// </summary>
public interface IVirtualKeySpendSnapshotStore
{
    /// <summary>
    /// Reads the key's current group and its persisted balance together, without caching.
    /// Returns null when the key or its group does not exist.
    /// </summary>
    Task<VirtualKeySpendSnapshot?> GetSpendSnapshotAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default);
}
