using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using CoreModels = ConduitLLM.Core.Models;
using CoreUtils = ConduitLLM.Core.Utilities;
using ConduitLLM.Providers.OpenAI;

namespace ConduitLLM.Providers.OpenAICompatible
{
    /// <summary>
    /// OpenAICompatibleClient partial class containing streaming functionality.
    /// </summary>
    public abstract partial class OpenAICompatibleClient
    {
        /// <summary>
        /// Streams a chat completion using the OpenAI-compatible API.
        /// </summary>
        /// <param name="request">The chat completion request.</param>
        /// <param name="apiKey">Optional API key to override the one in credentials.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>An async enumerable of chat completion chunks.</returns>
        /// <remarks>
        /// This implementation:
        /// <list type="bullet">
        /// <item>Validates the request for required parameters</item>
        /// <item>Maps the generic request to the OpenAI format, forcing the stream parameter to true</item>
        /// <item>Establishes a streaming connection to the provider's API</item>
        /// <item>Processes the server-sent events (SSE) format</item>
        /// <item>Maps each chunk back to the generic format</item>
        /// <item>Handles errors in a standardized way</item>
        /// </list>
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when the request is null.</exception>
        /// <exception cref="ValidationException">Thrown when the request fails validation.</exception>
        /// <exception cref="LLMCommunicationException">Thrown when there is a communication error with the provider.</exception>
        /// <exception cref="ConfigurationException">Thrown when there is a configuration error.</exception>
        public override async IAsyncEnumerable<CoreModels.ChatCompletionChunk> StreamChatCompletionAsync(
            CoreModels.ChatCompletionRequest request,
            string? apiKey = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "StreamChatCompletion");

            // Stream chunks progressively without buffering
            await foreach (var chunk in StreamChunksProgressivelyAsync(request, apiKey, cancellationToken).WithCancellation(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                yield return chunk;
            }
        }

        /// <summary>
        /// Transforms the raw JSON of a streaming chunk before deserialization.
        /// Override in subclasses to perform provider-specific JSON transformations
        /// (e.g., extracting usage data from vendor-specific fields).
        /// </summary>
        /// <param name="chunk">The raw JSON element from the SSE stream.</param>
        /// <returns>The JSON string to deserialize into a ChatCompletionChunk.</returns>
        protected virtual string TransformChunkJson(JsonElement chunk)
            => chunk.GetRawText();

        /// <summary>
        /// Maps a raw provider chunk to the provider-agnostic streaming model.
        /// Subclasses may override this when server-only metadata must be preserved
        /// separately from the JSON returned to clients.
        /// </summary>
        protected virtual CoreModels.ChatCompletionChunk? MapStreamingChunk(JsonElement chunk)
        {
            var chunkJson = TransformChunkJson(chunk);
            return JsonSerializer.Deserialize(
                chunkJson,
                Core.Serialization.CoreHttpJsonContext.Default.ChatCompletionChunk);
        }

        /// <summary>
        /// Streams chunks progressively without buffering them into a list
        /// </summary>
        protected virtual IAsyncEnumerable<CoreModels.ChatCompletionChunk> StreamChunksProgressivelyAsync(
            CoreModels.ChatCompletionRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return RunStreamingAsync(
                ct => ReadOpenAiStreamAsync(request, apiKey, ct),
                "StreamChatCompletion",
                request.Model ?? ProviderModelId,
                cancellationToken);
        }

        private async IAsyncEnumerable<CoreModels.ChatCompletionChunk> ReadOpenAiStreamAsync(
            CoreModels.ChatCompletionRequest request,
            string? apiKey,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var client = CreateHttpClient(apiKey);
            var openAiRequest = PrepareStreamingRequest(request);
            var endpoint = GetChatCompletionEndpoint();

            Logger.LogDebug(
                "Sending streaming chat completion request to {Provider} at {Endpoint}",
                ProviderName,
                endpoint);

            using var response = await SendStreamingRequestAsync(
                client,
                endpoint,
                openAiRequest,
                apiKey,
                cancellationToken);
            var reportedUsage = false;

            await foreach (var chunk in CoreUtils.StreamHelper.ProcessSseStreamAsync<JsonElement>(
                response,
                Serialization.ProvidersJsonContext.Default.JsonElement,
                Logger,
                cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                var mappedChunk = MapStreamingChunk(chunk);
                if (mappedChunk is null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(request.Model))
                {
                    mappedChunk.Model = request.Model;
                    mappedChunk.OriginalModelAlias = request.Model;
                }

                ExtractProviderUsageFromExtensionData(mappedChunk.Usage);
                if (!reportedUsage && mappedChunk.Usage != null)
                {
                    RecordUsage(mappedChunk.Usage, "StreamChatCompletion");
                    reportedUsage = true;
                }

                yield return mappedChunk;
            }
        }

        /// <summary>
        /// Prepares a request for streaming by ensuring the stream parameter is set to true
        /// and stream_options includes usage data if not already set
        /// </summary>
        /// <param name="request">The original chat completion request</param>
        /// <returns>A request object with stream=true and stream_options configured</returns>
        private object PrepareStreamingRequest(CoreModels.ChatCompletionRequest request)
        {
            // Ensure stream_options is set to request usage data if not already configured
            // This is critical for accurate token counting and billing in streaming mode
            request.StreamOptions ??= new CoreModels.StreamOptions { IncludeUsage = true };

            var openAiRequest = MapToOpenAIRequest(request);

            // Force stream parameter to true (all MapToOpenAIRequest overrides return a dictionary)
            if (openAiRequest is Dictionary<string, object> dictObj)
            {
                dictObj["stream"] = true;
                // Ensure stream_options is present
                if (!dictObj.ContainsKey("stream_options"))
                {
                    dictObj["stream_options"] = new CoreModels.StreamOptions { IncludeUsage = true };
                }
                return dictObj;
            }

            // If we can't determine the type, return the original request
            return openAiRequest;
        }

        /// <summary>
        /// Sends a streaming request to the specified endpoint
        /// </summary>
        /// <param name="client">The HTTP client to use</param>
        /// <param name="endpoint">The endpoint to send the request to</param>
        /// <param name="request">The request object</param>
        /// <param name="apiKey">Optional API key to override the one in credentials</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests</param>
        /// <returns>The HTTP response message</returns>
        private async Task<HttpResponseMessage> SendStreamingRequestAsync(
            HttpClient client,
            string endpoint,
            object request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await CoreUtils.HttpClientHelper.SendStreamingRequestAsync(
                client,
                HttpMethod.Post,
                endpoint,
                request,
                GetRequiredJsonTypeInfo<object>(DefaultJsonOptions),
                CreateStandardHeaders(apiKey),
                Logger,
                cancellationToken,
                TranslateHttpError);
        }

    }
}
