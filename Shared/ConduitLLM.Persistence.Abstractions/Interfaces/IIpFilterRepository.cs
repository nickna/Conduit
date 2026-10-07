using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Configuration.Interfaces;

/// <summary>
/// Complete persistence operations required by IP-filter administration and
/// request enforcement.
/// </summary>
/// <remarks>
/// The contract exposes materialized values and fixed operations so EF Core and
/// NativeAOT-compatible backends can implement identical behavior.
/// </remarks>
public interface IIpFilterRepository
{
    /// <summary>
    /// Lists all filters ordered by filter type and IP address.
    /// </summary>
    Task<IReadOnlyList<IpFilterEntity>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a filter by identifier.
    /// </summary>
    Task<IpFilterEntity?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets enabled global filters ordered by filter type and IP address.
    /// </summary>
    Task<IReadOnlyList<IpFilterEntity>> GetEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets enabled virtual-key filters ordered by key, type, and IP address.
    /// </summary>
    Task<IReadOnlyList<IpFilterEntity>> GetEnabledPerKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all filters scoped to one virtual key, ordered by type and IP address.
    /// </summary>
    Task<IReadOnlyList<IpFilterEntity>> GetByVirtualKeyIdAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a filter and returns it with its generated identifier.
    /// </summary>
    Task<IpFilterEntity> AddAsync(
        IpFilterEntity filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a filter using its row-version concurrency token.
    /// </summary>
    Task<bool> UpdateAsync(
        IpFilterEntity filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a filter by identifier.
    /// </summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
