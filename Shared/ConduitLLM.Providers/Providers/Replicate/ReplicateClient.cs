using System.Net.Http.Headers;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.Authentication;
using ConduitLLM.Providers.Configuration;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.Replicate
{
    /// <summary>
    /// Revised client for interacting with Replicate APIs using the new client hierarchy.
    /// Handles the asynchronous prediction workflow (start, poll, get result) for various model providers.
    /// </summary>
    public partial class ReplicateClient : BaseLLMClient, IVideoGenerationClient
    {
        // Default polling configuration
        private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxPollingDuration = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Base URL for the Replicate API (e.g. "https://api.replicate.com/v1").
        /// </summary>
        protected readonly string BaseUrl;

        /// <summary>
        /// Gets the Token authentication strategy for Replicate.
        /// Replicate uses "Token" scheme instead of "Bearer".
        /// </summary>
        protected override IAuthenticationStrategy AuthenticationStrategy => TokenStrategy.Instance;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReplicateClient"/> class.
        /// </summary>
        /// <param name="provider">The provider configuration.</param>
        /// <param name="keyCredential">The API key credential.</param>
        /// <param name="providerModelId">The model identifier to use (typically a version hash or full slug).</param>
        /// <param name="logger">The logger to use.</param>
        /// <param name="httpClientFactory">The HTTP client factory for creating HttpClient instances.</param>
        public ReplicateClient(
            Provider provider,
            ProviderKeyCredential keyCredential,
            string providerModelId,
            ILogger logger,
            IHttpClientFactory? httpClientFactory = null)
            : base(
                provider,
                keyCredential,
                providerModelId,
                logger,
                httpClientFactory,
                "Replicate")
        {
            BaseUrl = ProviderConfigurationRegistry.ResolveBaseUrl(provider);
        }

        /// <inheritdoc/>
        protected override void ValidateCredentials()
        {
            base.ValidateCredentials();

            if (string.IsNullOrWhiteSpace(PrimaryKeyCredential.ApiKey))
            {
                throw new ConfigurationException($"API key is missing for provider '{ProviderName}'.");
            }
        }

        /// <inheritdoc/>
        protected override void ValidateRequest<TRequest>(TRequest request, string operationName)
        {
            base.ValidateRequest(request, operationName);

            switch (request)
            {
                case ChatCompletionRequest chat when chat.Messages is null || !chat.Messages.Any():
                    throw new ValidationException($"{operationName}: Messages cannot be null or empty");
                case EmbeddingRequest embed when embed.Input is null:
                    throw new ValidationException($"{operationName}: Input cannot be null");
                case ImageGenerationRequest image when string.IsNullOrWhiteSpace(image.Prompt):
                    throw new ValidationException($"{operationName}: Prompt cannot be null or empty");
            }
        }

        private static ChatCompletionChunk CreateChatCompletionChunk(
            string content,
            string model,
            bool isFirst = false,
            string? finishReason = null) => new()
            {
                Id = $"chatcmpl-{Guid.NewGuid():N}",
                Object = "chat.completion.chunk",
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = model,
                Choices = new List<StreamingChoice>
                {
                    new StreamingChoice
                    {
                        Index = 0,
                        Delta = new DeltaContent
                        {
                            Role = isFirst ? "assistant" : null,
                            Content = content,
                        },
                        FinishReason = finishReason,
                    },
                },
            };

        /// <inheritdoc/>
        protected override void ConfigureHttpClient(HttpClient client, string apiKey)
        {
            // Configure standard headers
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("User-Agent", "ConduitLLM");

            // Apply Token authentication via strategy
            AuthenticationStrategy.ApplyAuthentication(client, apiKey);

            // Set the base address if not already set
            // Ensure base URL ends with trailing slash for relative path resolution
            if (client.BaseAddress == null && !string.IsNullOrEmpty(BaseUrl))
            {
                var baseUrl = BaseUrl.EndsWith('/') ? BaseUrl : BaseUrl + '/';
                client.BaseAddress = new Uri(baseUrl);
            }
        }

        /// <summary>
        /// Gets the default base URL for Replicate from the configuration registry.
        /// </summary>
        protected override string GetDefaultBaseUrl()
        {
            return ProviderConfigurationRegistry.GetDefaultBaseUrl(ProviderType.Replicate)!;
        }
    }
}
