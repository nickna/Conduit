using System.Text.Json;
using System.Text.Json.Serialization;
using ConduitLLM.Providers.Serialization;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Providers.Common.Models;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.Cloudflare
{
    /// <summary>
    /// CloudflareClient partial class containing model discovery functionality.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cloudflare Workers AI does NOT expose the OpenAI-compatible <c>GET /ai/v1/models</c> endpoint;
    /// requesting it returns HTTP 405 (Method Not Allowed). Model discovery instead lives on the native
    /// account-scoped endpoint <c>GET /accounts/{account_id}/ai/models/search</c>, a sibling of the
    /// <c>/ai/v1</c> OpenAI-compatible path.
    /// </para>
    /// <para>
    /// This override targets that native endpoint so that connection tests validate the API token and
    /// account ID against a real, authenticated GET request rather than failing on the non-existent
    /// OpenAI-style models path.
    /// </para>
    /// </remarks>
    public partial class CloudflareClient
    {
        /// <inheritdoc />
        public override async Task<List<ExtendedModelInfo>> GetModelsAsync(
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteApiRequestAsync(async () =>
            {
                using var client = CreateHttpClient(apiKey);

                var endpoint = $"{BuildAiBaseUrl()}/models/search";

                Logger.LogDebug("Getting available models from Cloudflare at {Endpoint}", endpoint);

                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using var httpResponse = await client.SendAsync(
                    httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);

                var content = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    // Surface Cloudflare's own error text when present (for example "Authentication error"),
                    // which lets the API-key test classifier distinguish a bad token from a bad account ID.
                    var errorMessage = TryExtractCloudflareError(content)
                        ?? $"{(int)httpResponse.StatusCode} ({httpResponse.StatusCode})";
                    throw new LLMCommunicationException(
                        $"Cloudflare model discovery failed: {errorMessage} [HTTP {(int)httpResponse.StatusCode}]");
                }

                var parsed = JsonSerializer.Deserialize(
                    content,
                    ProvidersJsonContext.Default.CloudflareModelsSearchResponse);

                var models = parsed?.Result?
                    .Select(entry => (Id: SelectModelIdentifier(entry), entry.Description))
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                    .Select(model => ExtendedModelInfo.Create(model.Id!, ProviderName, model.Description ?? model.Id!))
                    .ToList();

                // Cloudflare authenticated the request but returned no usable models: fall back to the
                // curated list so callers still see the known Workers AI catalog.
                return models is { Count: > 0 } ? models : CloudflareModels;
            }, "GetModels", cancellationToken);
        }

        /// <summary>
        /// Selects the Workers AI inference identifier (e.g. <c>@cf/meta/llama-3.1-8b-instruct</c>) for a
        /// model entry. Cloudflare carries this slug in the <c>name</c> field while <c>id</c> is an opaque
        /// UUID, so this prefers whichever field actually holds the <c>@</c>-prefixed slug to stay robust
        /// against schema drift.
        /// </summary>
        private static string? SelectModelIdentifier(CloudflareModelEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name) && entry.Name.StartsWith('@'))
            {
                return entry.Name;
            }

            if (!string.IsNullOrWhiteSpace(entry.Id) && entry.Id.StartsWith('@'))
            {
                return entry.Id;
            }

            return entry.Name ?? entry.Id;
        }
    }

    /// <summary>
    /// Cloudflare API response wrapper for the model-search endpoint.
    /// </summary>
    internal sealed class CloudflareModelsSearchResponse
    {
        [JsonPropertyName("result")]
        public List<CloudflareModelEntry>? Result { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errors")]
        public List<CloudflareApiError>? Errors { get; set; }
    }

    /// <summary>
    /// A single model entry returned by the Cloudflare model-search endpoint.
    /// </summary>
    internal sealed class CloudflareModelEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
