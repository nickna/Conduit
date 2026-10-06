namespace ConduitLLM.Providers.MiniMax
{
    /// <summary>
    /// MiniMaxClient partial class containing model definitions.
    /// </summary>
    public partial class MiniMaxClient
    {
        private class MiniMaxChatCompletionRequest
        {
            [System.Text.Json.Serialization.JsonPropertyName("model")]
            public string Model { get; set; } = "MiniMax-M1";

            [System.Text.Json.Serialization.JsonPropertyName("messages")]
            public List<MiniMaxMessage> Messages { get; set; } = new();

            [System.Text.Json.Serialization.JsonPropertyName("stream")]
            public bool Stream { get; set; } = false;

            [System.Text.Json.Serialization.JsonPropertyName("max_tokens")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public int? MaxTokens { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("temperature")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public double? Temperature { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("top_p")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public double? TopP { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("tools")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public List<MiniMaxTool>? Tools { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("tool_choice")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public object? ToolChoice { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("reply_constraints")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public ReplyConstraints? ReplyConstraints { get; set; }
        }

        private class MiniMaxMessage
        {
            [System.Text.Json.Serialization.JsonPropertyName("role")]
            public string Role { get; set; } = string.Empty;

            [System.Text.Json.Serialization.JsonPropertyName("content")]
            public object Content { get; set; } = string.Empty;

            [System.Text.Json.Serialization.JsonPropertyName("name")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? Name { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("audio_content")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? AudioContent { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("reasoning_content")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? ReasoningContent { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("function_call")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public MiniMaxFunctionCall? FunctionCall { get; set; }
        }

        private class ReplyConstraints
        {
            [System.Text.Json.Serialization.JsonPropertyName("guidance_type")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? GuidanceType { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("json_schema")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public object? JsonSchema { get; set; }
        }

        private class MiniMaxChatCompletionResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public string? Id { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("created")]
            public long? Created { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("model")]
            public string? Model { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("object")]
            public string? Object { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("choices")]
            public List<MiniMaxChoice>? Choices { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("usage")]
            public MiniMaxUsage? Usage { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("base_resp")]
            public BaseResponse? BaseResp { get; set; }

            // MiniMax specific fields
            [System.Text.Json.Serialization.JsonPropertyName("input_sensitive")]
            public bool? InputSensitive { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("output_sensitive")]
            public bool? OutputSensitive { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("input_sensitive_type")]
            public int? InputSensitiveType { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("output_sensitive_type")]
            public int? OutputSensitiveType { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("output_sensitive_int")]
            public int? OutputSensitiveInt { get; set; }
        }

        private class MiniMaxChoice
        {
            [System.Text.Json.Serialization.JsonPropertyName("index")]
            public int Index { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("message")]
            public MiniMaxMessage? Message { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("finish_reason")]
            public string? FinishReason { get; set; }
        }

        private class MiniMaxStreamChunk
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public string? Id { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("created")]
            public long? Created { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("model")]
            public string? Model { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("object")]
            public string? Object { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("choices")]
            public List<MiniMaxStreamChoice>? Choices { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("usage")]
            public MiniMaxUsage? Usage { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("base_resp")]
            public BaseResponse? BaseResp { get; set; }

            // MiniMax specific fields
            [System.Text.Json.Serialization.JsonPropertyName("input_sensitive")]
            public bool? InputSensitive { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("output_sensitive")]
            public bool? OutputSensitive { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("input_sensitive_type")]
            public int? InputSensitiveType { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("output_sensitive_type")]
            public int? OutputSensitiveType { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("output_sensitive_int")]
            public int? OutputSensitiveInt { get; set; }
        }

        private class MiniMaxStreamChoice
        {
            [System.Text.Json.Serialization.JsonPropertyName("index")]
            public int Index { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("delta")]
            public MiniMaxDelta? Delta { get; set; }

            // DEVIATION FROM OPENAI SPEC: MiniMax sends a non-standard final chunk that includes
            // a complete 'message' field instead of using 'delta' consistently throughout the stream.
            // OpenAI spec requires all streaming chunks to use 'delta' fields only.
            // MiniMax's final chunk has:
            // - object: "chat.completion" (should be "chat.completion.chunk")
            // - message: {complete message} (should use delta with empty content)
            // This violates the OpenAI streaming protocol but must be handled for compatibility.
            [System.Text.Json.Serialization.JsonPropertyName("message")]
            public MiniMaxMessage? Message { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("finish_reason")]
            public string? FinishReason { get; set; }
        }

        private class MiniMaxDelta
        {
            [System.Text.Json.Serialization.JsonPropertyName("role")]
            public string? Role { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("content")]
            public string? Content { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string? Name { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("audio_content")]
            public string? AudioContent { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("reasoning_content")]
            public string? ReasoningContent { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("function_call")]
            public MiniMaxFunctionCall? FunctionCall { get; set; }
        }

        private class MiniMaxUsage
        {
            [System.Text.Json.Serialization.JsonPropertyName("prompt_tokens")]
            public int PromptTokens { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("completion_tokens")]
            public int CompletionTokens { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("total_tokens")]
            public int TotalTokens { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("total_characters")]
            public int? TotalCharacters { get; set; }
        }

        private class MiniMaxImageGenerationRequest
        {
            [System.Text.Json.Serialization.JsonPropertyName("model")]
            public string Model { get; set; } = "image-01";

            [System.Text.Json.Serialization.JsonPropertyName("prompt")]
            public string Prompt { get; set; } = string.Empty;

            [System.Text.Json.Serialization.JsonPropertyName("aspect_ratio")]
            public string AspectRatio { get; set; } = "1:1";

            [System.Text.Json.Serialization.JsonPropertyName("response_format")]
            public string ResponseFormat { get; set; } = "url";

            [System.Text.Json.Serialization.JsonPropertyName("n")]
            public int N { get; set; } = 1;

            [System.Text.Json.Serialization.JsonPropertyName("prompt_optimizer")]
            public bool PromptOptimizer { get; set; } = true;
        }

        private class MiniMaxImageGenerationResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public string? Id { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("data")]
            public MiniMaxImageResponseData? Data { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("metadata")]
            public object? Metadata { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("base_resp")]
            public BaseResponse? BaseResp { get; set; }
        }

        private class MiniMaxImageResponseData
        {
            [System.Text.Json.Serialization.JsonPropertyName("image_urls")]
            public List<string>? ImageUrls { get; set; }
            
            [System.Text.Json.Serialization.JsonPropertyName("images")]
            public List<MiniMaxImageData>? Images { get; set; }
            
            [System.Text.Json.Serialization.JsonPropertyName("image_base64")]
            public List<string>? ImageBase64 { get; set; }
        }
        
        private class MiniMaxImageData
        {
            [System.Text.Json.Serialization.JsonPropertyName("b64")]
            public string? B64 { get; set; }
        }

        private class BaseResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("status_code")]
            public int StatusCode { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("status_msg")]
            public string? StatusMsg { get; set; }
        }

        private class MiniMaxTool
        {
            [System.Text.Json.Serialization.JsonPropertyName("type")]
            public string Type { get; set; } = "function";

            [System.Text.Json.Serialization.JsonPropertyName("function")]
            public MiniMaxFunctionDefinition? Function { get; set; }
        }

        private class MiniMaxFunctionDefinition
        {
            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [System.Text.Json.Serialization.JsonPropertyName("description")]
            public string? Description { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("parameters")]
            public object? Parameters { get; set; }
        }

        private class MiniMaxFunctionCall
        {
            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [System.Text.Json.Serialization.JsonPropertyName("arguments")]
            public string Arguments { get; set; } = string.Empty;
        }

        private class MiniMaxVideoGenerationResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("task_id")]
            public string? TaskId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("status")]
            public string? Status { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("base_resp")]
            public BaseResponse? BaseResp { get; set; }
        }

        private class MiniMaxVideoStatusResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("task_id")]
            public string? TaskId { get; set; }
            
            [System.Text.Json.Serialization.JsonPropertyName("status")]
            public string? Status { get; set; }
            
            [System.Text.Json.Serialization.JsonPropertyName("file_id")]
            public string? FileId { get; set; }
            
            [System.Text.Json.Serialization.JsonPropertyName("video_width")]
            public int VideoWidth { get; set; }
            
            [System.Text.Json.Serialization.JsonPropertyName("video_height")]
            public int VideoHeight { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("video")]
            public MiniMaxVideoData? Video { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("base_resp")]
            public BaseResponse? BaseResp { get; set; }
        }

        private class MiniMaxVideoData
        {
            [System.Text.Json.Serialization.JsonPropertyName("url")]
            public string? Url { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("duration")]
            public double? Duration { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("resolution")]
            public string? Resolution { get; set; }
        }

        [System.Text.Json.Serialization.JsonSourceGenerationOptions(
            GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
        [System.Text.Json.Serialization.JsonSerializable(typeof(MiniMaxStreamChunk))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(MiniMaxChatCompletionRequest))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(MiniMaxVideoStatusResponse))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string?>))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(List<object>))]
        private sealed partial class MiniMaxStreamJsonContext
            : System.Text.Json.Serialization.JsonSerializerContext;
    }
}
