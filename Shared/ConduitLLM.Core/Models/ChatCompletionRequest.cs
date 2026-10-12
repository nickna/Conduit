using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Core.Models;

/// <summary>
/// Represents a request to create a chat completion.
/// </summary>
public class ChatCompletionRequest
{
    /// <summary>
    /// The ID of the model to use for this completion.
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    /// <summary>
    /// A list of messages comprising the conversation so far.
    /// </summary>
    [JsonPropertyName("messages")]
    public required List<Message> Messages { get; set; }

    /// <summary>
    /// What sampling temperature to use, between 0 and 2. Higher values like 0.8 will make the output more random,
    /// while lower values like 0.2 will make it more focused and deterministic.
    /// </summary>
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    /// <summary>
    /// The maximum number of tokens to generate in the chat completion.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    /// <summary>The maximum number of generated tokens, including visible and reasoning tokens.</summary>
    [JsonPropertyName("max_completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxCompletionTokens { get; set; }

    /// <summary>
    /// An alternative to sampling with temperature, called nucleus sampling, where the model considers the results of
    /// the tokens with top_p probability mass. So 0.1 means only the tokens comprising the top 10% probability mass are considered.
    /// </summary>
    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    /// <summary>
    /// Limits the number of tokens to consider for each step of generation.
    /// Only the top K most likely tokens are considered for sampling.
    /// Typical values range from 1 to 100. Lower values make output more focused.
    /// </summary>
    /// <remarks>
    /// Top-k sampling is a technique that restricts the model to only consider 
    /// the K most likely next tokens at each step. This can help prevent the model 
    /// from selecting very unlikely tokens and can make the output more coherent.
    /// Not all providers support this parameter.
    /// </remarks>
    [JsonPropertyName("top_k")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopK { get; set; }

    /// <summary>
    /// How many chat completion choices to generate for each input message.
    /// </summary>
    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? N { get; set; }

    /// <summary>
    /// If set, partial message deltas will be sent, like in ChatGPT. Tokens will be sent as data-only server-sent events
    /// as they become available, with the stream terminated by a data: [DONE] message.
    /// </summary>
    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    /// <summary>
    /// Options for streaming responses. Allows requesting usage data in the final chunk.
    /// </summary>
    /// <remarks>
    /// Set stream_options.include_usage to true to receive token usage information in streaming mode.
    /// Supported by OpenAI and OpenAI-compatible providers (Groq, SambaNova, Cerebras, etc.).
    /// This is critical for accurate billing and performance metrics.
    /// </remarks>
    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StreamOptions? StreamOptions { get; set; }

    /// <summary>
    /// Up to 4 sequences where the API will stop generating further tokens.
    /// </summary>
    [JsonPropertyName("stop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public List<string>? Stop { get; set; }

    /// <summary>
    /// A unique identifier representing your end-user, which can help OpenAI to monitor and detect abuse.
    /// </summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    /// <summary>
    /// Stable conversation identifier used for provider and prompt-cache affinity. The request body
    /// takes precedence over X-Conduit-Session-Id. Limited to 256 characters.
    /// </summary>
    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [StringLength(256)]
    public string? SessionId { get; set; }

    /// <summary>
    /// A list of tools the model may call. Currently, only functions are supported as tools.
    /// </summary>
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Tool>? Tools { get; set; }

    /// <summary>
    /// Controls which (if any) tool is called by the model. "none" means the model will not call a tool and instead generates a message.
    /// "auto" means the model can choose either to call a tool or not. Specifying a particular function forces the model to call that function.
    /// </summary>
    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolChoice? ToolChoice { get; set; }

    /// <summary>Deprecated function definitions retained for official SDK compatibility.</summary>
    [JsonPropertyName("functions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FunctionDefinition>? Functions { get; set; }

    /// <summary>Deprecated function selection retained for official SDK compatibility.</summary>
    [JsonPropertyName("function_call")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyFunctionChoice? FunctionCall { get; set; }

    /// <summary>
    /// Specifies the format that the model must output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property allows you to control the format of the model's response. For example,
    /// you can request the model to respond with valid JSON by setting ResponseFormat.Type to "json_object".
    /// </para>
    /// <para>
    /// See <see cref="ResponseFormat"/> for details on available format options.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Request JSON response
    /// var request = new ChatCompletionRequest 
    /// {
    ///     // ... other properties
    ///     ResponseFormat = ResponseFormat.Json()
    /// };
    /// </code>
    /// </example>
    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseFormat? ResponseFormat { get; set; }

    /// <summary>
    /// Unified reasoning configuration (effort / max tokens / enabled / exclude). Forwarded to
    /// providers that support reasoning (e.g. OpenRouter). Only serialized when set.
    /// </summary>
    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReasoningConfig? Reasoning { get; set; }

    /// <summary>OpenAI reasoning effort hint.</summary>
    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; set; }

    /// <summary>Whether tools may be called in parallel.</summary>
    [JsonPropertyName("parallel_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParallelToolCalls { get; set; }

    /// <summary>Requested response modalities, such as text and audio.</summary>
    [JsonPropertyName("modalities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Modalities { get; set; }

    /// <summary>Audio output configuration when audio is requested as a modality.</summary>
    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatAudioOptions? Audio { get; set; }

    /// <summary>Static predicted output content used to reduce latency.</summary>
    [JsonPropertyName("prediction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatPrediction? Prediction { get; set; }

    /// <summary>Whether token log probabilities should be returned.</summary>
    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Logprobs { get; set; }

    /// <summary>Number of most likely tokens returned at each position.</summary>
    [JsonPropertyName("top_logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopLogprobs { get; set; }

    /// <summary>Requested processing tier.</summary>
    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    /// <summary>Whether the provider should store this completion.</summary>
    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Store { get; set; }

    /// <summary>Developer-provided request metadata.</summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>Stable identifier used by providers for safety monitoring.</summary>
    [JsonPropertyName("safety_identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SafetyIdentifier { get; set; }

    /// <summary>Desired response verbosity.</summary>
    [JsonPropertyName("verbosity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verbosity { get; set; }

    /// <summary>Moderation behavior requested for the completion.</summary>
    [JsonPropertyName("moderation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Moderation { get; set; }

    /// <summary>Prompt-cache routing key.</summary>
    [JsonPropertyName("prompt_cache_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheKey { get; set; }

    /// <summary>Prompt-cache configuration.</summary>
    [JsonPropertyName("prompt_cache_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PromptCacheOptions? PromptCacheOptions { get; set; }

    /// <summary>Prompt-cache retention policy.</summary>
    [JsonPropertyName("prompt_cache_retention")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheRetention { get; set; }

    /// <summary>Provider-hosted web search configuration.</summary>
    [JsonPropertyName("web_search_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebSearchOptions? WebSearchOptions { get; set; }

    /// <summary>
    /// A random number seed for deterministic outputs.
    /// </summary>
    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; set; }

    /// <summary>
    /// Number between -2.0 and 2.0. Positive values penalize new tokens based on whether they appear 
    /// in the text so far, increasing the model's likelihood to talk about new topics.
    /// </summary>
    [JsonPropertyName("presence_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PresencePenalty { get; set; }

    /// <summary>
    /// Number between -2.0 and 2.0. Positive values penalize new tokens based on their existing 
    /// frequency in the text so far, decreasing the model's likelihood to repeat the same line verbatim.
    /// </summary>
    [JsonPropertyName("frequency_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FrequencyPenalty { get; set; }

    /// <summary>
    /// Modify the likelihood of specified tokens appearing in the completion.
    /// </summary>
    [JsonPropertyName("logit_bias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? LogitBias { get; set; }

    /// <summary>
    /// List of function configuration IDs to make available for this chat session.
    /// When provided, these functions will be converted to Tools and made available for the LLM to call.
    /// </summary>
    /// <remarks>
    /// This is a Conduit-specific extension that maps to internal function configurations.
    /// The functions will be validated for availability and enabled status before being added to the Tools list.
    /// </remarks>
    [JsonPropertyName("function_configuration_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? FunctionConfigurationIds { get; set; }

    /// <summary>
    /// Whether to enable agentic mode, where function calls are automatically executed and results are fed back to the LLM.
    /// If false, function calls will be returned to the caller for manual execution.
    /// </summary>
    /// <remarks>
    /// When enabled, the system will:
    /// 1. Detect tool_calls in LLM responses
    /// 2. Execute the requested functions
    /// 3. Append tool result messages to the conversation
    /// 4. Make a new LLM request with the updated conversation
    /// 5. Repeat until the LLM provides a final answer or max iterations reached
    /// Omitted or null: resolved by the Gateway from GlobalSetting 'Agentic.DefaultEnabled'
    /// (fallback: true). Direct Conduit callers use the fallback when unspecified.
    /// </remarks>
    [JsonPropertyName("enable_agentic_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableAgenticMode { get; set; }

    /// <summary>
    /// Maximum number of agentic iterations (function call loops) allowed.
    /// Prevents infinite loops in agentic workflows.
    /// </summary>
    /// <remarks>
    /// Each iteration consists of: LLM response → function execution → LLM response.
    /// Omitted or null: resolved by the Gateway from GlobalSetting 'Agentic.MaxIterations'
    /// (fallback: 5). Direct Conduit callers use the fallback when unspecified.
    /// Valid Range: Configurable via GlobalSettings 'Agentic.MinIterations' and 'Agentic.MaxIterations' (fallback: 1-100)
    /// If the limit is reached, the system will return the conversation state with an error message.
    /// </remarks>
    [JsonPropertyName("max_agentic_iterations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxAgenticIterations { get; set; }

    /// <summary>
    /// Additional model-specific parameters that are passed through to the provider API.
    /// These parameters are populated based on the model's ApiParameters configuration.
    /// Examples include reasoning_effort, min_p, language, timestamp_granularities, etc.
    /// </summary>
    /// <remarks>
    /// This property captures any JSON properties not explicitly mapped to other properties, and they
    /// are forwarded to the provider as-is (they are not validated against the model's supported
    /// parameters). Standard mapped parameters take precedence on key collision.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Resolved server-side prompt caching intent. Never exposed on the Gateway API.</summary>
    [JsonIgnore]
    public PromptCachingIntent? PromptCachingIntent { get; set; }

    /// <summary>Opaque HMAC affinity value generated by the Gateway. Never serialized to callers.</summary>
    [JsonIgnore]
    public string? RoutingAffinityKey { get; set; }

    [JsonIgnore] public int? SelectedMappingId { get; set; }
    [JsonIgnore] public bool RoutingAffinityUsed { get; set; }
    [JsonIgnore] public string? RoutingDecisionReason { get; set; }
    [JsonIgnore] public int RoutingFailoverCount { get; set; }

    /// <summary>Server-only accounting observers, retained when context management replaces this request.</summary>
    [JsonIgnore] public Func<int, CancellationToken, Task>? ProviderInvocationStarting { get; set; }
    [JsonIgnore] public Action<int, ChatCompletionResponse, Usage?>? ProviderInvocationCompleted { get; set; }
    [JsonIgnore] public Action<AgenticExecutionMetrics>? FunctionExecutionCompleted { get; set; }
    [JsonIgnore] public Action<int, ChatCompletionChunk, Usage?>? ProviderStreamUsageObserved { get; set; }
    [JsonIgnore] public Action<int>? ProviderStreamCompleted { get; set; }

    /// <summary>Preserves server-owned request state when context management replaces the request.</summary>
    internal void CopyRuntimeStateTo(ChatCompletionRequest target)
    {
        target.PromptCachingIntent = PromptCachingIntent;
        target.RoutingAffinityKey = RoutingAffinityKey;
        target.SelectedMappingId = SelectedMappingId;
        target.RoutingAffinityUsed = RoutingAffinityUsed;
        target.RoutingDecisionReason = RoutingDecisionReason;
        target.RoutingFailoverCount = RoutingFailoverCount;
        target.ProviderInvocationStarting = ProviderInvocationStarting;
        target.ProviderInvocationCompleted = ProviderInvocationCompleted;
        target.FunctionExecutionCompleted = FunctionExecutionCompleted;
        target.ProviderStreamUsageObserved = ProviderStreamUsageObserved;
        target.ProviderStreamCompleted = ProviderStreamCompleted;
    }
}
