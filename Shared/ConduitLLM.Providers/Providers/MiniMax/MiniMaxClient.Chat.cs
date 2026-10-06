using System.Text.Json;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.MiniMax
{
    /// <summary>
    /// MiniMaxClient partial class containing chat completion methods.
    /// </summary>
    public partial class MiniMaxClient
    {
        /// <inheritdoc />
        public override async Task<ChatCompletionResponse> CreateChatCompletionAsync(
            ChatCompletionRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "CreateChatCompletion");

            return await ExecuteApiRequestAsync(async () =>
            {
                using var httpClient = CreateHttpClient(apiKey);
                
                var miniMaxRequest = CreateChatRequest(request, request.Stream == true);

                // MiniMax uses different endpoints for streaming vs non-streaming
                // Streaming uses the v2 API which requires name fields in messages
                var endpoint = request.Stream == true
                    ? $"{_baseUrl}/v1/text/chatcompletion_v2"
                    : $"{_baseUrl}/v1/chat/completions";

                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    var requestJson = JsonSerializer.Serialize(
                        miniMaxRequest,
                        MiniMaxStreamJsonContext.Default.MiniMaxChatCompletionRequest);
                    Logger.LogDebug("MiniMax request to {Endpoint}: {Request}", endpoint, requestJson);
                }

                var response = await SendMiniMaxJsonAsync<
                    MiniMaxChatCompletionRequest,
                    MiniMaxChatCompletionResponse>(
                    httpClient,
                    endpoint,
                    miniMaxRequest,
                    CaseInsensitiveJsonOptions,
                    cancellationToken);

                Logger.LogDebug("MiniMax response choices count: {Count}", response.Choices?.Count ?? 0);

                // Check for MiniMax error response
                if (response.BaseResp is { } baseResp && baseResp.StatusCode != 0)
                {
                    Logger.LogError("MiniMax error: {StatusCode} - {StatusMsg}",
                        baseResp.StatusCode, baseResp.StatusMsg);
                    throw new LLMCommunicationException($"MiniMax error: {baseResp.StatusMsg}");
                }

                var coreResponse = ConvertToCoreResponse(response, request.Model ?? ProviderModelId);
                RecordUsage(coreResponse.Usage, "CreateChatCompletion");
                return coreResponse;
            }, "CreateChatCompletion", cancellationToken);
        }

        private MiniMaxChatCompletionRequest CreateChatRequest(
            ChatCompletionRequest request,
            bool stream) =>
            new()
            {
                Model = request.Model ?? ProviderModelId,
                Messages = ConvertMessages(request.Messages, includeNames: stream),
                Stream = stream,
                MaxTokens = request.MaxTokens,
                Temperature = request.Temperature,
                TopP = request.TopP,
                Tools = ConvertTools(request.Tools),
                ToolChoice = ConvertToolChoice(request.ToolChoice),
                ReplyConstraints = request.ResponseFormat != null ? new ReplyConstraints
                {
                    GuidanceType =
                        request.ResponseFormat.Type == "json_object" ? "json_schema" : null,
                    JsonSchema =
                        request.ResponseFormat.Type == "json_object"
                            ? new Dictionary<string, string> { ["type"] = "object" }
                            : null
                } : null
            };
    }
}
