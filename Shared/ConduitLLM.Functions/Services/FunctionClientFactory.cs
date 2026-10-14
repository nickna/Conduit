using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Providers.Exa;
using ConduitLLM.Functions.Providers.Mcp;
using ConduitLLM.Functions.Providers.Tavily;
using ConduitLLM.Functions.Security;
using ConduitLLM.Persistence.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Services;

/// <summary>
/// Factory for creating function provider clients.
/// </summary>
/// <remarks>
/// This factory creates provider-specific client instances based on configuration.
/// Similar to LLMClientFactory, but for function providers.
/// </remarks>
public class FunctionClientFactory : IFunctionClientFactory
{
    private readonly IFunctionConfigurationRepository? _configurationRepository;
    private readonly IFunctionCredentialRepository? _credentialRepository;
    private readonly IFunctionExecutionConfigurationStore? _runtimeStore;
    private readonly IFunctionCredentialProtector _credentialProtector;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public FunctionClientFactory(
        IFunctionConfigurationRepository configurationRepository,
        IFunctionCredentialRepository credentialRepository,
        IFunctionCredentialProtector credentialProtector,
        ILoggerFactory loggerFactory,
        IHttpClientFactory? httpClientFactory = null)
    {
        _configurationRepository = configurationRepository ?? throw new ArgumentNullException(nameof(configurationRepository));
        _credentialRepository = credentialRepository ?? throw new ArgumentNullException(nameof(credentialRepository));
        _credentialProtector = credentialProtector ?? throw new ArgumentNullException(nameof(credentialProtector));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Creates provider clients using detached scalar runtime persistence.</summary>
    public FunctionClientFactory(
        IFunctionExecutionConfigurationStore runtimeStore,
        IFunctionCredentialProtector credentialProtector,
        ILoggerFactory loggerFactory,
        IHttpClientFactory? httpClientFactory = null)
    {
        _runtimeStore = runtimeStore ?? throw new ArgumentNullException(nameof(runtimeStore));
        _credentialProtector = credentialProtector ?? throw new ArgumentNullException(nameof(credentialProtector));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets a function client asynchronously.
    /// </summary>
    /// <param name="providerType">The type of function provider.</param>
    /// <param name="functionConfigurationId">The function configuration ID.</param>
    /// <returns>The function client for the specified provider.</returns>
    /// <exception cref="InvalidOperationException">Thrown when configuration or credentials are not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the provider type is not supported.</exception>
    public async Task<IFunctionClient> GetClientAsync(FunctionProviderType providerType, int functionConfigurationId)
    {
        var configuration = _runtimeStore is not null
            ? await _runtimeStore.GetConfigurationAsync(functionConfigurationId)
            : await _configurationRepository!.GetByIdAsync(functionConfigurationId);
        if (configuration == null)
        {
            throw new InvalidOperationException($"Function configuration {functionConfigurationId} not found");
        }

        // Get credentials for this provider type
        IReadOnlyList<FunctionCredential> credentials = _runtimeStore is not null
            ? await _runtimeStore.ListCredentialsForProviderAsync(configuration.ProviderType)
            : await _credentialRepository!.GetByProviderTypeAsync(configuration.ProviderType);

        var credential = CredentialSelector.SelectForConfiguration(credentials, functionConfigurationId);
        if (credential == null)
        {
            throw new InvalidOperationException(
                $"No enabled credentials found for provider type {configuration.ProviderType}");
        }

        // Reveal the (possibly encrypted) secret in-memory before handing it to the client. The
        // credential is a fresh, untracked read, so mutating it here does not persist.
        credential.ApiKey = _credentialProtector.Reveal(credential.ApiKey);

        // Create provider-specific client
        return providerType switch
        {
            FunctionProviderType.Exa => CreateExaClient(configuration, credential),
            FunctionProviderType.Tavily => CreateTavilyClient(configuration, credential),
            FunctionProviderType.Mcp => CreateMcpClient(configuration, credential),
            _ => throw new NotSupportedException($"Function provider type '{providerType}' is not supported")
        };
    }

    /// <summary>
    /// Creates an Exa client instance.
    /// </summary>
    private ExaClient CreateExaClient(
        FunctionConfiguration configuration,
        FunctionCredential credential)
    {
        var logger = _loggerFactory.CreateLogger<ExaClient>();

        return new ExaClient(
            configuration,
            credential,
            _httpClientFactory,
            logger);
    }

    /// <summary>
    /// Creates a Tavily client instance.
    /// </summary>
    private TavilyClient CreateTavilyClient(
        FunctionConfiguration configuration,
        FunctionCredential credential)
    {
        var logger = _loggerFactory.CreateLogger<TavilyClient>();

        return new TavilyClient(
            configuration,
            credential,
            _httpClientFactory,
            logger);
    }

    /// <summary>
    /// Creates an MCP (Model Context Protocol) client instance.
    /// </summary>
    private McpFunctionClient CreateMcpClient(
        FunctionConfiguration configuration,
        FunctionCredential credential)
    {
        return new McpFunctionClient(
            configuration,
            credential,
            _httpClientFactory,
            _loggerFactory);
    }
}
