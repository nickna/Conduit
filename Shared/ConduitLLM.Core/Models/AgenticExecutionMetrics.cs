using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models;

/// <summary>
/// Metrics and summary data for agentic execution workflows.
/// Tracks iterations, function calls, and aggregated costs across the entire agentic loop.
/// </summary>
public class AgenticExecutionMetrics
{
    /// <summary>
    /// Individual provider calls made by the agentic loop. This is server-only billing data;
    /// clients continue to receive the aggregate Usage object on the response.
    /// </summary>
    [JsonIgnore]
    public List<ProviderCallUsage> ProviderCalls { get; set; } = new();

    /// <summary>
    /// Total number of iterations (LLM call → function execution → LLM call cycles) that occurred
    /// </summary>
    [JsonPropertyName("total_iterations")]
    public int TotalIterations { get; set; }

    /// <summary>
    /// Total number of function calls executed across all iterations
    /// </summary>
    [JsonPropertyName("total_function_calls")]
    public int TotalFunctionCalls { get; set; }

    /// <summary>
    /// Total cost of all function executions
    /// </summary>
    [JsonPropertyName("total_function_cost")]
    public decimal TotalFunctionCost { get; set; }

    /// <summary>
    /// Total cost of all LLM completions (across all iterations), when pricing is resolved.
    /// </summary>
    [JsonPropertyName("total_llm_cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? TotalLLMCost { get; set; }

    /// <summary>
    /// Combined cost of LLM completions, provider-hosted tools, and functions, when pricing is resolved.
    /// </summary>
    [JsonPropertyName("total_cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? TotalCost { get; set; }

    /// <summary>
    /// Summary of each function call that was executed
    /// </summary>
    [JsonPropertyName("function_calls")]
    public List<FunctionCallSummary> FunctionCalls { get; set; } = new();
}

/// <summary>
/// Server-only usage captured for one provider call in an agentic request.
/// </summary>
public sealed class ProviderCallUsage
{
    /// <summary>Actual provider invocation ordinal, including fallback and forced-final calls.</summary>
    public int Iteration { get; set; }
    public Usage Usage { get; set; } = new();
}

/// <summary>
/// Summary information for a single function call within an agentic workflow
/// </summary>
public class FunctionCallSummary
{
    /// <summary>
    /// Which iteration this function call occurred in (1-indexed)
    /// </summary>
    [JsonPropertyName("iteration")]
    public int Iteration { get; set; }

    /// <summary>
    /// ID of the tool call from the LLM response
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    /// <summary>
    /// Function name that was called
    /// </summary>
    [JsonPropertyName("function_name")]
    public string? FunctionName { get; set; }

    /// <summary>
    /// Function configuration ID
    /// </summary>
    [JsonPropertyName("function_configuration_id")]
    public int FunctionConfigurationId { get; set; }

    /// <summary>
    /// Function execution ID (for detailed audit lookup)
    /// </summary>
    [JsonPropertyName("function_execution_id")]
    public Guid? FunctionExecutionId { get; set; }

    /// <summary>
    /// Whether the function execution succeeded
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Cost of this specific function call
    /// </summary>
    [JsonPropertyName("cost")]
    public decimal? Cost { get; set; }

    /// <summary>
    /// Error message if the function failed
    /// </summary>
    [JsonPropertyName("error_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Duration of the function execution
    /// </summary>
    [JsonPropertyName("duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; set; }
}
