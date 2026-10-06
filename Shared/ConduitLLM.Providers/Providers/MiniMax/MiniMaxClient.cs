using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Providers.Configuration;
using ConduitLLM.Providers.Serialization;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.MiniMax
{
    /// <summary>
    /// Client for interacting with MiniMax AI APIs.
    /// </summary>
    public partial class MiniMaxClient :
        BaseLLMClient,
        IVideoGenerationClient,
        IVideoProgressCallbackClient,
        IAuthenticationVerifiable
    {
        private readonly string _baseUrl;
        private Func<string, string, int, Task>? _progressCallback;

        /// <summary>
        /// MiniMax chat API returns snake_case properties — needs case-insensitive deserialization.
        /// </summary>
        private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                MiniMaxStreamJsonContext.Default,
                ProvidersJsonContext.Default,
                ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default,
                ConduitLLM.Core.Serialization.AsyncTaskJsonContext.Default)
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="MiniMaxClient"/> class.
        /// </summary>
        /// <param name="credentials">The provider credentials containing API key and endpoint.</param>
        /// <param name="modelId">The default model ID to use.</param>
        /// <param name="logger">The logger for diagnostic information.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        public MiniMaxClient(
            Provider provider,
            ProviderKeyCredential keyCredential,
            string modelId,
            ILogger<MiniMaxClient> logger,
            IHttpClientFactory httpClientFactory)
            : base(provider, keyCredential, modelId, logger, httpClientFactory, "minimax")
        {
            _baseUrl = ProviderConfigurationRegistry.ResolveBaseUrl(provider);
            logger.LogInformation("MiniMax client initialized with base URL: {BaseUrl}, Model: {Model}", _baseUrl, modelId);
        }

        /// <inheritdoc />
        protected override void ConfigureHttpClient(HttpClient client, string apiKey)
        {
            base.ConfigureHttpClient(client, apiKey);
            // Override Accept header for SSE streaming (base sets application/json)
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
            // Use video generation timeout since MiniMax supports video
            client.Timeout = VideoGenerationTimeout;
        }

        /// <summary>
        /// Sets a progress callback for long-running operations like video generation.
        /// </summary>
        /// <param name="callback">The callback function that receives status updates.</param>
        public void SetProgressCallback(Func<string, string, int, Task> callback)
        {
            _progressCallback = callback;
        }

        private Task<TResponse> SendMiniMaxJsonAsync<TRequest, TResponse>(
            HttpClient client,
            string endpoint,
            TRequest request,
            JsonSerializerOptions jsonOptions,
            CancellationToken cancellationToken)
        {
            return Core.Utilities.HttpClientHelper.SendJsonRequestAsync<TRequest, TResponse>(
                client,
                HttpMethod.Post,
                endpoint,
                request,
                GetRequiredJsonTypeInfo<TRequest>(jsonOptions),
                GetRequiredJsonTypeInfo<TResponse>(jsonOptions),
                headers: null,
                Logger,
                cancellationToken);
        }

        /// <inheritdoc/>
        protected override string GetDefaultBaseUrl()
        {
            return ProviderConfigurationRegistry.GetDefaultBaseUrl(ProviderType.MiniMax)!;
        }
    }
}
