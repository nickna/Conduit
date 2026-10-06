using System.Text.Json.Serialization;

using CoreModels = ConduitLLM.Core.Models;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.OpenRouter
{
    public partial class OpenRouterClient
    {
        /// <summary>
        /// Generates images via OpenRouter's native image API (<c>POST /api/v1/images</c>), which
        /// differs from the OpenAI <c>/images/generations</c> schema the base client posts to (that
        /// endpoint does not exist on OpenRouter, so the inherited implementation 404s).
        /// </summary>
        /// <remarks>
        /// OpenRouter returns images as base64 <c>b64_json</c> plus a usage block carrying the actual
        /// USD cost, which is captured for authoritative billing. Output defaults to PNG so the stored
        /// bytes are labeled <c>image/png</c> correctly by the images controller; other formats can be
        /// requested via extension data (the stored content-type label is a known limitation there).
        /// </remarks>
        public override async Task<CoreModels.ImageGenerationResponse> CreateImageAsync(
            CoreModels.ImageGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "CreateImage");

            return await ExecuteApiRequestAsync(async () =>
            {
                using var client = CreateHttpClient(apiKey);

                var body = new Dictionary<string, object?>
                {
                    ["prompt"] = request.Prompt,
                    ["model"] = request.Model ?? ProviderModelId,
                    ["n"] = Math.Clamp(request.N, 1, 10)
                };

                if (!string.IsNullOrEmpty(request.Size))
                    body["size"] = request.Size;

                var quality = MapQuality(request.Quality);
                if (quality != null)
                    body["quality"] = quality;

                // Image-to-image: map the base64 input image to OpenRouter's input_references.
                if (!string.IsNullOrEmpty(request.Image))
                    body["input_references"] = new[] { $"data:image/png;base64,{request.Image}" };

                // Pass through extension data, excluding parameters OpenRouter's image endpoint does not
                // use (stream / response_format) and anything already mapped above.
                if (request.ExtensionData != null)
                {
                    foreach (var kvp in request.ExtensionData)
                    {
                        if (kvp.Key is "stream" or "response_format")
                            continue;
                        if (!body.ContainsKey(kvp.Key))
                            body[kvp.Key] = kvp.Value;
                    }
                }

                if (!body.ContainsKey("output_format"))
                    body["output_format"] = "png";

                var endpoint = $"{BaseUrl}/images";
                Logger.LogInformation("Creating images via {Provider} at {Endpoint} with model {Model}",
                    ProviderName, endpoint, body["model"]);

                var response = await PostJsonAsync<Dictionary<string, object?>, OpenRouterImageResponse>(
                    client,
                    endpoint,
                    body,
                    apiKey,
                    cancellationToken);

                var data = response.Data?
                    .Select(d => new CoreModels.ImageData { B64Json = d.B64Json })
                    .ToList() ?? new List<CoreModels.ImageData>();

                var usage = response.Usage ?? new CoreModels.Usage();
                // Capture the provider-reported cost (and strip it from extension data) so billing can
                // use it authoritatively; then annotate image request-shape for cost/logging.
                ExtractProviderUsageFromExtensionData(usage);
                usage.ImageCount = data.Count;
                usage.ImageQuality = request.Quality;
                usage.ImageResolution = request.Size;

                return new CoreModels.ImageGenerationResponse
                {
                    Created = response.Created != 0 ? response.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Data = data,
                    Usage = usage
                };
            }, "CreateImage", cancellationToken);
        }

        /// <summary>
        /// Maps OpenAI-style quality tiers to OpenRouter's (auto/low/medium/high); unknown values are
        /// omitted rather than forwarded so OpenRouter doesn't reject the request.
        /// </summary>
        private static string? MapQuality(string? quality) => quality?.ToLowerInvariant() switch
        {
            "hd" => "high",
            "standard" => "medium",
            "auto" => "auto",
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => null
        };

        internal record OpenRouterImageResponse
        {
            [JsonPropertyName("created")]
            public long Created { get; init; }

            [JsonPropertyName("data")]
            public List<OpenRouterImageData>? Data { get; init; }

            [JsonPropertyName("usage")]
            public CoreModels.Usage? Usage { get; init; }
        }

        internal record OpenRouterImageData
        {
            [JsonPropertyName("b64_json")]
            public string? B64Json { get; init; }

            [JsonPropertyName("media_type")]
            public string? MediaType { get; init; }
        }
    }
}
