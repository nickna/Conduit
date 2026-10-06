using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ConduitLLM.Functions.Entities.Interfaces;
using ConduitLLM.Functions.Enums;

namespace ConduitLLM.Functions.Entities;

/// <summary>
/// Represents a single execution of a function.
/// Tracks the complete lifecycle from request to completion/failure.
/// </summary>
[Table("FunctionExecutions")]
public class FunctionExecution : IIdentifiableEntity<Guid>
{
    /// <summary>
    /// Unique identifier for this execution
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to the function configuration being executed
    /// </summary>
    [Required]
    public int FunctionConfigurationId { get; set; }

    /// <summary>
    /// Navigation property to the function configuration
    /// </summary>
    [ForeignKey(nameof(FunctionConfigurationId))]
    public FunctionConfiguration? FunctionConfiguration { get; set; }

    /// <summary>
    /// Foreign key to the virtual key used for authorization
    /// </summary>
    [Required]
    public int VirtualKeyId { get; set; }

    /// <summary>
    /// Execution mode for this specific execution (Synchronous or Asynchronous)
    /// </summary>
    [Required]
    public ExecutionMode ExecutionMode { get; set; }

    /// <summary>
    /// Current state of the execution
    /// </summary>
    [Required]
    public ExecutionState State { get; set; } = ExecutionState.Pending;

    /// <summary>
    /// When the execution was requested/created
    /// </summary>
    [Required]
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the execution actually started processing
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the execution completed (success or failure)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Execution duration (calculated when completed)
    /// </summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Input parameters for the function (stored as JSON)
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? RequestJson { get; set; }

    /// <summary>
    /// Result data from the function (stored as JSON)
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ResponseJson { get; set; }

    /// <summary>
    /// Error message if execution failed
    /// </summary>
    [StringLength(2000)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Estimated cost before execution (for balance reservation)
    /// </summary>
    [Column(TypeName = "decimal(18,8)")]
    public decimal? EstimatedCost { get; set; }

    /// <summary>
    /// Actual cost after execution (for final billing)
    /// </summary>
    [Column(TypeName = "decimal(18,8)")]
    public decimal? ActualCost { get; set; }

    /// <summary>
    /// Detailed cost calculation breakdown (stored as JSON)
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? CostCalculationDetails { get; set; }

    /// <summary>
    /// Number of retry attempts made for this execution
    /// </summary>
    [Required]
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// When the next retry should be attempted (for failed executions)
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// Worker instance ID that has leased this execution for processing
    /// Used for distributed async execution
    /// </summary>
    [StringLength(100)]
    public string? LeasedBy { get; set; }

    /// <summary>
    /// When the lease on this execution expires
    /// If worker crashes, lease expiry allows another worker to pick it up
    /// </summary>
    public DateTime? LeaseExpiryTime { get; set; }

    /// <summary>
    /// Version number for optimistic concurrency control
    /// Incremented on each update to prevent lost updates
    /// </summary>
    [Required]
    [ConcurrencyCheck]
    public int Version { get; set; } = 0;

    /// <summary>
    /// Optional webhook URL to notify when execution completes
    /// </summary>
    [StringLength(1000)]
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Whether the webhook has been successfully delivered
    /// </summary>
    [Required]
    public bool WebhookDelivered { get; set; } = false;

    /// <summary>
    /// Progress percentage (0-100) for long-running executions
    /// </summary>
    public int? ProgressPercentage { get; set; }

    /// <summary>
    /// Optional status message for progress updates
    /// </summary>
    [StringLength(500)]
    public string? StatusMessage { get; set; }

    // Navigation properties

    /// <summary>
    /// Audit events for this execution
    /// </summary>
    public ICollection<FunctionExecutionAudit> AuditEvents { get; set; } = new List<FunctionExecutionAudit>();
}
