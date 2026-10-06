using System.Text.Json;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Providers.Helpers;
using ConduitLLM.Providers.Serialization;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.Bedrock
{
    /// <summary>
    /// BedrockClient partial class containing chat completion via the Converse API.
    /// </summary>
    public partial class BedrockClient
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

                var payload = SerializePayload(MapToConverseRequest(request));
                var url = BuildModelUrl(request.Model ?? ProviderModelId, "converse");
                using var httpRequest = BuildRequest(HttpMethod.Post, url, payload, RuntimeService, apiKey);

                using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken);
                await ThrowOnErrorAsync(httpResponse, "chat completion", cancellationToken);

                var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                var converseResponse = JsonSerializer.Deserialize(
                        body,
                        ProvidersJsonContext.Default.BedrockConverseResponse)
                    ?? throw new LLMCommunicationException("Bedrock returned an empty converse response.");

                var response = MapToCoreResponse(converseResponse, request.Model ?? ProviderModelId);
                RecordUsage(response.Usage, "CreateChatCompletion");
                return response;
            }, "CreateChatCompletion", cancellationToken);
        }

        /// <summary>
        /// Maps the OpenAI-shaped request to a Converse request: system messages become system
        /// blocks, tool results become <c>toolResult</c> blocks on a user message, assistant tool
        /// calls become <c>toolUse</c> blocks, and consecutive same-role messages are merged because
        /// Converse requires strictly alternating roles.
        /// </summary>
        internal BedrockConverseRequest MapToConverseRequest(ChatCompletionRequest request)
        {
            var system = new List<BedrockSystemBlock>();
            var messages = new List<BedrockMessage>();

            foreach (var message in request.Messages)
            {
                if (!string.Equals(message.Role, MessageRole.Tool, StringComparison.OrdinalIgnoreCase))
                {
                    EnsureSupportedContentParts(message.Content);
                }

                if (string.Equals(message.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
                {
                    var text = ContentHelper.GetContentAsString(message.Content);
                    if (!string.IsNullOrEmpty(text))
                    {
                        system.Add(new BedrockSystemBlock { Text = text });
                    }
                    continue;
                }

                var mapped = MapMessage(message);
                if (mapped.Content.Count == 0)
                {
                    continue;
                }

                if (messages.Count > 0 && messages[^1].Role == mapped.Role)
                {
                    messages[^1].Content.AddRange(mapped.Content);
                }
                else
                {
                    messages.Add(mapped);
                }
            }

            var converseRequest = new BedrockConverseRequest
            {
                Messages = messages,
                System = system.Count > 0 ? system : null,
                InferenceConfig = MapInferenceConfig(request),
                ToolConfig = MapToolConfig(request)
            };

            if (request.TopK is { } topK)
            {
                converseRequest.AdditionalModelRequestFields = new Dictionary<string, JsonElement>
                {
                    ["top_k"] = JsonSerializer.SerializeToElement(
                        topK,
                        ProvidersJsonContext.Default.Int32)
                };
            }

            if (request.ResponseFormat?.Type == "json_object")
            {
                Logger.LogDebug("Bedrock Converse has no JSON response mode; response_format is ignored.");
            }

            return converseRequest;
        }

        private BedrockMessage MapMessage(Message message)
        {
            if (string.Equals(message.Role, MessageRole.Tool, StringComparison.OrdinalIgnoreCase))
            {
                // Tool results ride on a user message in Converse.
                return new BedrockMessage
                {
                    Role = "user",
                    Content = new List<BedrockContentBlock>
                    {
                        new()
                        {
                            ToolResult = new BedrockToolResultBlock
                            {
                                ToolUseId = message.ToolCallId ?? string.Empty,
                                Content = new List<BedrockContentBlock> { MapToolResultContent(message.Content) }
                            }
                        }
                    }
                };
            }

            var role = string.Equals(message.Role, MessageRole.Assistant, StringComparison.OrdinalIgnoreCase)
                ? "assistant"
                : "user";
            var blocks = new List<BedrockContentBlock>();

            var text = ContentHelper.GetContentAsString(message.Content);
            if (!string.IsNullOrEmpty(text))
            {
                blocks.Add(new BedrockContentBlock { Text = text });
            }

            foreach (var image in ContentHelper.ExtractImageUrls(message.Content))
            {
                blocks.Add(MapImage(image));
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    blocks.Add(new BedrockContentBlock
                    {
                        ToolUse = new BedrockToolUseBlock
                        {
                            ToolUseId = toolCall.Id,
                            Name = toolCall.Function.Name,
                            Input = ParseJsonOrWrap(toolCall.Function.Arguments)
                        }
                    });
                }
            }

            return new BedrockMessage { Role = role, Content = blocks };
        }

        private static void EnsureSupportedContentParts(object? content)
        {
            if (content is null or string)
                return;

            if (content is JsonElement root)
            {
                if (root.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var part in root.EnumerateArray())
                {
                    ValidateContentPartType(
                        part.ValueKind == JsonValueKind.Object
                            && part.TryGetProperty("type", out var typeElement)
                            && typeElement.ValueKind == JsonValueKind.String
                                ? typeElement.GetString()
                                : null);
                }
                return;
            }

            if (content is not IEnumerable<object> parts)
            {
                throw new ValidationException(
                    "Bedrock message content must be a string, a JSON array, or typed content parts.");
            }

            foreach (var part in parts)
            {
                var type = part switch
                {
                    TextContentPart => "text",
                    ImageUrlContentPart => "image_url",
                    ProviderContentPart providerPart => providerPart.Type,
                    JsonElement element when element.ValueKind == JsonValueKind.Object
                        && element.TryGetProperty("type", out var typeElement)
                        && typeElement.ValueKind == JsonValueKind.String => typeElement.GetString(),
                    _ => null
                };

                ValidateContentPartType(type);
            }
        }

        private static void ValidateContentPartType(string? type)
        {
            if (type is null)
            {
                throw new ValidationException("Bedrock content parts must include a string type.");
            }

            if (type is not ("text" or "image_url"))
            {
                throw new ValidationException(
                    $"Bedrock does not support content part type '{type}'. Supported types are text and image_url.");
            }
        }

        private static BedrockContentBlock MapToolResultContent(object? content)
        {
            var text = ContentHelper.GetContentAsString(content);
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind is JsonValueKind.Object)
                {
                    return new BedrockContentBlock { Json = document.RootElement.Clone() };
                }
            }
            catch (JsonException)
            {
                // Plain-text tool output.
            }

            return new BedrockContentBlock { Text = text };
        }

        private static BedrockContentBlock MapImage(ImageUrl image)
        {
            // Converse only accepts inline bytes. Data URLs carry them directly; remote URLs would
            // require Conduit to fetch arbitrary content server-side, which this adapter does not do.
            var url = image.Url;
            if (!DataUrl.IsDataUrl(url))
            {
                throw new ValidationException(
                    "Bedrock requires image content to be supplied inline as a data URL; remote image URLs are not supported.");
            }

            if (!DataUrl.TryParse(url, out var dataUrl) || !dataUrl.IsBase64)
            {
                throw new ValidationException("Bedrock image content must be a valid base64 data URL.");
            }

            var format = dataUrl.MediaType.ToLowerInvariant() switch
            {
                "image/jpeg" => "jpeg",
                "image/png" => "png",
                "image/gif" => "gif",
                "image/webp" => "webp",
                _ => throw new ValidationException(
                    $"Bedrock does not support image data URL MIME type '{dataUrl.MediaType}'.")
            };

            return new BedrockContentBlock
            {
                Image = new BedrockImageBlock
                {
                    Format = format,
                    Source = new BedrockImageSource { Bytes = dataUrl.Data }
                }
            };
        }

        private static JsonElement ParseJsonOrWrap(string arguments)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return JsonSerializer.SerializeToElement(
                    new BedrockRawToolArguments { Raw = arguments },
                    ProvidersJsonContext.Default.BedrockRawToolArguments);
            }
        }

        private static BedrockInferenceConfig? MapInferenceConfig(ChatCompletionRequest request)
        {
            var config = new BedrockInferenceConfig
            {
                MaxTokens = request.MaxTokens ?? request.MaxCompletionTokens,
                Temperature = request.Temperature,
                TopP = request.TopP,
                StopSequences = request.Stop is { Count: > 0 } ? request.Stop : null
            };

            return config.MaxTokens is null && config.Temperature is null
                && config.TopP is null && config.StopSequences is null
                ? null
                : config;
        }

        private static BedrockToolConfig? MapToolConfig(ChatCompletionRequest request)
        {
            // OpenAI's tool_choice "none" means the model must not call tools; Converse cannot
            // express that with tools present, so the tools are simply not sent.
            if (request.Tools is not { Count: > 0 }
                || request.ToolChoice?.GetSerializedValue() as string == "none")
            {
                return null;
            }

            var config = new BedrockToolConfig
            {
                Tools = request.Tools.Select(tool => new BedrockTool
                {
                    ToolSpec = new BedrockToolSpec
                    {
                        Name = tool.Function.Name,
                        Description = tool.Function.Description,
                        InputSchema = tool.Function.Parameters is { } parameters
                            ? new BedrockToolInputSchema
                            {
                                Json = ParseJsonNode(parameters)
                            }
                            : null
                    }
                }).ToList()
            };

            config.ToolChoice = MapToolChoice(request.ToolChoice);
            return config;
        }

        private static BedrockToolChoice? MapToolChoice(ToolChoice? toolChoice)
        {
            if (toolChoice is null)
            {
                return null;
            }

            if (toolChoice.GetSerializedValue() is string serialized)
            {
                return serialized switch
                {
                    "auto" => new BedrockToolChoice { Auto = CreateEmptyJsonObject() },
                    "required" => new BedrockToolChoice { Any = CreateEmptyJsonObject() },
                    _ => null
                };
            }

            if (toolChoice.TryGetFunctionName(out var functionName))
            {
                return new BedrockToolChoice { Tool = new BedrockNamedToolChoice { Name = functionName } };
            }

            return null;
        }

        private static JsonElement CreateEmptyJsonObject()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

        private static JsonElement ParseJsonNode(System.Text.Json.Nodes.JsonNode node)
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            return document.RootElement.Clone();
        }

        internal ChatCompletionResponse MapToCoreResponse(BedrockConverseResponse response, string modelId)
        {
            var contentBlocks = response.Output?.Message?.Content ?? new List<BedrockContentBlock>();
            var text = string.Concat(contentBlocks
                .Where(block => !string.IsNullOrEmpty(block.Text))
                .Select(block => block.Text));

            var toolCalls = contentBlocks
                .Where(block => block.ToolUse != null)
                .Select(block => new ToolCall
                {
                    Id = block.ToolUse!.ToolUseId,
                    Function = new FunctionCall
                    {
                        Name = block.ToolUse.Name,
                        Arguments = block.ToolUse.Input.ValueKind == JsonValueKind.Undefined
                            ? "{}"
                            : block.ToolUse.Input.GetRawText()
                    }
                })
                .ToList();

            return new ChatCompletionResponse
            {
                Id = $"bedrock-{Guid.NewGuid():N}",
                Object = "chat.completion",
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = modelId,
                Choices = new List<Choice>
                {
                    new()
                    {
                        Index = 0,
                        FinishReason = MapStopReason(response.StopReason),
                        Message = new Message
                        {
                            Role = MessageRole.Assistant,
                            Content = text.Length > 0 ? text : null,
                            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
                        }
                    }
                },
                Usage = response.Usage is { } usage
                    ? new Usage
                    {
                        PromptTokens = usage.InputTokens,
                        CompletionTokens = usage.OutputTokens,
                        TotalTokens = usage.TotalTokens
                            ?? (usage.InputTokens ?? 0) + (usage.OutputTokens ?? 0)
                    }
                    : null
            };
        }

        internal static string MapStopReason(string? stopReason) => stopReason switch
        {
            "end_turn" or "stop_sequence" => FinishReason.Stop,
            "max_tokens" => FinishReason.Length,
            "tool_use" => FinishReason.ToolCalls,
            "content_filtered" or "guardrail_intervened" => FinishReason.ContentFilter,
            _ => FinishReason.Stop
        };
    }
}
