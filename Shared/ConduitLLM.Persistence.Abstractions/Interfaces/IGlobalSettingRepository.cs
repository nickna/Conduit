using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Configuration.Interfaces;

/// <summary>
/// Backend-neutral persistence operations for global settings.
/// </summary>
/// <remarks>
/// This interface deliberately lists complete operations instead of inheriting
/// generic query composition. Implementations must not expose IQueryable or
/// expression-tree parameters across this boundary.
/// </remarks>
public interface IGlobalSettingRepository
{
    /// <summary>
    /// Lists all settings ordered by key.
    /// </summary>
    Task<IReadOnlyList<GlobalSetting>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a setting by its primary key.
    /// </summary>
    Task<GlobalSetting?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a setting by its unique key.
    /// </summary>
    Task<GlobalSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a setting and returns its identifier.
    /// </summary>
    /// <remarks>
    /// Validation precedes cancellation. Before changing caller timestamps, the
    /// repository completes its preparation and checks cancellation; EF performs
    /// that check after acquiring its context. A token already cancelled at this
    /// boundary leaves the caller unchanged. Otherwise one client UTC instant
    /// supplies UpdatedAt and, when unset, CreatedAt, before the write acquires a
    /// database connection. Later cancellation or write failure does not restore
    /// those fields. A supplied CreatedAt is preserved.
    /// A zero Id requests a generated identifier; every nonzero Id, including
    /// negative values, is inserted unchanged. The caller's Id is assigned after
    /// receiving the successful insert result and otherwise retains its value.
    /// </remarks>
    Task<int> CreateAsync(GlobalSetting setting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a setting by its nonzero identifier, or inserts one when Id is zero.
    /// </summary>
    /// <remarks>
    /// Uses the same cancellation and caller-mutation boundary as CreateAsync.
    /// UpdatedAt is sampled from the client UTC clock before database connection
    /// acquisition; CreatedAt is preserved. Later cancellation or write failure
    /// does not restore the caller's fields.
    /// A zero Id inserts and receives a generated identifier, while preserving
    /// CreatedAt even when it is the default value. A missing nonzero Id causes
    /// a concurrency failure: EF uses DbUpdateConcurrencyException and the typed
    /// adapter uses System.Data.DBConcurrencyException. No replacement is inserted.
    /// </remarks>
    Task<bool> UpdateAsync(GlobalSetting setting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a setting by key. A null description preserves the
    /// existing description when the row already exists.
    /// </summary>
    /// <remarks>
    /// Reads by key, then samples one client UTC instant for the write. The read
    /// and write are separate statements without a transaction spanning both.
    /// An absent row is inserted with equal CreatedAt and UpdatedAt; a concurrent
    /// insert can cause a unique-key failure. An existing row is updated by its
    /// original primary key, writing only fields changed from the read snapshot.
    /// A disappearing original row causes a concurrency failure: EF reports
    /// DbUpdateConcurrencyException, while the typed adapter reports the BCL
    /// System.Data.DBConcurrencyException. Provider-specific database exception
    /// wrappers are retained. Returns false when the snapshot has no changes,
    /// including an identical newly sampled timestamp.
    /// </remarks>
    Task<bool> UpsertAsync(
        string key,
        string value,
        string? description = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a setting by its primary key.
    /// </summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a setting by its unique key.
    /// </summary>
    Task<bool> DeleteByKeyAsync(string key, CancellationToken cancellationToken = default);
}
