using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;

namespace ConduitLLM.Functions.Entities;

/// <summary>
/// Audit trail for function calls made during chat completions.
/// Tracks the lifecycle of function calls initiated by LLMs in agentic workflows.
/// Links to both the parent chat completion request and the function execution.
/// </summary>
[Table("FunctionCallAudits")]
public class FunctionCallAudit : IAuditEvent
{
    /// <summary>
    /// Unique identifier for this audit event
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Link to parent chat completion request (correlation ID)
    /// </summary>
    public Guid? ChatCompletionId { get; set; }

    /// <summary>
    /// Link to the function execution record (if this function call resulted in execution)
    /// </summary>
    public Guid? FunctionExecutionId { get; set; }

    /// <summary>
    /// Navigation property to FunctionExecution
    /// </summary>
    [ForeignKey(nameof(FunctionExecutionId))]
    public FunctionExecution? FunctionExecution { get; set; }

    /// <summary>
    /// Which function configuration was called
    /// </summary>
    [Required]
    public int FunctionConfigurationId { get; set; }

    /// <summary>
    /// Navigation property to FunctionConfiguration
    /// </summary>
    [ForeignKey(nameof(FunctionConfigurationId))]
    public FunctionConfiguration? FunctionConfiguration { get; set; }

    /// <summary>
    /// Which virtual key initiated this function call
    /// </summary>
    [Required]
    public int VirtualKeyId { get; set; }

    /// <summary>
    /// When this audit event occurred
    /// </summary>
    [Required]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Type of audit event (requested, started, completed, failed, etc.)
    /// </summary>
    [Required]
    public FunctionCallAuditEventType EventType { get; set; }

    /// <summary>
    /// Serialized function call details (tool call from LLM response)
    /// Stored as JSONB for efficient querying
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? FunctionCallJson { get; set; }

    /// <summary>
    /// Serialized function execution result
    /// Stored as JSONB for efficient querying
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ResultJson { get; set; }

    /// <summary>
    /// Cost associated with this function call
    /// </summary>
    [Column(TypeName = "decimal(18,8)")]
    public decimal? Cost { get; set; }

    /// <summary>
    /// Whether the cost was estimated (vs actual)
    /// </summary>
    public bool? IsEstimated { get; set; }

    /// <summary>
    /// Reason for failure (if EventType = Failed or ParseError)
    /// </summary>
    [StringLength(2000)]
    public string? FailureReason { get; set; }

    /// <summary>
    /// HTTP request correlation ID (trace identifier)
    /// Used to link all audit events for a single request
    /// </summary>
    [StringLength(100)]
    public string? RequestId { get; set; }

    /// <summary>
    /// Which iteration of the agentic loop this function call occurred in
    /// 1 = first iteration, 2 = second, etc.
    /// </summary>
    public int? IterationNumber { get; set; }

    /// <summary>
    /// Error message if execution failed
    /// </summary>
    [StringLength(500)]
    public string? ErrorMessage { get; set; }
}
