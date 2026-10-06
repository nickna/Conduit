using System.Text;
using System.Text.Json;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Providers.Common.Models;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.MiniMax
{
    /// <summary>
    /// MiniMaxClient partial class containing models and authentication functionality.
    /// </summary>
    public partial class MiniMaxClient
    {
        /// <inheritdoc />
        public override Task<List<ExtendedModelInfo>> GetModelsAsync(
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            Logger.LogWarning("MiniMax does not provide a models listing endpoint");
            throw new NotSupportedException(
                "MiniMax does not provide a models listing endpoint. " +
                "Model availability must be confirmed through MiniMax's documentation. " +
                "Configure specific model IDs directly in your application settings.");
        }

        /// <summary>
        /// Verifies MiniMax authentication by making a minimal API call.
        /// </summary>
        public override async Task<AuthenticationResult> VerifyAuthenticationAsync(
            string? apiKey = null, 
            string? baseUrl = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                var effectiveApiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : PrimaryKeyCredential.ApiKey;
                var effectiveBaseUrl = !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl.TrimEnd('/') : _baseUrl;
                
                if (string.IsNullOrWhiteSpace(effectiveApiKey))
                {
                    return AuthenticationResult.Failure(
                        "API key is required",
                        "No API key provided for MiniMax authentication");
                }

                // Create a test HTTP client
                using var httpClient = CreateHttpClient(effectiveApiKey);
                
                // Make a minimal chat completion request with max_tokens=1 to minimize cost
                var testUrl = $"{effectiveBaseUrl}/v1/chat/completions";
                var testRequest = new MiniMaxChatCompletionRequest
                {
                    Model = "abab6.5-chat",  // Use the actual MiniMax model name
                    Messages =
                    {
                        new MiniMaxMessage { Role = "user", Content = "Hi" }
                    },
                    MaxTokens = 1,
                    Stream = false
                };
                
                var json = JsonSerializer.Serialize(
                    testRequest,
                    MiniMaxStreamJsonContext.Default.MiniMaxChatCompletionRequest);
                using var testContent = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync(testUrl, testContent, cancellationToken);
                var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    
                    // MiniMax returns base_resp with status_code for errors
                    // Parse the response to check for MiniMax-specific error structure
                    try
                    {
                        Logger.LogInformation("MiniMax auth test response: {Response}", content);
                        
                        using var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.TryGetProperty("base_resp", out var baseResp))
                        {
                            if (baseResp.TryGetProperty("status_code", out var statusCode) && 
                                statusCode.GetInt32() != 0)
                            {
                                var statusMsg = baseResp.TryGetProperty("status_msg", out var msg) 
                                    ? msg.GetString() : "Unknown error";
                                Logger.LogWarning("MiniMax authentication failed with code {Code}: {Message}", 
                                    statusCode.GetInt32(), statusMsg);
                                return AuthenticationResult.Failure(
                                    "Authentication failed",
                                    statusMsg ?? "Invalid API key");
                            }
                        }
                        
                        // If we have choices, it's a successful response
                        if (doc.RootElement.TryGetProperty("choices", out _))
                        {
                            return AuthenticationResult.Success(
                                "Authentication successful",
                                responseTime);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to parse MiniMax response: {Response}", content);
                        // If we can't parse the response, treat it as an error
                    }
                    
                    return AuthenticationResult.Failure(
                        "Invalid response",
                        "Unexpected response format from MiniMax API");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return AuthenticationResult.Failure(
                        "Invalid API key",
                        "The provided API key was rejected by MiniMax");
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    return AuthenticationResult.Failure(
                        $"Authentication failed with status {response.StatusCode}",
                        content);
                }
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError(ex, "Network error during MiniMax authentication verification");
                return AuthenticationResult.Failure(
                    "Network error",
                    $"Could not connect to MiniMax API: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                return AuthenticationResult.Failure(
                    "Request timeout",
                    "The authentication request timed out");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unexpected error during MiniMax authentication verification");
                return AuthenticationResult.Failure(
                    "Authentication verification failed",
                    ex.Message);
            }
        }
    }
}
