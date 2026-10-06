using System.Text.Json;

using Microsoft.Extensions.Logging;
using CoreModels = ConduitLLM.Core.Models;
using ConduitLLM.Providers.OpenAI;
using ConduitLLM.Providers.Serialization;
using ProviderHelpers = ConduitLLM.Providers.Helpers;
using ConduitLLM.Providers.Utilities;

namespace ConduitLLM.Providers.OpenAICompatible
{
    /// <summary>
    /// OpenAICompatibleClient partial class containing request/response mapping functionality.
    /// </summary>
    public abstract partial class OpenAICompatibleClient
    {
        /// <summary>
        /// Maps the provider-agnostic request to OpenAI format.
        /// </summary>
        /// <param name="request">The provider-agnostic request.</param>
        /// <returns>An object representing the OpenAI-formatted request.</returns>
        /// <remarks>
        /// This method maps the generic request to the format expected by OpenAI-compatible APIs.
        /// Derived classes can override this method to provide custom mapping.
        /// </remarks>
        protected virtual object MapToOpenAIRequest(CoreModels.ChatCompletionRequest request)
        {
            // Map tools if present
            List<object>? openAiTools = null;
            if (request.Tools != null && request.Tools.Any())
            {
                openAiTools = request.Tools.Select(t =>
                    (object)new Dictionary<string, object?>
                    {
                        ["type"] = t.Type ?? "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = t.Function?.Name ?? "unknown",
                            ["description"] = t.Function?.Description,
                            ["parameters"] = t.Function?.Parameters
                        }
                    }).ToList();
            }

            // Map tool choice if present
            object? openAiToolChoice = null;
            if (request.ToolChoice != null)
            {
                // Use the GetSerializedValue method to get the properly formatted object
                openAiToolChoice = request.ToolChoice;
            }

            // Map messages with their content - handle multimodal content for vision models
            var messages = request.Messages.Select(m =>
            {
                return new OpenAIMessage
                {
                    Role = m.Role,
                    Content = ProviderHelpers.ContentHelper.ShouldPreserveAsArray(m.Content)
                        ? PassThroughContentArray(m.Content)
                        : ProviderHelpers.ContentHelper.IsTextOnly(m.Content)
                            ? ProviderHelpers.ContentHelper.GetContentAsString(m.Content)
                            : MapMultimodalContent(m.Content),
                    Name = m.Name,
                    ToolCalls = m.ToolCalls,
                    ToolCallId = m.ToolCallId,
                    Annotations = m.Annotations is null
                        ? null
                        : JsonSerializer.SerializeToElement(
                            m.Annotations,
                            typeof(List<JsonElement>),
                            ProvidersJsonContext.Default),
                    Audio = m.Audio,
                    Images = m.Images,
                    ReasoningDetails = m.ReasoningDetails,
                    Reasoning = m.Reasoning,
                    ExtensionData = m.ExtensionData
                };
            }).ToList();

            // Create the OpenAI request as a dictionary to support extension data
            var openAiRequest = new Dictionary<string, object?>
            {
                ["model"] = ProviderModelId,  // Always use the provider's model ID, not the alias
                ["messages"] = messages
            };
            
            // Add optional standard parameters
            if (request.MaxTokens != null)
                openAiRequest["max_tokens"] = request.MaxTokens;
            if (request.MaxCompletionTokens != null)
                openAiRequest["max_completion_tokens"] = request.MaxCompletionTokens;
            if (request.Temperature != null)
                openAiRequest["temperature"] = ParameterConverter.ToTemperature(request.Temperature);
            if (request.TopP != null)
                openAiRequest["top_p"] = ParameterConverter.ToProbability(request.TopP, 0.0, 1.0);
            if (request.N != null)
                openAiRequest["n"] = request.N;
            if (request.Stop != null)
                openAiRequest["stop"] = ParameterConverter.ConvertStopSequences(request.Stop);
            if (request.PresencePenalty != null)
                openAiRequest["presence_penalty"] = ParameterConverter.ToProbability(request.PresencePenalty);
            if (request.FrequencyPenalty != null)
                openAiRequest["frequency_penalty"] = ParameterConverter.ToProbability(request.FrequencyPenalty);
            if (request.LogitBias != null)
                openAiRequest["logit_bias"] = ParameterConverter.ConvertLogitBias(request.LogitBias);
            if (request.User != null)
                openAiRequest["user"] = request.User;
            if (request.Seed != null)
                openAiRequest["seed"] = request.Seed;
            if (request.ReasoningEffort != null)
                openAiRequest["reasoning_effort"] = request.ReasoningEffort;
            if (request.ParallelToolCalls != null)
                openAiRequest["parallel_tool_calls"] = request.ParallelToolCalls;
            if (request.Modalities != null)
                openAiRequest["modalities"] = request.Modalities;
            if (request.Audio != null)
                openAiRequest["audio"] = request.Audio;
            if (request.Prediction != null)
                openAiRequest["prediction"] = request.Prediction;
            if (request.Logprobs != null)
                openAiRequest["logprobs"] = request.Logprobs;
            if (request.TopLogprobs != null)
                openAiRequest["top_logprobs"] = request.TopLogprobs;
            if (request.ServiceTier != null)
                openAiRequest["service_tier"] = request.ServiceTier;
            if (request.Store != null)
                openAiRequest["store"] = request.Store;
            if (request.Metadata != null)
                openAiRequest["metadata"] = request.Metadata;
            if (request.SafetyIdentifier != null)
                openAiRequest["safety_identifier"] = request.SafetyIdentifier;
            if (request.Verbosity != null)
                openAiRequest["verbosity"] = request.Verbosity;
            if (request.Moderation != null)
                openAiRequest["moderation"] = request.Moderation;
            if (request.PromptCacheKey != null)
                openAiRequest["prompt_cache_key"] = request.PromptCacheKey;
            if (request.PromptCacheOptions != null)
                openAiRequest["prompt_cache_options"] = request.PromptCacheOptions;
            if (request.PromptCacheRetention != null)
                openAiRequest["prompt_cache_retention"] = request.PromptCacheRetention;
            if (request.WebSearchOptions != null)
                openAiRequest["web_search_options"] = request.WebSearchOptions;
            if (openAiTools != null)
                openAiRequest["tools"] = openAiTools;
            if (openAiToolChoice != null)
                openAiRequest["tool_choice"] = openAiToolChoice;
            if (request.Functions != null)
                openAiRequest["functions"] = request.Functions;
            if (request.FunctionCall != null)
                openAiRequest["function_call"] = request.FunctionCall;
            // Only send ResponseFormat if explicitly requested and not "text" (default)
            // Some providers like SambaNova don't support response_format with type "text"
            if (request.ResponseFormat != null && request.ResponseFormat.Type != "text")
            {
                // For json_schema, forward the Core ResponseFormat as-is so the schema payload
                // ({ type, json_schema: { name, strict, schema } }) reaches the provider. For other
                // types (e.g. json_object) send only { type } to match providers that reject extras.
                openAiRequest["response_format"] =
                    request.ResponseFormat.Type == "json_schema" && request.ResponseFormat.JsonSchema != null
                        ? (object)request.ResponseFormat
                        : new ResponseFormat { Type = request.ResponseFormat.Type ?? "text" };
            }
            // Unified reasoning config — only forwarded when the caller set it (providers that don't
            // support it simply ignore/return an error, same as any explicit unsupported parameter).
            if (request.Reasoning != null)
                openAiRequest["reasoning"] = request.Reasoning;
            if (request.Stream != null)
                openAiRequest["stream"] = request.Stream;
            if (request.StreamOptions != null)
                openAiRequest["stream_options"] = new Dictionary<string, object?>
                {
                    ["include_usage"] = request.StreamOptions.IncludeUsage
                };

            // Pass through any extension data (model-specific parameters)
            if (request.ExtensionData != null)
            {
                Logger.LogDebug("Forwarding {Count} extension data parameters", request.ExtensionData.Count);
                foreach (var kvp in request.ExtensionData)
                {
                    // Don't override standard parameters
                    if (!openAiRequest.ContainsKey(kvp.Key))
                    {
                        // Convert JsonElement to actual value for proper serialization
                        openAiRequest[kvp.Key] = ConvertJsonElement(kvp.Value);
                    }
                }
            }
            
            return openAiRequest;
        }

        /// <summary>
        /// Maps multimodal content to the format expected by OpenAI's API.
        /// </summary>
        /// <param name="content">The content object which may contain text and images</param>
        /// <returns>A properly formatted list of content parts for OpenAI</returns>
        protected virtual object MapMultimodalContent(object? content)
        {
            if (content == null)
                return "";

            if (content is string textContent)
                return textContent;

            // Create a list to hold the formatted content parts
            var contentParts = new List<object>();

            // Extract text parts
            var textParts = ProviderHelpers.ContentHelper.ExtractMultimodalContent(content);
            foreach (var text in textParts)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    contentParts.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = text
                    });
                }
            }

            // Extract image URLs
            var imageUrls = ProviderHelpers.ContentHelper.ExtractImageUrls(content);
            foreach (var imageUrl in imageUrls)
            {
                contentParts.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = imageUrl.Url,
                        ["detail"] = string.IsNullOrEmpty(imageUrl.Detail) ? "auto" : imageUrl.Detail
                    }
                });
            }

            foreach (var videoUrl in ProviderHelpers.ContentHelper.ExtractVideoUrls(content))
            {
                contentParts.Add(new Dictionary<string, object?>
                {
                    ["type"] = "video_url",
                    ["video_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = videoUrl.Url,
                        ["detail"] = videoUrl.Detail,
                        ["max_frames"] = videoUrl.MaxFrames,
                        ["sample_rate"] = videoUrl.SampleRate,
                        ["start_time"] = videoUrl.StartTime,
                        ["end_time"] = videoUrl.EndTime
                    }
                });
            }

            // If no parts were added, return an empty string
            if (contentParts.Count == 0)
                return "";

            return contentParts;
        }

        /// <summary>
        /// Passes through content array elements preserving all properties (including cache_control).
        /// </summary>
        /// <param name="content">The content object which should be a JSON array</param>
        /// <returns>A list of dictionaries preserving all properties on each content block</returns>
        protected virtual object PassThroughContentArray(object? content)
        {
            if (content == null)
                return "";

            if (content is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                // Convert each array element to a dictionary preserving all properties
                var contentParts = new List<object>();
                foreach (var element in jsonElement.EnumerateArray())
                {
                    if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var dict = new Dictionary<string, object?>();
                        foreach (var prop in element.EnumerateObject())
                        {
                            dict[prop.Name] = ConvertJsonElement(prop.Value);
                        }
                        contentParts.Add(dict);
                    }
                }
                return contentParts.Count > 0 ? contentParts : (object)"";
            }

            // Handle IEnumerable<object> of dictionaries (from PromptCacheInjectionService)
            if (content is IEnumerable<object> contentList)
            {
                var parts = new List<object>();
                foreach (var item in contentList)
                {
                    if (item is IDictionary<string, object?> dictNullable)
                    {
                        parts.Add(dictNullable);
                    }
                    else if (item is IDictionary<string, object> dictNonNull)
                    {
                        parts.Add(dictNonNull);
                    }
                    else
                    {
                        parts.Add(item);
                    }
                }
                if (parts.Count > 0)
                    return parts;
            }

            // Value-type collections are not covariant to IEnumerable<object>.
            if (content is IEnumerable<JsonElement> jsonElements)
            {
                var parts = jsonElements.Select(element => (object)element.Clone()).ToList();
                if (parts.Count > 0)
                    return parts;
            }

            return MapMultimodalContent(content);
        }

        /// <summary>
        /// Maps the OpenAI response to provider-agnostic format.
        /// </summary>
        /// <param name="responseObj">The response from the OpenAI API.</param>
        /// <param name="originalModelAlias">The original model alias from the request.</param>
        /// <returns>A provider-agnostic chat completion response.</returns>
        /// <remarks>
        /// This method maps the OpenAI-formatted response to the generic format used by the application.
        /// Derived classes can override this method to provide custom mapping.
        /// </remarks>
        protected virtual CoreModels.ChatCompletionResponse MapFromOpenAIResponse(
            object responseObj,
            string? originalModelAlias)
        {
            if (responseObj == null)
            {
                Logger.LogError("Received null response from OpenAI-compatible provider");
                return CreateEmptyResponse(originalModelAlias);
            }

            // Cast to the strongly-typed response
            var response = responseObj as OpenAIChatCompletionResponse;
            if (response == null)
            {
                Logger.LogError("Response is not of expected type OpenAIChatCompletionResponse. Type: {Type}", 
                    responseObj.GetType()?.FullName ?? "null");
                return CreateEmptyResponse(originalModelAlias);
            }

            try
            {
                // Map the strongly-typed response
                var mapped = new CoreModels.ChatCompletionResponse
                {
                    Id = response.Id ?? Guid.NewGuid().ToString(),
                    Object = response.Object ?? "chat.completion",
                    Created = response.Created ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model = originalModelAlias ?? response.Model ?? "unknown",
                    Choices = response.Choices?.Select(c => new CoreModels.Choice
                    {
                        Index = c.Index,
                        FinishReason = c.FinishReason ?? "stop",
                        Message = c.Message != null ? new CoreModels.Message
                        {
                            Role = c.Message.Role ?? "assistant",
                            Content = c.Message.Content,
                            Name = c.Message.Name,
                            ToolCalls = c.Message.ToolCalls,
                            ToolCallId = c.Message.ToolCallId,
                            Annotations = c.Message.Annotations is { ValueKind: System.Text.Json.JsonValueKind.Array } annotations
                                ? annotations.EnumerateArray().Select(annotation => annotation.Clone()).ToList()
                                : null,
                            Audio = c.Message.Audio,
                            Images = c.Message.Images,
                            ReasoningDetails = c.Message.ReasoningDetails,
                            Reasoning = c.Message.Reasoning,
                            ExtensionData = c.Message.ExtensionData
                        } : new CoreModels.Message
                        {
                            Role = "assistant",
                            Content = null
                        }
                    }).ToList() ?? new List<CoreModels.Choice>(),
                    Usage = response.Usage != null ? MapUsageFromOpenAI(response.Usage) : null,
                    SystemFingerprint = response.SystemFingerprint,
                    ServiceTier = response.ServiceTier,
                    Moderation = response.Moderation,
                    Seed = response.Seed,
                    OriginalModelAlias = originalModelAlias,
                    ExtensionData = response.ExtensionData
                };
                mapped.ProviderToolUsage = MapGroqHostedToolUsage(response.GroqExtension);
                return mapped;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error mapping OpenAI response: {Message}", ex.Message);
                return CreateEmptyResponse(originalModelAlias);
            }
        }

        internal static CoreModels.ProviderToolUsage? MapGroqHostedToolUsage(
            System.Text.Json.JsonElement? groqExtension)
        {
            if (groqExtension is not { ValueKind: System.Text.Json.JsonValueKind.Object } extension ||
                !extension.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            var tools = new List<CoreModels.ProviderToolUsageItem>();
            foreach (var toolName in new[] { "code_interpreter", "browser_search", "python" })
            {
                if (!usage.TryGetProperty(toolName, out var countElement) ||
                    !countElement.TryGetInt32(out var count) || count <= 0)
                {
                    continue;
                }

                decimal? durationSeconds = null;
                var durationName = $"{toolName}_duration_seconds";
                if (usage.TryGetProperty(durationName, out var durationElement) &&
                    durationElement.TryGetDecimal(out var duration))
                {
                    durationSeconds = duration;
                }

                tools.Add(new CoreModels.ProviderToolUsageItem
                {
                    ToolName = toolName == "python" ? "code_interpreter" : toolName,
                    Count = count,
                    DurationSeconds = durationSeconds
                });
            }

            return tools.Count == 0 ? null : new CoreModels.ProviderToolUsage { Tools = tools };
        }

        /// <summary>
        /// Maps an OpenAIUsage record to the provider-agnostic Usage model,
        /// extracting cached token counts from provider-specific extension data.
        /// </summary>
        /// <param name="openAiUsage">The OpenAI usage data.</param>
        /// <returns>A provider-agnostic Usage object with cached token fields populated.</returns>
        private static CoreModels.Usage MapUsageFromOpenAI(OpenAIUsage openAiUsage)
        {
            var usage = new CoreModels.Usage
            {
                PromptTokens = openAiUsage.PromptTokens,
                CompletionTokens = openAiUsage.CompletionTokens,
                TotalTokens = openAiUsage.TotalTokens,
                ReasoningTokens = openAiUsage.ReasoningTokens
            };

            if (openAiUsage.ExtensionData != null)
            {
                PopulateProviderUsageFields(openAiUsage.ExtensionData, usage);
            }

            return usage;
        }

        /// <summary>
        /// Populates provider-agnostic <see cref="CoreModels.Usage"/> fields (cached tokens,
        /// cache-write tokens, and provider-reported cost) from a provider usage extension-data
        /// dictionary. Shared by the non-streaming mapper (<see cref="MapUsageFromOpenAI"/>) and the
        /// streaming post-processor so both paths capture the same provider-specific fields.
        /// </summary>
        /// <remarks>
        /// Uses null-coalescing assignment so the first non-null value wins; callers pass a freshly
        /// constructed usage on the non-streaming path, so this is equivalent to plain assignment there.
        /// </remarks>
        private static void PopulateProviderUsageFields(
            IDictionary<string, System.Text.Json.JsonElement> extensionData,
            CoreModels.Usage usage)
        {
            // OpenAI / OpenRouter format: prompt_tokens_details.{cached_tokens, cache_write_tokens}
            if (extensionData.TryGetValue("prompt_tokens_details", out var promptDetails) &&
                promptDetails.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (promptDetails.TryGetProperty("cached_tokens", out var cachedTokens) &&
                    cachedTokens.TryGetInt32(out var cached))
                {
                    usage.CachedInputTokens ??= cached;
                }

                if (promptDetails.TryGetProperty("cache_write_tokens", out var cacheWriteTokens) &&
                    cacheWriteTokens.TryGetInt32(out var cacheWritten))
                {
                    usage.CachedWriteTokens ??= cacheWritten;
                }
            }

            // Anthropic format: cache_read_input_tokens / cache_creation_input_tokens
            if (extensionData.TryGetValue("cache_read_input_tokens", out var cacheRead) &&
                cacheRead.TryGetInt32(out var cacheReadCount))
            {
                usage.CachedInputTokens ??= cacheReadCount;
            }

            if (extensionData.TryGetValue("cache_creation_input_tokens", out var cacheWrite) &&
                cacheWrite.TryGetInt32(out var cacheWriteCount))
            {
                usage.CachedWriteTokens ??= cacheWriteCount;
            }

            // Deepseek format: prompt_cache_hit_tokens
            if (extensionData.TryGetValue("prompt_cache_hit_tokens", out var cacheHit) &&
                cacheHit.TryGetInt32(out var cacheHitCount))
            {
                usage.CachedInputTokens ??= cacheHitCount;
            }

            // OpenRouter (and compatible): usage.cost = actual credits charged (USD).
            if (extensionData.TryGetValue("cost", out var cost) &&
                cost.ValueKind == System.Text.Json.JsonValueKind.Number &&
                cost.TryGetDecimal(out var costValue))
            {
                usage.ProviderReportedCostUsd ??= costValue;
            }
        }

        /// <summary>
        /// Creates an empty chat completion response for error cases.
        /// </summary>
        /// <param name="originalModelAlias">The original model alias from the request.</param>
        /// <returns>An empty chat completion response.</returns>
        private CoreModels.ChatCompletionResponse CreateEmptyResponse(string? originalModelAlias)
        {
            return new CoreModels.ChatCompletionResponse
            {
                Id = Guid.NewGuid().ToString(),
                Object = "chat.completion",
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = originalModelAlias ?? ProviderModelId,
                Choices = new List<CoreModels.Choice>(),
                OriginalModelAlias = originalModelAlias
            };
        }

        /// <summary>
        /// Post-processes a deserialized Usage object to extract cached token counts and the
        /// provider-reported cost from provider-specific extension data, then strips the raw
        /// <c>cost</c> key so it is never re-serialized back to API clients.
        /// Call this after deserializing a Usage object from provider JSON (e.g. streaming chunks).
        /// </summary>
        /// <param name="usage">The deserialized Usage object to post-process.</param>
        internal static void ExtractProviderUsageFromExtensionData(CoreModels.Usage? usage)
        {
            if (usage?.ExtensionData == null)
                return;

            PopulateProviderUsageFields(usage.ExtensionData, usage);

            // The provider-reported cost is captured onto ProviderReportedCostUsd (server-only) above.
            // Remove it from ExtensionData so the operator's upstream cost is never leaked back to API
            // clients when the chunk/response is re-serialized.
            usage.ExtensionData.Remove("cost");
        }

        private static object? ConvertJsonElement(System.Text.Json.JsonElement element) =>
            ConduitLLM.Functions.Utilities.JsonElementConverter.ConvertJsonElement(element);

    }
}
