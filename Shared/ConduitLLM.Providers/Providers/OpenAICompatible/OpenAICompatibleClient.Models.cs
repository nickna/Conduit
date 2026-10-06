using Microsoft.Extensions.Logging;
using CoreUtils = ConduitLLM.Core.Utilities;
using ConduitLLM.Providers.OpenAI;
using InternalModels = ConduitLLM.Providers.Common.Models;

namespace ConduitLLM.Providers.OpenAICompatible
{
    /// <summary>
    /// OpenAICompatibleClient partial class containing model listing functionality.
    /// </summary>
    public abstract partial class OpenAICompatibleClient
    {
        /// <summary>
        /// Gets available models from the OpenAI-compatible API.
        /// </summary>
        /// <param name="apiKey">Optional API key to override the one in credentials.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A list of available models.</returns>
        /// <remarks>
        /// This implementation attempts to retrieve models from the provider's API,
        /// but falls back to a predefined list if the request fails or if the provider
        /// doesn't support the standard /models endpoint.
        /// </remarks>
        public override async Task<List<InternalModels.ExtendedModelInfo>> GetModelsAsync(
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteApiRequestAsync(async () =>
            {
                using var client = CreateHttpClient(apiKey);

                var endpoint = GetModelsEndpoint();

                Logger.LogDebug("Getting available models from {Provider} at {Endpoint}", ProviderName, endpoint);

                var response = await CoreUtils.HttpClientHelper.GetJsonAsync<ListModelsResponse>(
                    client,
                    endpoint,
                    Serialization.ProvidersJsonContext.Default.ListModelsResponse,
                    CreateStandardHeaders(apiKey),
                    Logger,
                    cancellationToken);

                return response.Data
                    .Select(m => InternalModels.ExtendedModelInfo.Create(m.Id, ProviderName, m.Id))
                    .ToList();
            }, "GetModels", cancellationToken);
        }
    }
}
