using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Functions.DTOs;

/// <summary>
/// Request DTO for executing a function
/// </summary>
public class FunctionExecutionRequestDto
{
    /// <summary>
    /// Parameters for the function execution (key-value pairs)
    /// Structure depends on the specific function being called
    /// </summary>
    [Required]
    public required Dictionary<string, object> Parameters { get; set; }

    /// <summary>
    /// Optional webhook URL to notify when execution completes (for async executions)
    /// </summary>
    [StringLength(1000)]
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Optional custom headers for webhook delivery (stored as JSON)
    /// </summary>
    public Dictionary<string, string>? WebhookHeaders { get; set; }
}
