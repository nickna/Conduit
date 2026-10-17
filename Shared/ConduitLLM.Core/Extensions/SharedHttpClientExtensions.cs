using ConduitLLM.Core.Policies;
using ConduitLLM.Core.Services;

using Microsoft.Extensions.DependencyInjection;

using Polly;

namespace ConduitLLM.Core.Extensions;

/// <summary>
/// Shared HTTP client registrations used by both Gateway and Admin services.
/// Centralizes configuration to avoid duplication and ensure consistent behavior.
/// </summary>
public static class SharedHttpClientExtensions
{
    /// <summary>
    /// Registers HTTP clients shared between Gateway and Admin: DiscoveryProviders,
    /// ImageDownload (ExternalImageFetch), Exa, and Tavily function providers.
    /// Also registers <see cref="IImageDownloadService"/> for DI-friendly image downloading.
    /// </summary>
    public static IServiceCollection AddSharedHttpClients(this IServiceCollection services)
    {
        // Configure HttpClient for discovery providers
        services.AddHttpClient("DiscoveryProviders", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Conduit-LLM/1.0");
        });

        // Register HTTP client for external image fetching (used by IImageDownloadService)
        services.AddHttpClient(ImageDownloadService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Conduit-LLM/1.0");
            client.DefaultRequestHeaders.Add("Accept", "image/*");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 20,
            EnableMultipleHttp2Connections = true
        });

        // Register IImageDownloadService for DI-friendly image downloading
        services.AddScoped<Interfaces.IImageDownloadService, ImageDownloadService>();

        // Register HTTP clients for function providers (Exa and Tavily)
        AddFunctionProviderHttpClient(services, "ExaFunctionClient");
        AddFunctionProviderHttpClient(services, "TavilyFunctionClient");
        // A durable claim permits one dispatch. A lost response must not cause a
        // transport policy to repeat a possibly billable provider operation.
        AddFunctionProviderHttpClient(services, "ExaFunctionClient.SingleDispatch", retry: false);
        AddFunctionProviderHttpClient(services, "TavilyFunctionClient.SingleDispatch", retry: false);

        // MCP uses Streamable HTTP / SSE: it manages its own Accept negotiation, keeps long-lived
        // streams open, and must not be wrapped in a request-level retry policy, so it gets a
        // dedicated client distinct from the fixed-Accept, 30s-timeout clients above.
        AddMcpFunctionHttpClient(services, "MCPFunctionClient");

        return services;
    }

    /// <summary>
    /// Registers the HTTP client used by the MCP function provider. No default Accept header (the
    /// MCP transport negotiates application/json vs text/event-stream itself), no request timeout
    /// (SSE streams are long-lived; connect timeouts are governed by the transport), and no retry
    /// policy (MCP sessions are stateful).
    /// </summary>
    private static void AddMcpFunctionHttpClient(IServiceCollection services, string clientName)
    {
        services.AddHttpClient(clientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Add("User-Agent", "ConduitLLM-Functions-MCP");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true
        });
    }

    /// <summary>
    /// Registers an HTTP client for a function provider with standard configuration.
    /// </summary>
    private static void AddFunctionProviderHttpClient(IServiceCollection services, string clientName, bool retry = true)
    {
        var builder = services.AddHttpClient(clientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "ConduitLLM-Functions");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true
        });
        if (retry)
        {
            var safeMethodRetry = HttpRetryPolicies.GetStandardRetryPolicy();
            var singleAttempt = Policy.NoOpAsync<HttpResponseMessage>();
            // Exa/Tavily POSTs, including authentication probes, can consume credits.
            // Without provider-supported idempotency, a lost response is not safe to replay.
            builder.AddPolicyHandler(request =>
                request.Method == HttpMethod.Get || request.Method == HttpMethod.Head || request.Method == HttpMethod.Options
                    ? safeMethodRetry
                    : singleAttempt);
        }
    }
}
