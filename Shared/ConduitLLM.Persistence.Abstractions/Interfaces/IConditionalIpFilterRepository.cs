using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Configuration.Interfaces;

/// <summary>IP-filter mutations that validate the locked resource snapshot before writing.</summary>
public interface IConditionalIpFilterRepository
{
    /// <summary>
    /// Locks the row, validates its version, and applies changes once in the same transaction.
    /// The callback returns false for a no-op. Returns the persisted snapshot, or null if missing.
    /// Callbacks only inspect or change the materialized row; external effects run after commit.
    /// </summary>
    Task<IpFilterEntity?> UpdateConditionalAsync(int id, Action<IpFilterEntity> validateVersion,
        Func<IpFilterEntity, bool> applyChanges, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the row and validates its version before deletion in the same transaction.
    /// Returns the deleted snapshot, or null if missing. Validation failures leave the row unchanged.
    /// </summary>
    Task<IpFilterEntity?> DeleteConditionalAsync(int id, Action<IpFilterEntity> validateVersion,
        CancellationToken cancellationToken = default);
}
