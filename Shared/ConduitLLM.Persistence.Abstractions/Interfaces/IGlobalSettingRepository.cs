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
    /// Creates a setting and returns its generated identifier.
    /// </summary>
    Task<int> CreateAsync(GlobalSetting setting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing setting.
    /// </summary>
    Task<bool> UpdateAsync(GlobalSetting setting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a setting by key. A null description preserves the
    /// existing description when the row already exists.
    /// </summary>
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
