using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models;

/// <summary>
/// Strongly-typed event emitted during agentic tool execution.
/// Replaces anonymous objects to provide compile-time safety and eliminate reflection.
/// </summary>
public class ToolExecutionEvent
{
    /// <summary>
    /// Originating agentic iteration (1-indexed), when the event belongs to an agentic loop.
    /// </summary>
    [JsonPropertyName("iteration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Iteration { get; set; }

    /// <summary>
    /// The tool call ID from the LLM response.
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    /// <summary>
    /// Name of the function being executed.
    /// </summary>
    [JsonPropertyName("function_name")]
    public string? FunctionName { get; set; }

    /// <summary>
    /// Execution status: "started", "completed", or "failed".
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    /// <summary>
    /// Cost of the function execution (populated on completed/failed).
    /// </summary>
    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Cost { get; set; }

    /// <summary>
    /// Error message if the function failed (populated on failed).
    /// </summary>
    [JsonPropertyName("error_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// ID of the FunctionExecution record for audit/drill-down (populated on completed/failed).
    /// </summary>
    [JsonPropertyName("function_execution_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? FunctionExecutionId { get; set; }
}
