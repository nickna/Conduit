using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Core.Exceptions;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Utilities
{
    /// <summary>
    /// Helper class for common HTTP client operations used across the application.
    /// Provides standardized methods for request/response handling and error processing.
    /// </summary>
    /// <remarks>
    /// Debug diagnostics may emit header names for troubleshooting, but must never emit
    /// request or response header values because they can contain provider credentials.
    /// </remarks>
    public static class HttpClientHelper
    {
        private static readonly EventId JsonFailureDiagnostic = new(1702, "ProviderJsonFailureDiagnostic");

        /// <summary>
        /// Sends a request with JSON content and deserializes the response.
        /// </summary>
        /// <typeparam name="TRequest">The type of the request object to serialize.</typeparam>
        /// <typeparam name="TResponse">The type to deserialize the response into.</typeparam>
        /// <param name="client">The HttpClient to use for the request.</param>
        /// <param name="method">The HTTP method to use.</param>
        /// <param name="endpoint">The endpoint to send the request to.</param>
        /// <param name="requestData">The data to serialize and send.</param>
        /// <param name="requestTypeInfo">Source-generated metadata for the request.</param>
        /// <param name="responseTypeInfo">Source-generated metadata for the response.</param>
        /// <param name="headers">Optional additional headers to include with the request.</param>
        /// <param name="logger">Optional logger for request/response logging.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <param name="errorTranslator">Optional provider-specific HTTP error translator.</param>
        /// <returns>The deserialized response object.</returns>
        /// <exception cref="LLMCommunicationException">Thrown when there is an error communicating with the API.</exception>
        public static async Task<TResponse> SendJsonRequestAsync<TRequest, TResponse>(
            HttpClient client,
            HttpMethod method,
            string endpoint,
            TRequest requestData,
            JsonTypeInfo<TRequest> requestTypeInfo,
            JsonTypeInfo<TResponse> responseTypeInfo,
            IDictionary<string, string>? headers = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default,
            Func<HttpResponseMessage, string, Exception?>? errorTranslator = null)
        {
            var stage = "request-create";
            int? observedStatus = null;
            try
            {
                var request = CreateJsonRequest(
                    method,
                    endpoint,
                    requestData,
                    requestTypeInfo,
                    headers,
                    logger);
                LogRequestHeaderNames(request, logger);
                logger?.LogDebug("Sending {Method} request to {Endpoint}", method, endpoint);

                // The default completion option buffers the response body. A failure
                // here does not establish whether upstream headers were received.
                stage = "send-buffered";
                using var response = await client.SendAsync(request, cancellationToken);
                observedStatus = (int)response.StatusCode;
                stage = "response-processing";
                if (!response.IsSuccessStatusCode)
                    LogJsonFailure(logger, stage, "upstream-http-response", observedStatus);
                return await ProcessResponseAsync(
                    response,
                    responseTypeInfo,
                    logger,
                    cancellationToken,
                    errorTranslator,
                    recordNullJsonDiagnostic: true);
            }
            catch (HttpRequestException ex)
            {
                LogJsonFailure(logger, stage, "http-request", observedStatus, ex.HttpRequestError);
                logger?.LogError(ex, "HTTP request error communicating with API at {Endpoint}", endpoint);
                throw new LLMCommunicationException($"HTTP request error: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                LogJsonFailure(logger, stage, "caller-cancelled", observedStatus);
                logger?.LogWarning("Request to {Endpoint} was cancelled", endpoint);
                throw new LLMCommunicationException("Request was cancelled", ex);
            }
            catch (TaskCanceledException ex)
            {
                LogJsonFailure(logger, stage, "timeout", observedStatus);
                logger?.LogError(ex, "Request to {Endpoint} timed out", endpoint);
                throw new LLMCommunicationException("Request timed out", ex);
            }
            catch (JsonException ex)
            {
                LogJsonFailure(logger, stage, "invalid-json", observedStatus);
                logger?.LogError(ex, "JSON error processing response from {Endpoint}", endpoint);
                throw new LLMCommunicationException("Error processing response", ex);
            }
            catch (Exception ex) when (
                ex is not LLMCommunicationException &&
                ex is not ConfigurationException &&
                ex is not ModelNotFoundException &&
                ex is not ValidationException)
            {
                LogJsonFailure(logger, stage, "unexpected", observedStatus);
                logger?.LogError(ex, "Unexpected error during API communication with {Endpoint}", endpoint);
                throw new LLMCommunicationException($"Unexpected error: {ex.Message}", ex);
            }
        }

        // This event is independently extractable from the existing detailed logs.
        // Retain only closed codes and numeric/hex metadata, never log an exception,
        // request, response, endpoint, header, or provider-supplied text here.
        private static void LogJsonFailure(ILogger? logger, string stage, string category,
            int? observedStatus, HttpRequestError? httpRequestError = null)
        {
            var error = httpRequestError switch
            {
                null => "none",
                HttpRequestError.NameResolutionError => "name-resolution-error",
                HttpRequestError.ConnectionError => "connection-error",
                HttpRequestError.SecureConnectionError => "secure-connection-error",
                HttpRequestError.HttpProtocolError => "http-protocol-error",
                HttpRequestError.ExtendedConnectNotSupported => "extended-connect-not-supported",
                HttpRequestError.VersionNegotiationError => "version-negotiation-error",
                HttpRequestError.UserAuthenticationError => "user-authentication-error",
                HttpRequestError.ProxyTunnelError => "proxy-tunnel-error",
                HttpRequestError.InvalidResponse => "invalid-response",
                HttpRequestError.ResponseEnded => "response-ended",
                HttpRequestError.ConfigurationLimitExceeded => "configuration-limit-exceeded",
                _ => "unknown"
            };
            var traceId = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity
                ? activity.TraceId.ToHexString() : "none";
            logger?.LogWarning(JsonFailureDiagnostic,
                "Provider JSON failure: Stage={Stage} Category={Category} ObservedStatus={ObservedStatus} HttpRequestError={HttpRequestError} TraceId={TraceId}",
                stage, category, observedStatus, error, traceId);
        }

        /// <summary>
        /// Sends a GET request and deserializes the response with source-generated metadata.
        /// </summary>
        public static async Task<TResponse> GetJsonAsync<TResponse>(
            HttpClient client,
            string endpoint,
            JsonTypeInfo<TResponse> responseTypeInfo,
            IDictionary<string, string>? headers = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                LogRequestHeaderNames(request, logger);
                logger?.LogDebug("Sending GET request to {Endpoint}", endpoint);

                using var response = await client.SendAsync(request, cancellationToken);
                return await ProcessResponseAsync(
                    response,
                    responseTypeInfo,
                    logger,
                    cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                logger?.LogError(ex, "HTTP request error communicating with API at {Endpoint}", endpoint);
                throw new LLMCommunicationException($"HTTP request error: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                logger?.LogWarning("Request to {Endpoint} was cancelled", endpoint);
                throw new LLMCommunicationException("Request was cancelled", ex);
            }
            catch (TaskCanceledException ex)
            {
                logger?.LogError(ex, "Request to {Endpoint} timed out", endpoint);
                throw new LLMCommunicationException("Request timed out", ex);
            }
            catch (JsonException ex)
            {
                logger?.LogError(ex, "Failed to deserialize JSON response from {Endpoint}", endpoint);
                throw new LLMCommunicationException($"Failed to deserialize response: {ex.Message}", ex);
            }
            catch (Exception ex) when (
                ex is not LLMCommunicationException &&
                ex is not ConfigurationException &&
                ex is not ModelNotFoundException &&
                ex is not ValidationException)
            {
                logger?.LogError(ex, "Unexpected error during API communication with {Endpoint}", endpoint);
                throw new LLMCommunicationException($"Unexpected error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Creates an HTTP request with JSON content and headers.
        /// </summary>
        private static HttpRequestMessage CreateJsonRequest<TRequest>(
            HttpMethod method,
            string endpoint,
            TRequest requestData,
            JsonTypeInfo<TRequest> requestTypeInfo,
            IDictionary<string, string>? headers,
            ILogger? logger = null)
        {
            var request = new HttpRequestMessage(method, endpoint);

            // Add content if data is provided
            if (requestData != null)
            {
                var requestJson = JsonSerializer.Serialize(requestData, requestTypeInfo);
                logger?.LogDebug("Prepared JSON request body ({BodyLength} bytes)", Encoding.UTF8.GetByteCount(requestJson));
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            }

            // Add headers
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (headers != null)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return request;
        }

        /// <summary>
        /// Logs request header names without emitting any header values.
        /// </summary>
        private static void LogRequestHeaderNames(HttpRequestMessage request, ILogger? logger)
        {
            if (logger?.IsEnabled(LogLevel.Debug) != true)
            {
                return;
            }

            var headerNames = request.Headers
                .Select(header => header.Key)
                .Concat(request.Content?.Headers.Select(header => header.Key) ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

            logger.LogDebug("Request header names: {HeaderNames}", string.Join("; ", headerNames));
        }

        private static async Task<TResponse> ProcessResponseAsync<TResponse>(
            HttpResponseMessage response,
            JsonTypeInfo<TResponse> responseTypeInfo,
            ILogger? logger,
            CancellationToken cancellationToken,
            Func<HttpResponseMessage, string, Exception?>? errorTranslator = null,
            bool recordNullJsonDiagnostic = false)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await ReadErrorContentAsync(response, cancellationToken);
                logger?.LogError("API error: {StatusCode} - {Content}", response.StatusCode, errorContent);

                // Preserve the existing provider-specific diagnostic used by the options-based path.
                if (errorContent.Contains("\"message\": null") &&
                    errorContent.Contains("image_generation_user_error"))
                {
                    logger?.LogWarning(
                        "Detected possible OpenAI quota/billing issue - image generation errors with null messages often indicate insufficient quota");
                    throw new LLMCommunicationException(
                        $"API returned an error: {(int)response.StatusCode} {response.StatusCode} - Possible quota/billing issue. Please check your provider account status.",
                        response.StatusCode,
                        errorContent,
                        null);
                }

                var translatedError = errorTranslator?.Invoke(response, errorContent);
                if (translatedError != null)
                {
                    throw translatedError;
                }

                throw new LLMCommunicationException(
                    $"API returned an error: {(int)response.StatusCode} {response.StatusCode} - {SensitiveDataRedactor.Redact(errorContent)}",
                    response.StatusCode,
                    errorContent,
                    null);
            }

            logger?.LogDebug("Received successful response with status code {StatusCode}", response.StatusCode);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (logger?.IsEnabled(LogLevel.Debug) == true)
            {
                var safeContent = SensitiveDataRedactor.Redact(responseContent);
                var preview = safeContent.Length > 500 ? safeContent[..500] + "..." : safeContent;
                logger.LogDebug("Response content preview: {Content}", preview);
            }

            try
            {
                var result = JsonSerializer.Deserialize(responseContent, responseTypeInfo);
                if (result is null)
                {
                    if (recordNullJsonDiagnostic)
                        LogJsonFailure(logger, "response-processing", "null-json-result", (int)response.StatusCode);
                    throw new LLMCommunicationException("Failed to deserialize response - result was null");
                }
                return result;
            }
            catch (JsonException ex)
            {
                logger?.LogError(
                    ex,
                    "Failed to deserialize response. Redacted content: {Content}",
                    SensitiveDataRedactor.Redact(responseContent));
                throw;
            }
        }

        /// <summary>
        /// Reads error content from an HTTP response.
        /// </summary>
        public static async Task<string> ReadErrorContentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                if (response.Content == null)
                {
                    return "No content";
                }

                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception)
            {
                return "Could not read error content";
            }
        }

        /// <summary>
        /// Sends a streaming request and returns the response for processing.
        /// </summary>
        /// <param name="client">The HttpClient to use for the request.</param>
        /// <param name="method">The HTTP method to use.</param>
        /// <param name="endpoint">The endpoint to send the request to.</param>
        /// <param name="requestData">The data to serialize and send.</param>
        /// <param name="requestTypeInfo">Source-generated metadata for the request.</param>
        /// <param name="headers">Optional additional headers to include with the request.</param>
        /// <param name="logger">Optional logger for request/response logging.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <param name="errorTranslator">Optional provider-specific HTTP error translator.</param>
        /// <returns>The HttpResponseMessage for further processing.</returns>
        /// <exception cref="LLMCommunicationException">Thrown when there is an error communicating with the API.</exception>
        public static async Task<HttpResponseMessage> SendStreamingRequestAsync<TRequest>(
            HttpClient client,
            HttpMethod method,
            string endpoint,
            TRequest requestData,
            JsonTypeInfo<TRequest> requestTypeInfo,
            IDictionary<string, string>? headers = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default,
            Func<HttpResponseMessage, string, Exception?>? errorTranslator = null)
        {
            try
            {
                var request = CreateJsonRequest(
                    method,
                    endpoint,
                    requestData,
                    requestTypeInfo,
                    headers,
                    logger);
                
                // Add Accept header for SSE if not already present
                if (!request.Headers.Accept.Any(h => h.MediaType == "text/event-stream"))
                {
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
                }

                LogRequestHeaderNames(request, logger);
                logger?.LogDebug("Sending streaming {Method} request to {Endpoint}", method, endpoint);

                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await ReadErrorContentAsync(response, cancellationToken);
                    logger?.LogError("API streaming error: {StatusCode} - {Content}", response.StatusCode,
                        SensitiveDataRedactor.Redact(errorContent));

                    var translatedError = errorTranslator?.Invoke(response, errorContent);
                    if (translatedError != null)
                    {
                        throw translatedError;
                    }

                    throw new LLMCommunicationException(
                        $"API returned an error: {(int)response.StatusCode} {response.StatusCode} - {SensitiveDataRedactor.Redact(errorContent)}",
                        response.StatusCode,
                        errorContent,
                        null);
                }

                logger?.LogDebug("Received successful streaming response with status code {StatusCode}", response.StatusCode);
                return response;
            }
            catch (HttpRequestException ex)
            {
                logger?.LogError(ex, "HTTP request error communicating with streaming API at {Endpoint}", endpoint);
                throw new LLMCommunicationException($"HTTP request error during streaming: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                logger?.LogWarning("Streaming request to {Endpoint} was cancelled", endpoint);
                throw new LLMCommunicationException("Streaming request was cancelled", ex);
            }
            catch (TaskCanceledException ex)
            {
                logger?.LogError(ex, "Streaming request to {Endpoint} timed out", endpoint);
                throw new LLMCommunicationException("Streaming request timed out", ex);
            }
            catch (Exception ex) when (
                ex is not LLMCommunicationException &&
                ex is not ConfigurationException &&
                ex is not ModelNotFoundException &&
                ex is not ValidationException)
            {
                logger?.LogError(ex, "Unexpected error during streaming API communication with {Endpoint}", endpoint);
                throw new LLMCommunicationException($"Unexpected streaming error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Adds standard authentication headers to a request based on the authentication type.
        /// </summary>
        /// <param name="headers">The headers dictionary to add authentication to.</param>
        /// <param name="authType">The type of authentication (Bearer, ApiKey, etc.).</param>
        /// <param name="authValue">The authentication value (token, key, etc.).</param>
        public static void AddAuthenticationHeader(
            IDictionary<string, string> headers,
            string authType,
            string authValue)
        {
            if (string.IsNullOrEmpty(authValue))
            {
                throw new ArgumentException("Authentication value cannot be empty", nameof(authValue));
            }

            switch (authType.ToLowerInvariant())
            {
                case "bearer":
                    headers["Authorization"] = $"Bearer {authValue}";
                    break;

                case "apikey":
                case "api-key":
                    headers["x-api-key"] = authValue;
                    break;

                case "basic":
                    var encodedAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes(authValue));
                    headers["Authorization"] = $"Basic {encodedAuth}";
                    break;

                default:
                    // For custom header auth
                    headers[authType] = authValue;
                    break;
            }
        }
    }
}
