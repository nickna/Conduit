using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Providers.Bedrock
{
    /// <summary>
    /// Wire models for the Amazon Bedrock Converse and control-plane REST APIs. Bedrock JSON is
    /// camelCase; these serialize with the client's camelCase options and omit nulls.
    /// </summary>
    internal class BedrockConverseRequest
    {
        public List<BedrockMessage> Messages { get; set; } = new();
        public List<BedrockSystemBlock>? System { get; set; }
        public BedrockInferenceConfig? InferenceConfig { get; set; }
        public BedrockToolConfig? ToolConfig { get; set; }

        /// <summary>Model-native pass-through fields (for example Anthropic's <c>top_k</c>).</summary>
        public Dictionary<string, JsonElement>? AdditionalModelRequestFields { get; set; }
    }

    internal class BedrockSystemBlock
    {
        public string? Text { get; set; }
    }

    internal class BedrockMessage
    {
        public string Role { get; set; } = "user";
        public List<BedrockContentBlock> Content { get; set; } = new();
    }

    /// <summary>
    /// A Converse content block. Exactly one member is set per block.
    /// </summary>
    internal class BedrockContentBlock
    {
        public string? Text { get; set; }
        public BedrockImageBlock? Image { get; set; }
        public BedrockToolUseBlock? ToolUse { get; set; }
        public BedrockToolResultBlock? ToolResult { get; set; }
        public JsonElement? Json { get; set; }
    }

    internal class BedrockImageBlock
    {
        public string Format { get; set; } = "png";
        public BedrockImageSource Source { get; set; } = new();
    }

    internal class BedrockImageSource
    {
        /// <summary>Base64-encoded image data (the REST API carries bytes fields as base64 strings).</summary>
        public string? Bytes { get; set; }
    }

    internal class BedrockToolUseBlock
    {
        public string ToolUseId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public JsonElement Input { get; set; }
    }

    internal class BedrockToolResultBlock
    {
        public string ToolUseId { get; set; } = string.Empty;
        public List<BedrockContentBlock> Content { get; set; } = new();
    }

    internal class BedrockInferenceConfig
    {
        public int? MaxTokens { get; set; }
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public List<string>? StopSequences { get; set; }
    }

    internal class BedrockToolConfig
    {
        public List<BedrockTool> Tools { get; set; } = new();
        public BedrockToolChoice? ToolChoice { get; set; }
    }

    internal class BedrockTool
    {
        public BedrockToolSpec ToolSpec { get; set; } = new();
    }

    internal class BedrockToolSpec
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public BedrockToolInputSchema? InputSchema { get; set; }
    }

    internal class BedrockToolInputSchema
    {
        public JsonElement Json { get; set; }
    }

    internal class BedrockRawToolArguments
    {
        public string Raw { get; set; } = string.Empty;
    }

    /// <summary>Exactly one member is set: <c>auto</c>, <c>any</c>, or a named <c>tool</c>.</summary>
    internal class BedrockToolChoice
    {
        public JsonElement? Auto { get; set; }
        public JsonElement? Any { get; set; }
        public BedrockNamedToolChoice? Tool { get; set; }
    }

    internal class BedrockNamedToolChoice
    {
        public string Name { get; set; } = string.Empty;
    }

    internal class BedrockConverseResponse
    {
        public BedrockConverseOutput? Output { get; set; }
        public string? StopReason { get; set; }
        public BedrockUsage? Usage { get; set; }
    }

    internal class BedrockConverseOutput
    {
        public BedrockMessage? Message { get; set; }
    }

    internal class BedrockUsage
    {
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public int? TotalTokens { get; set; }
    }

    // ---- ConverseStream event payloads ----

    internal class BedrockStreamMessageStart
    {
        public string? Role { get; set; }
    }

    internal class BedrockStreamContentBlockStart
    {
        public int ContentBlockIndex { get; set; }
        public BedrockStreamBlockStart? Start { get; set; }
    }

    internal class BedrockStreamBlockStart
    {
        public BedrockStreamToolUseStart? ToolUse { get; set; }
    }

    internal class BedrockStreamToolUseStart
    {
        public string? ToolUseId { get; set; }
        public string? Name { get; set; }
    }

    internal class BedrockStreamContentBlockDelta
    {
        public int ContentBlockIndex { get; set; }
        public BedrockStreamDelta? Delta { get; set; }
    }

    internal class BedrockStreamDelta
    {
        public string? Text { get; set; }
        public BedrockStreamToolUseDelta? ToolUse { get; set; }
        public BedrockStreamReasoningDelta? ReasoningContent { get; set; }
    }

    internal class BedrockStreamToolUseDelta
    {
        /// <summary>A fragment of the tool input JSON, streamed as text.</summary>
        public string? Input { get; set; }
    }

    internal class BedrockStreamReasoningDelta
    {
        public string? Text { get; set; }
    }

    internal class BedrockStreamMessageStop
    {
        public string? StopReason { get; set; }
    }

    internal class BedrockStreamMetadata
    {
        public BedrockUsage? Usage { get; set; }
    }

    // ---- Control plane (ListFoundationModels) ----

    internal class BedrockListFoundationModelsResponse
    {
        public List<BedrockFoundationModelSummary>? ModelSummaries { get; set; }
    }

    internal class BedrockFoundationModelSummary
    {
        public string? ModelId { get; set; }
        public string? ModelName { get; set; }
        public string? ProviderName { get; set; }
        public List<string>? OutputModalities { get; set; }
        public bool? ResponseStreamingSupported { get; set; }
    }

    /// <summary>Bedrock error envelope (<c>{"message": "..."}</c>).</summary>
    internal class BedrockErrorResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
