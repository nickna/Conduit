using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Providers;

/// <summary>
/// Shared scaffolding for HTTP-based function provider clients: base-URL resolution,
/// pooled HttpClient creation, and common header configuration. Authentication is
/// provider-specific and supplied via <see cref="ApplyAuthHeader"/>.
/// </summary>
public abstract class FunctionClientBase
{
    protected readonly FunctionConfiguration _configuration;
    protected readonly FunctionCredential _credential;
    protected readonly IHttpClientFactory? _httpClientFactory;
    protected readonly ILogger _logger;
    protected readonly string _baseUrl;
    protected readonly JsonSerializerOptions _jsonOptions;

    protected FunctionClientBase(
        FunctionConfiguration configuration,
        FunctionCredential credential,
        IHttpClientFactory? httpClientFactory,
        ILogger logger,
        string defaultBaseUrl)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Priority: Credential BaseUrl > Configuration BaseUrl > Default
        _baseUrl = !string.IsNullOrWhiteSpace(_credential.BaseUrl)
            ? _credential.BaseUrl.TrimEnd('/')
            : !string.IsNullOrWhiteSpace(_configuration.BaseUrl)
                ? _configuration.BaseUrl.TrimEnd('/')
                : defaultBaseUrl;

        _jsonOptions = Utilities.FunctionsJsonOptions.CompactWire;
    }

    /// <summary>The user-facing provider name, used for HttpClient naming and diagnostics.</summary>
    public abstract string ProviderName { get; }

    /// <summary>Adds the provider's authentication header for the given API key.</summary>
    protected abstract void ApplyAuthHeader(HttpClient client, string apiKey);

    /// <summary>
    /// Creates an HTTP client instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when IHttpClientFactory is not available.</exception>
    protected virtual HttpClient CreateHttpClient(string? apiKey = null)
    {
        if (_httpClientFactory == null)
        {
            throw new InvalidOperationException(
                $"IHttpClientFactory is required for {ProviderName} but was not injected. " +
                "Ensure IHttpClientFactory is registered in the dependency injection container. " +
                "Creating HttpClient instances directly can cause socket exhaustion under load.");
        }

        var client = _httpClientFactory.CreateClient($"{ProviderName}FunctionClient");
        ConfigureHttpClient(client, apiKey);
        return client;
    }

    /// <summary>
    /// Configures the HTTP client with headers and authentication.
    /// </summary>
    protected virtual void ConfigureHttpClient(HttpClient client, string? apiKey = null)
    {
        client.BaseAddress = new Uri(_baseUrl);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "ConduitLLM-Functions");

        var effectiveApiKey = apiKey ?? _credential.ApiKey;
        if (!string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            ApplyAuthHeader(client, effectiveApiKey);
        }

        // Default timeout (can be overridden by configuration)
        client.Timeout = TimeSpan.FromSeconds(_configuration.TimeoutSeconds ?? 30);
    }

    /// <summary>
    /// Verifies credentials with a lightweight provider-specific request while keeping
    /// transport, cancellation, and common status handling consistent.
    /// </summary>
    protected async Task<FunctionAuthenticationResult> VerifyViaProbeAsync<TRequest>(
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> jsonTypeInfo,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var effectiveApiKey = apiKey ?? _credential.ApiKey;

        if (string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            return FunctionAuthenticationResult.Failure(
                $"API key is required for {ProviderName}",
                "No API key provided in credential or parameter");
        }

        try
        {
            _logger.LogInformation("Verifying {Provider} authentication...", ProviderName);

            using var client = CreateHttpClient(apiKey);
            using var response = await client.PostAsJsonAsync(
                path,
                request,
                jsonTypeInfo,
                cancellationToken);

            stopwatch.Stop();
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "{Provider} authentication successful (Response: {StatusCode}, Time: {ElapsedMs}ms)",
                    ProviderName,
                    response.StatusCode,
                    stopwatch.ElapsedMilliseconds);

                return FunctionAuthenticationResult.Success(
                    $"Successfully authenticated with {ProviderName} API",
                    stopwatch.Elapsed.TotalMilliseconds);
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var providerResult = TranslateAuthenticationFailure(response.StatusCode, errorBody);
            if (providerResult is not null)
            {
                return providerResult;
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => FunctionAuthenticationResult.Failure(
                    "Authentication failed",
                    $"Invalid API key for {ProviderName}"),
                HttpStatusCode.BadRequest => FunctionAuthenticationResult.Failure(
                    "Authentication failed",
                    $"Invalid API key or account configuration for {ProviderName}. Verify the key and account are active."),
                HttpStatusCode.Forbidden => FunctionAuthenticationResult.Failure(
                    "Access forbidden",
                    $"API key does not have sufficient permissions for {ProviderName}"),
                _ => FunctionAuthenticationResult.Failure(
                    $"Unexpected response: {response.StatusCode}",
                    $"{ProviderName} API returned status {(int)response.StatusCode}. Response: {errorBody}")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "{Provider} authentication timed out after {ElapsedMs}ms",
                ProviderName,
                stopwatch.ElapsedMilliseconds);
            return FunctionAuthenticationResult.Failure(
                "Request timeout",
                "Authentication request timed out. Please try again or check your network connection.");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "Network error verifying {Provider} authentication: {Error}",
                ProviderName,
                ex.Message);
            return FunctionAuthenticationResult.Failure(
                $"Network error: {ex.Message}",
                $"Unable to connect to {ProviderName} API. Check the network connection and try again.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "{Provider} authentication failed with exception: {Error}",
                ProviderName,
                ex.Message);
            return FunctionAuthenticationResult.Failure(
                $"Authentication verification failed: {ex.Message}",
                ex.ToString());
        }
    }

    /// <summary>Allows a provider to translate additional authentication statuses.</summary>
    protected virtual FunctionAuthenticationResult? TranslateAuthenticationFailure(
        HttpStatusCode statusCode,
        string responseBody) =>
        null;

    /// <summary>Creates a mapped communication exception for a provider HTTP failure.</summary>
    protected FunctionCommunicationException HandleHttpError(
        HttpStatusCode statusCode,
        string? responseBody)
    {
        var message = GetHttpErrorMessage(statusCode, responseBody);
        _logger.LogError(
            "{Provider} API error: {StatusCode} - {Message}",
            ProviderName,
            statusCode,
            message);
        return new FunctionCommunicationException(
            ProviderName,
            message,
            statusCode,
            responseBody);
    }

    protected virtual string GetHttpErrorMessage(
        HttpStatusCode statusCode,
        string? responseBody) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized => $"Invalid API key for {ProviderName}",
            HttpStatusCode.Forbidden => "Access forbidden - check API key permissions",
            HttpStatusCode.TooManyRequests => $"Rate limit exceeded for {ProviderName} API",
            HttpStatusCode.BadRequest => $"Bad request to {ProviderName} API: {responseBody}",
            HttpStatusCode.ServiceUnavailable => $"{ProviderName} API is temporarily unavailable",
            HttpStatusCode.GatewayTimeout => $"{ProviderName} API request timed out",
            _ => $"{ProviderName} API error: {(int)statusCode} {statusCode}"
        };
}
