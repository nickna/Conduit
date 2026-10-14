using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>
/// Fixed-shape reads for the authenticated function discovery endpoints.
/// </summary>
public interface IFunctionDiscoveryRuntimeStore
{
    /// <summary>
    /// Lists enabled configurations with optional exact enum filters. Ordering
    /// is unspecified, as in the existing endpoint query. Navigation collections
    /// are not loaded. Unknown persisted numeric enum values are preserved.
    /// </summary>
    Task<IReadOnlyList<FunctionConfiguration>> ListEnabledAsync(
        FunctionPurpose? purpose = null,
        FunctionProviderType? providerType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the scalar configuration for an enabled ID, or null when missing
    /// or disabled. Navigation collections are not loaded.
    /// </summary>
    Task<FunctionConfiguration?> GetEnabledByIdAsync(
        int id, CancellationToken cancellationToken = default);
}
