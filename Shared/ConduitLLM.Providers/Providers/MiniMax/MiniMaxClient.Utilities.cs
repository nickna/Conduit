using System.Text.Json;

using ConduitLLM.Core.Models;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.MiniMax
{
    /// <summary>
    /// MiniMaxClient partial class containing utility and helper methods.
    /// </summary>
    public partial class MiniMaxClient
    {
        private List<MiniMaxMessage> ConvertMessages(List<Message> messages, bool includeNames = false)
        {
            var miniMaxMessages = new List<MiniMaxMessage>();
            
            foreach (var message in messages)
            {
                var miniMaxMessage = new MiniMaxMessage
                {
                    Role = message.Role,
                    Content = ConvertMessageContent(message.Content ?? string.Empty)
                };

                // The v2 streaming API requires name fields
                if (includeNames)
                {
                    miniMaxMessage.Name = message.Role switch
                    {
                        "system" => "MiniMax AI",
                        "user" => "user", 
                        "assistant" => "assistant",
                        _ => message.Role
                    };
                }
                
                if (message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Any())
                {
                    // MiniMax uses function_call format, convert from tool_calls
                    var firstToolCall = message.ToolCalls[0];
                    if (firstToolCall.Function != null)
                    {
                        miniMaxMessage.FunctionCall = new MiniMaxFunctionCall
                        {
                            Name = firstToolCall.Function.Name,
                            Arguments = firstToolCall.Function.Arguments
                        };
                    }
                }
                
                miniMaxMessages.Add(miniMaxMessage);
            }
            
            return miniMaxMessages;
        }

        private object ConvertMessageContent(object content)
        {
            if (content is string stringContent)
            {
                return stringContent;
            }
            else if (content is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
            {
                var miniMaxParts = new List<object>();
                foreach (var element in jsonElement.EnumerateArray())
                {
                    if (element.TryGetProperty("type", out var typeElement))
                    {
                        var type = typeElement.GetString();
                        if (type == "text" && element.TryGetProperty("text", out var textElement))
                        {
                            miniMaxParts.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = textElement.GetString()
                            });
                        }
                        else if (type == "image_url" && element.TryGetProperty("image_url", out var imageElement) &&
                                 imageElement.TryGetProperty("url", out var urlElement))
                        {
                            miniMaxParts.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new Dictionary<string, string?>
                                {
                                    ["url"] = urlElement.GetString()
                                }
                            });
                        }
                    }
                }
                return miniMaxParts;
            }
            else if (content is List<object> contentParts)
            {
                // Handle if content is already a list of objects
                return contentParts;
            }
            
            return content;
        }

        private List<MiniMaxTool>? ConvertTools(List<Tool>? tools)
        {
            if (tools == null || !tools.Any())
                return null;

            var miniMaxTools = new List<MiniMaxTool>();
            foreach (var tool in tools)
            {
                if (tool.Type == "function" && tool.Function != null)
                {
                    miniMaxTools.Add(new MiniMaxTool
                    {
                        Type = "function",
                        Function = new MiniMaxFunctionDefinition
                        {
                            Name = tool.Function.Name,
                            Description = tool.Function.Description,
                            Parameters = tool.Function.Parameters
                        }
                    });
                }
            }

            return miniMaxTools.Any() ? miniMaxTools : null;
        }

        private object? ConvertToolChoice(ToolChoice? toolChoice)
        {
            if (toolChoice == null)
                return null;

            // Get the serialized value from ToolChoice
            var serializedValue = toolChoice.GetSerializedValue();
            
            // If it's already a string (like "auto", "none"), return it directly
            if (serializedValue is string stringChoice)
            {
                return stringChoice;
            }
            
            // Otherwise, it's a function choice object, return it as-is
            // MiniMax expects the same format as OpenAI
            return serializedValue;
        }

        private ChatCompletionResponse ConvertToCoreResponse(MiniMaxChatCompletionResponse miniMaxResponse, string modelId)
        {
            Logger.LogDebug("Converting MiniMax response: Id={Id}, ChoiceCount={ChoiceCount}, BaseResp={BaseResp}", 
                miniMaxResponse.Id, miniMaxResponse.Choices?.Count ?? 0, miniMaxResponse.BaseResp?.StatusCode ?? 0);
            
            var response = new ChatCompletionResponse
            {
                Id = miniMaxResponse.Id ?? Guid.NewGuid().ToString(),
                Object = "chat.completion",
                Created = miniMaxResponse.Created ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = modelId,
                Choices = new List<Choice>()
            };

            if (miniMaxResponse.Choices != null)
            {
                foreach (var choice in miniMaxResponse.Choices)
                {
                    Logger.LogDebug("MiniMax choice: Index={Index}, Role={Role}, Content={Content}, FinishReason={FinishReason}", 
                        choice.Index, choice.Message?.Role, choice.Message?.Content?.ToString()?.Substring(0, Math.Min(50, choice.Message?.Content?.ToString()?.Length ?? 0)), choice.FinishReason);
                    
                    response.Choices.Add(new Choice
                    {
                        Index = choice.Index,
                        Message = new Message
                        {
                            Role = choice.Message?.Role ?? "assistant",
                            // MiniMax sometimes returns content in ReasoningContent field instead of Content
                            // This appears to be for models that support reasoning tokens (similar to OpenAI o1)
                            // Check if Content is null, empty string, or whitespace
                            Content = !string.IsNullOrWhiteSpace(choice.Message?.Content?.ToString()) 
                                ? choice.Message.Content.ToString()!
                                : (choice.Message?.ReasoningContent ?? string.Empty),
                            ToolCalls = ConvertFunctionCallToToolCalls(choice.Message?.FunctionCall)
                        },
                        FinishReason = choice.FinishReason ?? "stop"
                    });
                }
            }

            if (miniMaxResponse.Usage != null)
            {
                response.Usage = new Usage
                {
                    PromptTokens = miniMaxResponse.Usage.PromptTokens,
                    CompletionTokens = miniMaxResponse.Usage.CompletionTokens,
                    TotalTokens = miniMaxResponse.Usage.TotalTokens
                };
            }

            return response;
        }

        private List<ToolCall>? ConvertFunctionCallToToolCalls(MiniMaxFunctionCall? functionCall)
        {
            if (functionCall == null)
                return null;

            return new List<ToolCall>
            {
                new ToolCall
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = functionCall.Name,
                        Arguments = functionCall.Arguments
                    }
                }
            };
        }

        private static string MapSizeToAspectRatio(string? size)
        {
            return size switch
            {
                "1792x1024" => "16:9",
                "1024x1792" => "9:16",
                "1024x1024" => "1:1",
                "512x512" => "1:1",
                "2048x2048" => "1:1",
                _ => "1:1" // Default to square
            };
        }

        private static string MapSizeToResolution(string? size)
        {
            return size switch
            {
                "1920x1080" => "1080P",
                "1280x720" => "768P",  // MiniMax uses 768P for HD
                "720x480" => "768P",   // Map SD to 768P
                "720x1280" => "768P",  // Portrait HD
                "1080x1920" => "1080P", // Portrait Full HD
                _ => "768P" // Default to 768P (HD)
            };
        }
    }
}
