using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models;

/// <summary>
/// Represents usage statistics for a chat completion request.
/// </summary>
public class Usage
{
    /// <summary>
    /// Copies usage for pricing without changing the provider's accounting evidence.
    /// Includes server-only cost and token-inclusion fields that JSON serialization omits.
    /// </summary>
    /// <remarks>
    /// Rules-based pricing enriches its parameter map from standard usage fields. Copy
    /// that map so these additions do not alter the provider evidence. Other metadata
    /// remains read-only during pricing.
    /// </remarks>
    public Usage CopyForBilling()
    {
        var copy = (Usage)MemberwiseClone();
        copy.PricingParameters = PricingParameters is null
            ? null : new Dictionary<string, object>(PricingParameters, PricingParameters.Comparer);
        return copy;
    }

    /// <summary>
    /// Number of tokens in the prompt.
    /// </summary>
    [JsonPropertyName("prompt_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PromptTokens { get; set; }

    /// <summary>
    /// Number of tokens in the generated completion.
    /// </summary>
    [JsonPropertyName("completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletionTokens { get; set; }

    /// <summary>
    /// Total number of tokens used in the request (prompt + completion).
    /// </summary>
    [JsonPropertyName("total_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalTokens { get; set; }

    /// <summary>
    /// Number of images generated (used for image generation requests).
    /// </summary>
    [JsonPropertyName("image_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ImageCount { get; set; }

    /// <summary>
    /// Duration of video generated in seconds (used for video generation requests).
    /// </summary>
    [JsonPropertyName("video_duration_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? VideoDurationSeconds { get; set; }

    /// <summary>
    /// Resolution of video generated (e.g., "1920x1080").
    /// </summary>
    [JsonPropertyName("video_resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoResolution { get; set; }

    /// <summary>
    /// Indicates whether this usage is for a batch processing request.
    /// </summary>
    /// <remarks>
    /// When true, batch processing discounts may be applied to the cost calculation.
    /// </remarks>
    [JsonPropertyName("is_batch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsBatch { get; set; }

    /// <summary>
    /// Quality tier for image generation (e.g., "standard", "hd").
    /// </summary>
    /// <remarks>
    /// Used to apply quality-based multipliers to image generation costs.
    /// Different providers may use different quality tier names.
    /// </remarks>
    [JsonPropertyName("image_quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageQuality { get; set; }

    /// <summary>
    /// Resolution for image generation (e.g., "1024x1024", "1792x1024").
    /// </summary>
    /// <remarks>
    /// Used to apply resolution-based multipliers to image generation costs.
    /// Format is typically "widthxheight" in pixels.
    /// </remarks>
    [JsonPropertyName("image_resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageResolution { get; set; }

    /// <summary>
    /// Number of cached input tokens used (read from cache).
    /// </summary>
    /// <remarks>
    /// For providers that support prompt caching (e.g., Anthropic Claude, Google Gemini),
    /// this represents tokens that were read from the cache rather than processed as new input.
    /// These tokens are typically charged at a much lower rate than regular input tokens.
    /// </remarks>
    [JsonPropertyName("cached_input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CachedInputTokens { get; set; }

    /// <summary>
    /// Indicates whether <see cref="CachedInputTokens"/> is already included in
    /// <see cref="PromptTokens"/>.
    /// </summary>
    /// <remarks>
    /// OpenAI-style usage includes cached tokens in the prompt total, while Anthropic's
    /// <c>input_tokens</c> count excludes cache-read tokens. The default preserves the
    /// OpenAI convention for existing callers.
    /// </remarks>
    [JsonIgnore]
    public bool CachedInputTokensIncludedInPrompt { get; set; } = true;

    /// <summary>
    /// Number of tokens written to the cache.
    /// </summary>
    /// <remarks>
    /// For providers that support prompt caching (e.g., Anthropic Claude, Google Gemini),
    /// this represents tokens that were written to the cache for future reuse.
    /// Cache write operations may have a different cost than regular input processing.
    /// </remarks>
    [JsonPropertyName("cached_write_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CachedWriteTokens { get; set; }

    /// <summary>Whether cache-write tokens are already included in <see cref="PromptTokens"/>.</summary>
    [JsonIgnore]
    public bool CachedWriteTokensIncludedInPrompt { get; set; } = true;

    /// <summary>
    /// Number of search units consumed (for rerank operations).
    /// </summary>
    /// <remarks>
    /// Used by reranking models like Cohere Rerank that charge per search unit.
    /// A search unit typically consists of 1 query + up to 100 documents to be ranked.
    /// </remarks>
    [JsonPropertyName("search_units")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SearchUnits { get; set; }

    /// <summary>
    /// Additional metadata for search operations.
    /// </summary>
    /// <remarks>
    /// Provides detailed breakdown of search/rerank operations for cost tracking and debugging.
    /// </remarks>
    [JsonPropertyName("search_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SearchUsageMetadata? SearchMetadata { get; set; }

    /// <summary>
    /// Number of inference steps used for image generation.
    /// </summary>
    /// <remarks>
    /// Used by providers like Fireworks that charge based on the number of iterative refinement steps.
    /// Each step represents one iteration of the diffusion process that refines noise into an image.
    /// Different models require different numbers of steps for optimal quality.
    /// Example: FLUX.1[schnell] uses 4 steps, SDXL typically uses 30 steps.
    /// </remarks>
    [JsonPropertyName("inference_steps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InferenceSteps { get; set; }

    /// <summary>
    /// Number of reasoning tokens used (o1 models and other reasoning models).
    /// </summary>
    /// <remarks>
    /// These represent the model's internal reasoning process tokens and are a subset of
    /// <see cref="CompletionTokens"/>, following the OpenAI usage convention.
    /// Used by models like OpenAI o1, DeepSeek-R1, Claude with thinking mode, 
    /// Gemini 2.5 with thinking, and Qwen QwQ.
    /// These tokens are typically billed at output token rates.
    /// </remarks>
    [JsonPropertyName("reasoning_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReasoningTokens { get; set; }

    /// <summary>
    /// Duration of transcribed audio in seconds (speech-to-text). Billed per minute.
    /// </summary>
    [JsonPropertyName("audio_duration_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AudioDurationSeconds { get; set; }

    /// <summary>
    /// Number of input characters synthesized (text-to-speech). Billed per thousand characters.
    /// </summary>
    [JsonPropertyName("tts_characters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TtsCharacters { get; set; }

    /// <summary>
    /// Optional metadata for provider-specific usage information.
    /// </summary>
    /// <remarks>
    /// Can include provider-specific details such as:
    /// - Cache TTL information
    /// - Model-specific parameters
    /// - Additional breakdown of resource usage
    /// - Provider-specific billing details
    /// </remarks>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Parameters used for rules-based pricing evaluation.
    /// </summary>
    /// <remarks>
    /// Contains key-value pairs that are matched against pricing rule conditions.
    /// These parameters typically correspond to ModelSeries.Parameters definitions.
    /// Common parameters include:
    /// - resolution: Normalized resolution (e.g., "1080p", "720p", "480p")
    /// - with_audio: Boolean for audio feature in video generation
    /// - aspect_ratio: Video/image aspect ratio (e.g., "16:9", "9:16")
    /// - quality: Quality tier (e.g., "standard", "hd")
    /// The pricing rules engine evaluates these against rule conditions to determine rates.
    /// </remarks>
    [JsonPropertyName("pricing_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? PricingParameters { get; set; }

    /// <summary>
    /// Cost in USD reported by the provider for this request (e.g. OpenRouter's usage.cost — the
    /// actual credits the operator was charged).
    /// </summary>
    /// <remarks>
    /// Server-only: captured for billing but never serialized to API clients (exposing it would
    /// leak the operator's upstream cost). It is set programmatically by the provider client's usage
    /// mapping, not by JSON binding. When present and the provider is configured as trusted,
    /// <see cref="ProviderCostPolicy"/> makes it authoritative for spend calculation.
    /// </remarks>
    [JsonIgnore]
    public decimal? ProviderReportedCostUsd { get; set; }

    /// <summary>
    /// Billing policy stamped by the Gateway before cost calculation. Never serialized.
    /// </summary>
    /// <remarks>
    /// When <see cref="ProviderCostBillingPolicy.TrustProviderReportedCost"/> is true and
    /// <see cref="ProviderReportedCostUsd"/> is present, the cost calculator bills
    /// <c>ProviderReportedCostUsd * MarkupMultiplier</c> instead of computing from ModelCost.
    /// </remarks>
    [JsonIgnore]
    public ProviderCostBillingPolicy? ProviderCostPolicy { get; set; }

    /// <summary>
    /// Describes a conservative pricing fallback used when the provider's reported usage did not
    /// exactly match a configured rate. Server-only; used to emit a billing audit event.
    /// </summary>
    [JsonIgnore]
    public string? PricingFallbackReason { get; set; }

    /// <summary>
    /// Extension data to capture additional provider-specific fields not defined in the model.
    /// </summary>
    /// <remarks>
    /// This property captures any JSON properties that don't map to defined properties.
    /// For example, SambaNova includes performance metrics like completion_tokens_per_sec,
    /// time_to_first_token, total_latency, etc. These will be captured here without
    /// causing deserialization errors.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Represents detailed usage metadata for search and reranking operations.
/// </summary>
public class SearchUsageMetadata
{
    /// <summary>
    /// Number of queries processed.
    /// </summary>
    [JsonPropertyName("query_count")]
    public int QueryCount { get; set; }

    /// <summary>
    /// Total number of documents ranked.
    /// </summary>
    [JsonPropertyName("document_count")]
    public int DocumentCount { get; set; }

    /// <summary>
    /// Number of documents that were split into chunks.
    /// </summary>
    /// <remarks>
    /// Documents exceeding 500 tokens are typically split into chunks,
    /// with each chunk counting as a separate document for billing purposes.
    /// </remarks>
    [JsonPropertyName("chunked_document_count")]
    public int ChunkedDocumentCount { get; set; }
}
