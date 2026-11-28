using ConduitLLM.Core.Exceptions; // Need this for exception documentation

namespace ConduitLLM.Core.Interfaces;

/// <summary>
/// Defines the contract for a factory that creates instances of ILLMClient based on configuration.
/// </summary>
public interface ILLMClientFactory
{
    /// <summary>
    /// Asynchronously gets an appropriate ILLMClient instance for the specified model alias.
    /// </summary>
    /// <param name="modelAlias">The model alias specified in the request (e.g., "gpt-4-turbo").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An instance of ILLMClient capable of handling the request for the specified model.</returns>
    /// <exception cref="ConfigurationException">Thrown if the configuration for the model alias or its provider is invalid or missing.</exception>
    /// <exception cref="UnsupportedProviderException">Thrown if the provider specified in the configuration is not supported by this factory.</exception>
    Task<ILLMClient> GetClientAsync(string modelAlias, CancellationToken cancellationToken = default);

    /// <summary>Creates a client for the model mapping already resolved for this request.</summary>
    Task<ILLMClient> GetClientForMappingAsync(
        string modelAlias,
        ConduitLLM.Configuration.Entities.ModelProviderMapping mapping,
        CancellationToken cancellationToken = default) => GetClientAsync(modelAlias, cancellationToken);

    /// <summary>Gets a provider client using chat-only provider-aware routing.</summary>
    Task<ILLMClient> GetClientForChatAsync(
        ConduitLLM.Core.Models.ChatCompletionRequest request,
        CancellationToken cancellationToken = default) => GetClientAsync(request.Model, cancellationToken);

    /// <summary>
    /// Asynchronously gets an ILLMClient instance for the specified provider ID directly.
    /// </summary>
    /// <param name="providerId">The ID of the provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An instance of ILLMClient for the specified provider.</returns>
    /// <exception cref="ConfigurationException">Thrown if the configuration for the provider is invalid or missing.</exception>
    /// <exception cref="UnsupportedProviderException">Thrown if the specified provider is not supported by this factory.</exception>
    Task<ILLMClient> GetClientByProviderIdAsync(int providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously gets an ILLMClient instance for the specified provider ID, configured with the
    /// given provider model id. Use this when a model mapping has already been resolved — the
    /// provider model id is not a model alias and must not go through alias resolution again.
    /// </summary>
    /// <param name="providerId">The ID of the provider.</param>
    /// <param name="providerModelId">The provider's model identifier (e.g. the mapping's ProviderModelId).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An instance of ILLMClient for the specified provider and model.</returns>
    /// <exception cref="ConfigurationException">Thrown if the configuration for the provider is invalid or missing.</exception>
    /// <exception cref="UnsupportedProviderException">Thrown if the specified provider is not supported by this factory.</exception>
    Task<ILLMClient> GetClientByProviderIdAsync(int providerId, string providerModelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously gets an ILLMClient instance for the specified provider type directly.
    /// </summary>
    /// <param name="providerType">The provider type enum value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An instance of ILLMClient for the specified provider type.</returns>
    /// <exception cref="ConfigurationException">Thrown if the configuration for the provider is invalid or missing.</exception>
    /// <exception cref="UnsupportedProviderException">Thrown if the specified provider type is not supported by this factory.</exception>
    Task<ILLMClient> GetClientByProviderTypeAsync(ConduitLLM.Configuration.ProviderType providerType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a lightweight ILLMClient instance for testing provider credentials.
    /// This method bypasses the normal configuration lookup and creates a client directly with the provided provider and key.
    /// </summary>
    /// <param name="provider">The provider entity with configuration.</param>
    /// <param name="keyCredential">The key credential to test.</param>
    /// <returns>An instance of ILLMClient configured with the test credentials.</returns>
    /// <exception cref="UnsupportedProviderException">Thrown if the specified provider type is not supported by this factory.</exception>
    /// <remarks>
    /// This method is specifically designed for credential validation and should not be used for normal operations.
    /// The created client is configured with minimal settings suitable for authentication testing only.
    /// </remarks>
    ILLMClient CreateTestClient(ConduitLLM.Configuration.Entities.Provider provider, ConduitLLM.Configuration.Entities.ProviderKeyCredential keyCredential);
}
