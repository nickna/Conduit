using Microsoft.Extensions.Logging;
using CoreModels = ConduitLLM.Core.Models;
using ConduitLLM.Providers.OpenAI;

namespace ConduitLLM.Providers.OpenAICompatible
{
    /// <summary>
    /// OpenAICompatibleClient partial class containing embedding functionality.
    /// </summary>
    public abstract partial class OpenAICompatibleClient
    {
        /// <summary>
        /// Creates embeddings using the OpenAI-compatible API.
        /// </summary>
        /// <param name="request">The embedding request.</param>
        /// <param name="apiKey">Optional API key to override the one in credentials.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>An embedding response.</returns>
        /// <remarks>
        /// This implementation sends an embedding request to the provider's API and maps the
        /// response to the generic format. If a provider doesn't support embeddings, this method
        /// should be overridden to throw a <see cref="NotSupportedException"/>.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when the request is null.</exception>
        /// <exception cref="ValidationException">Thrown when the request fails validation.</exception>
        /// <exception cref="LLMCommunicationException">Thrown when there is a communication error with the provider.</exception>
        /// <exception cref="NotSupportedException">Thrown when the provider doesn't support embeddings.</exception>
        public override async Task<CoreModels.EmbeddingResponse> CreateEmbeddingAsync(
            CoreModels.EmbeddingRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "CreateEmbedding");

            return await ExecuteApiRequestAsync(async () =>
            {
                using var client = CreateHttpClient(apiKey);

                var openAiRequest = new EmbeddingRequest
                {
                    Model = request.Model ?? ProviderModelId,
                    Input = request.Input,
                    EncodingFormat = request.EncodingFormat,
                    User = request.User ?? string.Empty,
                    Dimensions = request.Dimensions
                };

                var endpoint = GetEmbeddingEndpoint();

                Logger.LogDebug("Creating embeddings using {Provider} at {Endpoint}", ProviderName, endpoint);

                var response = await PostJsonAsync<EmbeddingRequest, EmbeddingResponse>(
                    client,
                    endpoint,
                    openAiRequest,
                    apiKey,
                    cancellationToken);

                return new CoreModels.EmbeddingResponse
                {
                    Data = response.Data.Select(d => new CoreModels.EmbeddingData
                    {
                        Index = d.Index,
                        Object = d.Object,
                        Embedding = d.Embedding
                    }).ToList(),
                    Model = response.Model ?? ProviderModelId,
                    Object = response.Object ?? "embedding",
                    Usage = new CoreModels.Usage
                    {
                        PromptTokens = response.Usage?.PromptTokens ?? 0,
                        CompletionTokens = 0, // Embeddings don't have completion tokens
                        TotalTokens = response.Usage?.TotalTokens ?? 0
                    }
                };
            }, "CreateEmbedding", cancellationToken);
        }
    }
}
