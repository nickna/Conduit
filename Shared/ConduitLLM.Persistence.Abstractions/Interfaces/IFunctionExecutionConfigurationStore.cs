using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>Scalar configuration and credential reads used before provider execution.</summary>
public interface IFunctionExecutionConfigurationStore
{
    /// <summary>Reads a configuration, including disabled rows, without navigation graphs.</summary>
    Task<FunctionConfiguration?> GetConfigurationAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads distinct existing IDs, including disabled rows, with unspecified ordering.
    /// Null/empty input returns immediately, including when cancellation was already requested.
    /// Navigation collections are not loaded.
    /// </summary>
    Task<IReadOnlyList<FunctionConfiguration>> GetConfigurationsAsync(
        IReadOnlyList<int>? ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads all credentials for a provider, including disabled and other-configuration
    /// credentials, ordered by primary descending then key name ascending using database
    /// collation/null ordering. Secrets remain opaque stored values. Selection and decryption
    /// belong to the caller.
    /// </summary>
    Task<IReadOnlyList<FunctionCredential>> ListCredentialsForProviderAsync(
        FunctionProviderType providerType, CancellationToken cancellationToken = default);
}
