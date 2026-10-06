using System.Text.Json.Serialization;
using System.Text.Json;

namespace ConduitLLM.Providers.OpenAI
{
    // Internal models mirroring OpenAI's /v1/chat/completions structure
    // See: https://platform.openai.com/docs/api-reference/chat/create

    internal sealed class OpenAIMessage
    {
        [JsonPropertyName("role")]
        public string? Role { get; init; } // "system", "user", "assistant", "tool"

        [JsonPropertyName("content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Content { get; init; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; init; }

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ConduitLLM.Core.Models.ToolCall>? ToolCalls { get; init; }

        [JsonPropertyName("tool_call_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("annotations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Annotations { get; init; }

        [JsonPropertyName("audio")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Audio { get; init; }

        [JsonPropertyName("images")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Images { get; init; }

        [JsonPropertyName("reasoning_details")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? ReasoningDetails { get; init; }

        [JsonPropertyName("reasoning")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reasoning { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        public OpenAIMessage WithContent(object? content) => new()
        {
            Role = Role,
            Content = content,
            Name = Name,
            ToolCalls = ToolCalls,
            ToolCallId = ToolCallId,
            Annotations = Annotations,
            Audio = Audio,
            Images = Images,
            ReasoningDetails = ReasoningDetails,
            Reasoning = Reasoning,
            ExtensionData = ExtensionData
        };
    }

    internal sealed class OpenAIChatCompletionResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("object")]
        public string? Object { get; init; } // e.g., "chat.completion"

        [JsonPropertyName("created")]
        [JsonConverter(typeof(FlexibleTimestampConverter))]
        public long? Created { get; init; } // Unix timestamp

        [JsonPropertyName("model")]
        public string? Model { get; init; } // Model used

        [JsonPropertyName("choices")]
        public List<OpenAIChoice>? Choices { get; init; }

        [JsonPropertyName("usage")]
        public OpenAIUsage? Usage { get; init; }

        [JsonPropertyName("system_fingerprint")]
        public string? SystemFingerprint { get; init; }

        [JsonPropertyName("service_tier")]
        public string? ServiceTier { get; init; }

        [JsonPropertyName("moderation")]
        public System.Text.Json.JsonElement? Moderation { get; init; }

        [JsonPropertyName("seed")]
        public int? Seed { get; init; }

        [JsonPropertyName("x_groq")]
        public System.Text.Json.JsonElement? GroqExtension { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    internal record OpenAIChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("message")]
        public OpenAIMessage? Message { get; init; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; init; } // e.g., "stop", "length", "tool_calls"

        // Optional logprobs field
    }

    internal sealed class OpenAIUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; init; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; init; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; init; }

        [JsonPropertyName("reasoning_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ReasoningTokens { get; init; }

        /// <summary>
        /// Captures provider-specific usage fields not explicitly modeled
        /// (e.g., prompt_tokens_details, cache_creation_input_tokens, prompt_cache_hit_tokens).
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
    }

    // --- Internal Models for Model Listing ---
    // See: https://platform.openai.com/docs/api-reference/models/list

    internal record ListModelsResponse
    {
        [JsonPropertyName("object")]
        public string Object { get; init; } = "list"; // Expected value

        [JsonPropertyName("data")]
        public required List<OpenAIModelData> Data { get; init; }
    }

    internal record OpenAIModelData
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; } // The model ID

        [JsonPropertyName("object")]
        public string Object { get; init; } = "model"; // Expected value

        [JsonPropertyName("created")]
        public long Created { get; init; } // Unix timestamp

        [JsonPropertyName("owned_by")]
        public required string OwnedBy { get; init; } // e.g., "openai", "system", "user"
    }

    // --- Internal Models for Embeddings ---
    // See: https://platform.openai.com/docs/api-reference/embeddings/create

    internal record EmbeddingRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required object Input { get; init; } // Can be string or array of strings

        [JsonPropertyName("encoding_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EncodingFormat { get; init; } // Default is "float"

        [JsonPropertyName("dimensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Dimensions { get; init; }

        [JsonPropertyName("user")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? User { get; init; }
    }

    internal record EmbeddingResponse
    {
        [JsonPropertyName("object")]
        public string Object { get; init; } = "list";

        [JsonPropertyName("data")]
        public required List<EmbeddingDataItem> Data { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("usage")]
        public required OpenAIUsage Usage { get; init; }
    }

    internal record EmbeddingDataItem
    {
        [JsonPropertyName("object")]
        public string Object { get; init; } = "embedding";

        [JsonPropertyName("embedding")]
        public required IReadOnlyList<float> Embedding { get; init; }

        [JsonPropertyName("index")]
        public int Index { get; init; }
    }

    // --- Internal Models for Image Generation ---
    // See: https://platform.openai.com/docs/api-reference/images/create

    internal record ImageGenerationResponse
    {
        [JsonPropertyName("created")]
        public long Created { get; init; }

        [JsonPropertyName("data")]
        public List<ImageData>? Data { get; init; }
    }

    internal record ImageData
    {
        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Url { get; init; }

        [JsonPropertyName("b64_json")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? B64Json { get; init; }

        [JsonPropertyName("revised_prompt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RevisedPrompt { get; init; }
    }

    internal record ResponseFormat
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "text"; // Either "text" or "json_object"
    }
}
