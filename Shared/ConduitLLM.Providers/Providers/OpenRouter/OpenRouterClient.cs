using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.Configuration;
using ConduitLLM.Providers.Serialization;
using InternalModels = ConduitLLM.Providers.Common.Models;
using CoreUtils = ConduitLLM.Core.Utilities;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.OpenRouter
{
    /// <summary>
    /// Client for interacting with OpenRouter's OpenAI-compatible API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OpenRouter is a meta-provider that routes requests to various underlying LLM providers
    /// (OpenAI, Anthropic, Google, Meta, etc.) via a unified OpenAI-compatible API.
    /// This client extends OpenAICompatibleClient with OpenRouter-specific headers and error handling.
    /// </para>
    /// <para>
    /// Key features:
    /// - Access to 100+ models from multiple providers through a single API
    /// - Full OpenAI API compatibility for chat completions
    /// - Support for streaming and non-streaming responses
    /// - Tool/function calling support (dependent on routed model)
    /// - Vision support (dependent on routed model)
    /// - Model IDs use provider/model-name format (e.g., openai/gpt-4o)
    /// - Provider routing preferences via ExtensionData (provider object)
    /// </para>
    /// <para>
    /// OpenRouter-specific request parameters can be passed via ExtensionData:
    /// - "provider": object with routing preferences (order, ignore, only, data_collection, sort, etc.)
    /// - "transforms": string[] for prompt transformations (e.g., "middle-out")
    /// - "models": string[] with "route": "fallback" for multi-model fallback
    /// </para>
    /// </remarks>
    public partial class OpenRouterClient :
        ConduitLLM.Providers.OpenAICompatible.OpenAICompatibleClient,
        ConduitLLM.Core.Interfaces.IVideoGenerationClient,
        ConduitLLM.Core.Interfaces.IVideoProgressCallbackClient
    {
        private static ProviderErrorMessages OpenRouterErrorMessages =>
            ProviderConfigurationRegistry.GetErrorMessages(ProviderType.OpenRouter);

        // OpenRouter's recommended app-attribution headers. Configurable via env vars because the
        // static client-creator delegates have no access to IConfiguration.
        private static readonly string AttributionReferer =
            Environment.GetEnvironmentVariable("CONDUIT_OPENROUTER_HTTP_REFERER")
                ?? "https://github.com/nickna/Conduit";

        private static readonly string AttributionTitle =
            Environment.GetEnvironmentVariable("CONDUIT_OPENROUTER_X_TITLE") ?? "Conduit";

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenRouterClient"/> class.
        /// </summary>
        /// <param name="provider">The provider configuration.</param>
        /// <param name="keyCredential">The API key credential.</param>
        /// <param name="providerModelId">The model identifier to use (e.g., openai/gpt-4o, anthropic/claude-3.5-sonnet).</param>
        /// <param name="logger">The logger to use.</param>
        /// <param name="httpClientFactory">Optional HTTP client factory for advanced usage scenarios.</param>
        public OpenRouterClient(
            Provider provider,
            ProviderKeyCredential keyCredential,
            string providerModelId,
            ILogger logger,
            IHttpClientFactory? httpClientFactory = null,
            string? providerOptionsJson = null)
            : base(
                provider,
                keyCredential,
                providerModelId,
                logger,
                httpClientFactory,
                "OpenRouter",
                baseUrl: ProviderConfigurationRegistry.ResolveBaseUrl(provider))
        {
            _mappingOptions = ParseProviderOptions(providerOptionsJson);
        }

        /// <summary>
        /// Adds OpenRouter's recommended app-attribution headers (HTTP-Referer + X-Title) to every
        /// outgoing request so Conduit is identified in OpenRouter analytics/leaderboards.
        /// </summary>
        protected override Dictionary<string, string> CreateStandardHeaders(string? apiKey = null)
        {
            var headers = base.CreateStandardHeaders(apiKey);
            headers["HTTP-Referer"] = AttributionReferer;
            headers["X-Title"] = AttributionTitle;
            return headers;
        }

        /// <summary>
        /// Gets available models from OpenRouter's API.
        /// </summary>
        /// <remarks>
        /// <para>
        /// OpenRouter's /models endpoint is public and does not validate API keys.
        /// To ensure the key is valid, this method first calls GET /key which requires
        /// authentication and returns 401 for invalid keys.
        /// </para>
        /// <para>
        /// OpenRouter's /models response does not include the 'owned_by' field that the base
        /// OpenAI model data type requires. This override uses a permissive model type
        /// that only requires the 'id' field.
        /// </para>
        /// </remarks>
        public override async Task<List<InternalModels.ExtendedModelInfo>> GetModelsAsync(
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteApiRequestAsync(async () =>
            {
                using var client = CreateHttpClient(apiKey);
                var headers = CreateStandardHeaders(apiKey);

                // Validate the API key first via GET /key (the /models endpoint is public
                // and does not require authentication)
                await ValidateApiKeyAsync(client, headers, cancellationToken);

                var endpoint = GetModelsEndpoint();

                Logger.LogDebug("Getting available models from {Provider} at {Endpoint}", ProviderName, endpoint);

                var response = await CoreUtils.HttpClientHelper.GetJsonAsync<OpenRouterCatalogResponse>(
                    client,
                    endpoint,
                    ProvidersJsonContext.Default.OpenRouterCatalogResponse,
                    headers,
                    Logger,
                    cancellationToken);

                return response.Data
                    .Select(MapModelInfo)
                    .ToList();
            }, "GetModels", cancellationToken);
        }

        /// <summary>
        /// Projects an OpenRouter /models entry into an <see cref="InternalModels.ExtendedModelInfo"/>,
        /// deriving capabilities from <c>supported_parameters</c> + <c>architecture</c> modalities and
        /// token limits from <c>context_length</c> + <c>top_provider.max_completion_tokens</c>. All
        /// fields are nullable-tolerant so schema drift cannot break model listing.
        /// </summary>
        private InternalModels.ExtendedModelInfo MapModelInfo(OpenRouterCatalogModel m)
        {
            var info = InternalModels.ExtendedModelInfo.Create(m.Id, ProviderName, m.Id);

            if (!string.IsNullOrEmpty(m.Name))
            {
                info.WithName(m.Name);
            }

            var supported = m.SupportedParameters ?? new List<string>();
            var inputModalities = m.Architecture?.InputModalities ?? new List<string>();
            var outputModalities = m.Architecture?.OutputModalities ?? new List<string>();

            var toolsSupported = supported.Contains("tools", StringComparer.OrdinalIgnoreCase);

            info.WithCapabilities(new ConduitLLM.Configuration.DTOs.ModelCapabilitiesDto
            {
                CapabilitySource = ConduitLLM.Configuration.Models.ModelCapabilitySource.ProviderApi,
                InputModalities = ConduitLLM.Configuration.Models.ModelModalities.Normalize(inputModalities),
                OutputModalities = ConduitLLM.Configuration.Models.ModelModalities.Normalize(outputModalities),
                SupportsChat = true,
                SupportsFunctionCalling = toolsSupported,
                SupportsVision = inputModalities.Contains("image", StringComparer.OrdinalIgnoreCase),
                SupportsImageInput = inputModalities.Contains("image", StringComparer.OrdinalIgnoreCase),
                SupportsVideoInput = inputModalities.Contains("video", StringComparer.OrdinalIgnoreCase),
                SupportsAudioInput = inputModalities.Contains("audio", StringComparer.OrdinalIgnoreCase),
                SupportsFileInput = inputModalities.Contains("file", StringComparer.OrdinalIgnoreCase),
                SupportsVideoUnderstanding =
                    inputModalities.Contains("video", StringComparer.OrdinalIgnoreCase) &&
                    outputModalities.Contains("text", StringComparer.OrdinalIgnoreCase),
                SupportsImageGeneration = outputModalities.Contains("image", StringComparer.OrdinalIgnoreCase),
                SupportsVideoGeneration = outputModalities.Contains("video", StringComparer.OrdinalIgnoreCase)
            });

            if (m.ContextLength.HasValue || m.TopProvider?.MaxCompletionTokens != null)
            {
                info.WithTokenLimits(new InternalModels.ModelTokenLimits
                {
                    Context = m.ContextLength ?? m.TopProvider?.ContextLength,
                    Output = m.TopProvider?.MaxCompletionTokens
                });
            }

            return info;
        }

        /// <summary>
        /// Validates the API key by calling OpenRouter's GET /key endpoint.
        /// </summary>
        /// <remarks>
        /// Unlike the /models endpoint which is public, GET /key requires authentication
        /// and returns 401 for invalid keys.
        /// </remarks>
        private async Task ValidateApiKeyAsync(
            HttpClient client,
            Dictionary<string, string> headers,
            CancellationToken cancellationToken)
        {
            var keyEndpoint = $"{BaseUrl}/key";

            Logger.LogDebug("Validating API key via {Endpoint}", keyEndpoint);

            using var request = new HttpRequestMessage(HttpMethod.Get, keyEndpoint);
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new Core.Exceptions.LLMCommunicationException(
                    "Invalid API key for OpenRouter. Please verify your API key is correct.",
                    System.Net.HttpStatusCode.Unauthorized,
                    null);
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning(
                    "OpenRouter key validation returned {StatusCode}, proceeding with model listing",
                    response.StatusCode);
            }
        }

        /// <summary>
        /// Extracts enhanced error messages for OpenRouter-specific error patterns.
        /// Adds OpenRouter-specific keyword matching on top of base extraction.
        /// </summary>
        protected override string ExtractEnhancedErrorMessage(Exception ex)
        {
            var baseResult = base.ExtractEnhancedErrorMessage(ex);

            // If the base found something useful beyond the raw message, use it
            if (!string.IsNullOrEmpty(baseResult) &&
                !baseResult.Equals(ex.Message) &&
                !baseResult.Contains("Exception of type"))
            {
                return baseResult;
            }

            // OpenRouter-specific keyword matching
            var msg = ex.Message;

            if (msg.Contains("model not found", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                return OpenRouterErrorMessages.ModelNotFound;
            }

            if (msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
            {
                return OpenRouterErrorMessages.RateLimitExceeded;
            }

            if (msg.Contains("credit", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("insufficient", StringComparison.OrdinalIgnoreCase))
            {
                return "Insufficient OpenRouter credits. Add credits at openrouter.ai/credits.";
            }

            if (msg.Contains("no endpoints found", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("no provider", StringComparison.OrdinalIgnoreCase))
            {
                return "No OpenRouter provider available for this model. The model may be temporarily unavailable or routing constraints are too restrictive.";
            }

            if (msg.Contains("moderation", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("flagged", StringComparison.OrdinalIgnoreCase))
            {
                return "Request was flagged by OpenRouter content moderation.";
            }

            // Fallback: use base result with provider prefix
            return $"OpenRouter API error: {baseResult}";
        }
    }

}
