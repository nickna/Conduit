using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Configuration.Interfaces;

/// <summary>
/// Optional request-time read of a fresh, complete provider selection snapshot.
/// </summary>
public interface IProviderRuntimeSelectionReader
{
    /// <summary>
    /// Reads the provider and all its credentials, including disabled credentials,
    /// from one current persistence snapshot. Each call returns an owned graph;
    /// an earlier mapping or cached provider graph must not satisfy this read.
    /// </summary>
    Task<Provider?> GetByIdAsync(int providerId, CancellationToken cancellationToken = default);
}
