using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Functions.Models;

/// <summary>
/// Represents usage statistics for a function execution.
/// Captures billing dimensions across different function types (search, RAG, answer, etc.).
/// </summary>
/// <remarks>
/// This model is analogous to the Usage model for LLM operations, but adapted for function-specific billing.
/// Different providers and function types will populate different fields based on their capabilities.
/// </remarks>
public class FunctionExecutionUsage
{
    /// <summary>
    /// Number of results returned by the function (e.g., search results).
    /// </summary>
    /// <remarks>
    /// Used for PerResult pricing model.
    /// For search functions like Exa, this represents the number of search results returned.
    /// </remarks>
    [JsonPropertyName("result_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ResultCount { get; set; }

    /// <summary>
    /// Number of tokens consumed by the function (for Answer/LLM-based functions).
    /// </summary>
    /// <remarks>
    /// Used for PerToken pricing model.
    /// Applicable to functions that call LLMs internally (e.g., Perplexity Answer API).
    /// </remarks>
    [JsonPropertyName("tokens_consumed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TokensConsumed { get; set; }

    /// <summary>
    /// Number of input tokens consumed when the provider reports token types separately.
    /// </summary>
    [JsonPropertyName("input_tokens_consumed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InputTokensConsumed { get; set; }

    /// <summary>
    /// Number of output tokens consumed when the provider reports token types separately.
    /// </summary>
    [JsonPropertyName("output_tokens_consumed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputTokensConsumed { get; set; }

    /// <summary>
    /// Duration of the function execution.
    /// </summary>
    /// <remarks>
    /// Used for TimeBased pricing model.
    /// Captured for all executions for performance monitoring and time-based billing.
    /// </remarks>
    [JsonPropertyName("execution_duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? ExecutionDuration { get; set; }

    /// <summary>
    /// Type of search performed (e.g., "neural", "keyword", "auto").
    /// </summary>
    /// <remarks>
    /// Exa-specific: Determines which pricing tier to apply.
    /// Neural search typically costs more than keyword search.
    /// </remarks>
    [JsonPropertyName("search_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SearchType { get; set; }

    /// <summary>
    /// Number of pages with full text extraction.
    /// </summary>
    /// <remarks>
    /// Exa-specific: Each page with text extraction incurs additional cost.
    /// Used to calculate content extraction costs separately from search costs.
    /// </remarks>
    [JsonPropertyName("text_pages_extracted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TextPagesExtracted { get; set; }

    /// <summary>
    /// Number of pages with highlights extraction.
    /// </summary>
    /// <remarks>
    /// Exa-specific: Each page with highlights extraction incurs additional cost.
    /// Highlights are key snippets from the page relevant to the search query.
    /// </remarks>
    [JsonPropertyName("highlight_pages_extracted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HighlightPagesExtracted { get; set; }

    /// <summary>
    /// Number of pages with AI-generated summaries.
    /// </summary>
    /// <remarks>
    /// Exa-specific: Each page with summary generation incurs additional cost.
    /// Summaries are LLM-generated condensed versions of the page content.
    /// </remarks>
    [JsonPropertyName("summary_pages_generated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SummaryPagesGenerated { get; set; }

    /// <summary>
    /// Number of documents indexed or stored (for RAG functions).
    /// </summary>
    /// <remarks>
    /// RAG-specific: Number of document chunks indexed in vector database.
    /// Used for RAG storage pricing.
    /// </remarks>
    [JsonPropertyName("documents_indexed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DocumentsIndexed { get; set; }

    /// <summary>
    /// Number of vector dimensions used (for RAG functions).
    /// </summary>
    /// <remarks>
    /// RAG-specific: Higher dimensions typically cost more for storage and retrieval.
    /// Example: 1536 for OpenAI embeddings, 768 for smaller models.
    /// </remarks>
    [JsonPropertyName("vector_dimensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VectorDimensions { get; set; }

    /// <summary>
    /// Provider-specific metadata for detailed usage tracking.
    /// </summary>
    /// <remarks>
    /// Extensibility mechanism for provider-specific usage details.
    /// Examples:
    /// - Exa: {"requestId": "exa_12345", "livecrawlUsed": true, "autopromptApplied": false}
    /// - Perplexity: {"citations": 5, "model": "pplx-70b-online"}
    /// - RAG: {"vectorDbProvider": "pinecone", "namespace": "default"}
    /// </remarks>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Provider-reported cost (if available) used as the authoritative execution cost.
    /// </summary>
    /// <remarks>
    /// Some providers (like Exa) return their own cost calculation in the response.
    /// This field stores that value for billing, reconciliation, and audit purposes.
    /// Stored in USD.
    /// </remarks>
    [JsonPropertyName("provider_reported_cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ProviderReportedCost { get; set; }

    /// <summary>
    /// Extension data to capture additional provider-specific fields not defined in the model.
    /// </summary>
    /// <remarks>
    /// This property captures any JSON properties that don't map to defined properties.
    /// Ensures forward compatibility as providers add new usage metrics.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
