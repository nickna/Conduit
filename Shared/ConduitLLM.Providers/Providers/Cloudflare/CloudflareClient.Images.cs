using System.Text.Json;
using System.Text.Json.Serialization;
using ConduitLLM.Providers.Serialization;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.Helpers;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.Cloudflare
{
    /// <summary>
    /// CloudflareClient partial class containing image generation functionality.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cloudflare Workers AI image generation uses the native /ai/run/{MODEL_ID} endpoint,
    /// which is NOT OpenAI-compatible. This override handles the Cloudflare-specific request
    /// and response formats.
    /// </para>
    /// <para>
    /// Response formats vary by model family:
    /// - SD family, DreamShaper, Phoenix: Raw binary image (PNG or JPEG)
    /// - Flux 1, Lucid Origin: JSON with base64-encoded image
    /// </para>
    /// <para>
    /// Phase 1 supports JSON-body models only. Flux 2 models (which require multipart
    /// form-data) are not yet supported.
    /// </para>
    /// </remarks>
    public partial class CloudflareClient
    {
        /// <summary>
        /// Maximum number of images that can be generated in a single request.
        /// Cloudflare generates one image per API call, so N>1 requires parallel calls.
        /// </summary>
        private const int MaxImagesPerRequest = 4;

        /// <inheritdoc />
        public override async Task<ImageGenerationResponse> CreateImageAsync(
            ImageGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "CreateImage");

            var modelId = request.Model ?? ProviderModelId;
            var modelFamily = ClassifyModelFamily(modelId);

            if (modelFamily == CloudflareImageModelFamily.Flux2)
            {
                throw new NotSupportedException(
                    $"Flux 2 models require multipart form-data and are not yet supported. " +
                    $"Use Flux 1 Schnell (@cf/black-forest-labs/flux-1-schnell) or Stable Diffusion models instead.");
            }

            return await ExecuteApiRequestAsync(async () =>
            {
                var imageCount = Math.Min(Math.Max(request.N, 1), MaxImagesPerRequest);

                if (imageCount == 1)
                {
                    var imageData = await GenerateSingleImageAsync(request, modelId, modelFamily, apiKey, cancellationToken);
                    return new ImageGenerationResponse
                    {
                        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Data = new List<ImageData> { imageData }
                    };
                }

                // Cloudflare generates one image per call; run N calls in parallel
                var tasks = Enumerable.Range(0, imageCount)
                    .Select(_ => GenerateSingleImageAsync(request, modelId, modelFamily, apiKey, cancellationToken))
                    .ToList();

                var results = await Task.WhenAll(tasks);

                return new ImageGenerationResponse
                {
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Data = results.ToList()
                };
            }, "CreateImage", cancellationToken);
        }

        /// <summary>
        /// Generates a single image from Cloudflare Workers AI.
        /// </summary>
        private async Task<ImageData> GenerateSingleImageAsync(
            ImageGenerationRequest request,
            string modelId,
            CloudflareImageModelFamily modelFamily,
            string? apiKey,
            CancellationToken cancellationToken)
        {
            using var client = CreateHttpClient(apiKey);

            var nativeEndpoint = BuildNativeEndpoint(modelId);
            var requestBody = BuildImageRequestBody(request, modelFamily);

            Logger.LogInformation(
                "Creating image using Cloudflare Workers AI at {Endpoint} with model {Model}, prompt length: {PromptLength}",
                nativeEndpoint, modelId, request.Prompt.Length);

            var requestJson = JsonSerializer.Serialize(
                requestBody,
                ProvidersJsonContext.Default.DictionaryStringObject);
            using var httpContent = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, nativeEndpoint)
            {
                Content = httpContent
            };

            using var httpResponse = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorContent = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                var errorMessage = TryExtractCloudflareError(errorContent)
                    ?? $"Cloudflare Workers AI returned {httpResponse.StatusCode}";
                Logger.LogError("Cloudflare image generation failed: {StatusCode} - {Error}", httpResponse.StatusCode, errorMessage);
                throw new LLMCommunicationException($"Cloudflare image generation failed: {errorMessage}");
            }

            var contentType = httpResponse.Content.Headers.ContentType?.MediaType ?? "";

            // JSON response (Flux 1, Lucid Origin)
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return await ParseJsonImageResponseAsync(httpResponse, cancellationToken);
            }

            // Binary response (SD family, DreamShaper, Phoenix)
            return await ParseBinaryImageResponseAsync(httpResponse, cancellationToken);
        }

        /// <summary>
        /// Builds the native Cloudflare endpoint URL for image generation.
        /// Transforms the OpenAI-compat base URL to the native /ai/run/{model} format.
        /// </summary>
        private string BuildNativeEndpoint(string modelId)
        {
            // BaseUrl is like: https://api.cloudflare.com/client/v4/accounts/{ACCOUNT_ID}/ai/v1
            // We need:          https://api.cloudflare.com/client/v4/accounts/{ACCOUNT_ID}/ai/run/{MODEL_ID}
            return $"{BuildAiBaseUrl()}/run/{modelId}";
        }

        /// <summary>
        /// Derives the account-scoped <c>/ai</c> base URL from the configured OpenAI-compatible base URL.
        /// Cloudflare's native (non-OpenAI) endpoints — image generation (<c>/run/{model}</c>) and model
        /// discovery (<c>/models/search</c>) — live as siblings of the <c>/ai/v1</c> OpenAI-compatible path.
        /// </summary>
        /// <returns>The <c>.../ai</c> base URL, without a trailing slash.</returns>
        private string BuildAiBaseUrl()
        {
            // BaseUrl is like: https://api.cloudflare.com/client/v4/accounts/{ACCOUNT_ID}/ai/v1
            // We need the /ai root: https://api.cloudflare.com/client/v4/accounts/{ACCOUNT_ID}/ai
            var baseUrl = BaseUrl.TrimEnd('/');

            // Strip the /v1 suffix to get the /ai base
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl[..^3];
            }

            if (baseUrl.EndsWith("/ai", StringComparison.OrdinalIgnoreCase))
            {
                // Already at the /ai level
                return baseUrl;
            }

            // Fallback: the URL doesn't follow the expected pattern; use it as-is as the /ai base.
            Logger.LogWarning(
                "Cloudflare base URL '{BaseUrl}' doesn't match the expected '/ai/v1' pattern; using it as the /ai base.",
                baseUrl);
            return baseUrl;
        }

        /// <summary>
        /// Builds the request body for a Cloudflare image generation call.
        /// Parameter names vary by model family.
        /// </summary>
        private Dictionary<string, object?> BuildImageRequestBody(
            ImageGenerationRequest request,
            CloudflareImageModelFamily modelFamily)
        {
            var body = new Dictionary<string, object?>
            {
                ["prompt"] = request.Prompt
            };

            // Parse size into width/height
            if (!string.IsNullOrEmpty(request.Size))
            {
                var dimensions = request.Size.Split('x');
                if (dimensions.Length == 2
                    && int.TryParse(dimensions[0], out var width)
                    && int.TryParse(dimensions[1], out var height))
                {
                    body["width"] = width;
                    body["height"] = height;
                }
            }

            // Map steps parameter (name differs by family)
            var stepsKey = modelFamily == CloudflareImageModelFamily.Flux1 ? "steps" : "num_steps";

            // Map extension data (negative_prompt, guidance, seed, strength, num_steps/steps)
            if (request.ExtensionData != null)
            {
                foreach (var kvp in request.ExtensionData)
                {
                    var key = kvp.Key;

                    // Normalize steps parameter name for the target model family
                    if (key is "num_steps" or "steps")
                    {
                        key = stepsKey;
                    }

                    if (!body.ContainsKey(key))
                    {
                        body[key] = ConvertJsonElement(kvp.Value);
                    }
                }
            }

            // Image-to-image support (SD family models)
            if (!string.IsNullOrEmpty(request.Image) && modelFamily == CloudflareImageModelFamily.StableDiffusion)
            {
                body["image_b64"] = request.Image;
            }

            if (!string.IsNullOrEmpty(request.Mask) && modelFamily == CloudflareImageModelFamily.StableDiffusion)
            {
                // Cloudflare accepts mask as byte array; pass through base64 as image_b64 for inpainting
                body["mask"] = request.Mask;
            }

            return body;
        }

        /// <summary>
        /// Parses a JSON response containing a base64-encoded image (Flux 1, Lucid Origin).
        /// </summary>
        private async Task<ImageData> ParseJsonImageResponseAsync(
            HttpResponseMessage httpResponse,
            CancellationToken cancellationToken)
        {
            var content = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            CloudflareImageResponse? response;
            try
            {
                response = JsonSerializer.Deserialize(
                    content,
                    ProvidersJsonContext.Default.CloudflareImageResponse);
            }
            catch (JsonException ex)
            {
                Logger.LogError(ex, "Failed to parse Cloudflare JSON image response");
                throw new LLMCommunicationException("Failed to parse Cloudflare image response", ex);
            }

            if (response == null || response.Success == false)
            {
                var errorMsg = response?.Errors?.FirstOrDefault()?.Message ?? "Unknown error";
                throw new LLMCommunicationException($"Cloudflare image generation failed: {errorMsg}");
            }

            var base64Image = response.Result?.Image;
            if (string.IsNullOrEmpty(base64Image))
            {
                throw new LLMCommunicationException("Cloudflare returned a successful response but no image data");
            }

            return new ImageData { B64Json = base64Image };
        }

        /// <summary>
        /// Parses a binary image response (SD family, DreamShaper, Phoenix).
        /// Converts the raw bytes to base64.
        /// </summary>
        private async Task<ImageData> ParseBinaryImageResponseAsync(
            HttpResponseMessage httpResponse,
            CancellationToken cancellationToken)
        {
            var imageBytes = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);

            if (imageBytes.Length == 0)
            {
                throw new LLMCommunicationException("Cloudflare returned an empty image response");
            }

            var base64String = Convert.ToBase64String(imageBytes);
            return new ImageData { B64Json = base64String };
        }

        /// <summary>
        /// Attempts to extract an error message from a Cloudflare API error response.
        /// </summary>
        private string? TryExtractCloudflareError(string responseContent)
        {
            try
            {
                var errorResponse = JsonSerializer.Deserialize(
                    responseContent,
                    ProvidersJsonContext.Default.CloudflareImageResponse);
                if (errorResponse?.Errors != null && errorResponse.Errors.Count > 0)
                {
                    return string.Join("; ", errorResponse.Errors
                        .Where(e => !string.IsNullOrEmpty(e.Message))
                        .Select(e => e.Message));
                }
            }
            catch
            {
                // Response is not JSON or not in expected format
            }

            return null;
        }

        private static object? ConvertJsonElement(JsonElement element) =>
            ConduitLLM.Functions.Utilities.JsonElementConverter.ConvertJsonElement(element);

        /// <summary>
        /// Classifies a Cloudflare model ID into its model family.
        /// </summary>
        private static CloudflareImageModelFamily ClassifyModelFamily(string modelId)
        {
            if (modelId.Contains("flux-2", StringComparison.OrdinalIgnoreCase))
                return CloudflareImageModelFamily.Flux2;

            if (modelId.Contains("flux-1", StringComparison.OrdinalIgnoreCase))
                return CloudflareImageModelFamily.Flux1;

            if (modelId.Contains("leonardo", StringComparison.OrdinalIgnoreCase))
                return CloudflareImageModelFamily.Leonardo;

            // SD family: stabilityai, bytedance (sdxl-lightning), lykon (dreamshaper), runwayml
            return CloudflareImageModelFamily.StableDiffusion;
        }

        /// <summary>
        /// Cloudflare image model families, each with different API behavior.
        /// </summary>
        private enum CloudflareImageModelFamily
        {
            /// <summary>Stable Diffusion variants: SDXL, SDXL Lightning, DreamShaper, SD v1.5. JSON request, binary PNG response.</summary>
            StableDiffusion,

            /// <summary>Flux 1 Schnell. JSON request, base64 JSON response.</summary>
            Flux1,

            /// <summary>Flux 2 models (dev, klein). Multipart form-data request, base64 JSON response. Not yet supported.</summary>
            Flux2,

            /// <summary>Leonardo models (Phoenix, Lucid Origin). JSON request, mixed response format.</summary>
            Leonardo
        }
    }

    /// <summary>
    /// Cloudflare API response wrapper for image generation.
    /// </summary>
    internal class CloudflareImageResponse
    {
        [JsonPropertyName("result")]
        public CloudflareImageResult? Result { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errors")]
        public List<CloudflareApiError>? Errors { get; set; }

        [JsonPropertyName("messages")]
        public List<CloudflareApiError>? Messages { get; set; }
    }

    /// <summary>
    /// The result payload from a Cloudflare image generation call.
    /// </summary>
    internal class CloudflareImageResult
    {
        [JsonPropertyName("image")]
        public string? Image { get; set; }
    }

    /// <summary>
    /// A Cloudflare API error entry.
    /// </summary>
    internal class CloudflareApiError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
