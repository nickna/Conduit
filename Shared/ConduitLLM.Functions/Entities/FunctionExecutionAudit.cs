using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ConduitLLM.Functions.Enums;

namespace ConduitLLM.Functions.Entities;

/// <summary>
/// Audit trail for function execution lifecycle events.
/// Provides complete history for billing verification, compliance, and troubleshooting.
/// </summary>
[Table("FunctionExecutionAudits")]
public class FunctionExecutionAudit
{
    /// <summary>
    /// Unique identifier for this audit event
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to the function execution this audit event belongs to
    /// </summary>
    [Required]
    public Guid FunctionExecutionId { get; set; }

    /// <summary>
    /// Navigation property to the function execution
    /// </summary>
    [ForeignKey(nameof(FunctionExecutionId))]
    public FunctionExecution? FunctionExecution { get; set; }

    /// <summary>
    /// When this audit event occurred
    /// </summary>
    [Required]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Type of audit event
    /// </summary>
    [Required]
    public FunctionAuditEventType EventType { get; set; }

    /// <summary>
    /// Detailed event information (stored as JSON)
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? EventDetails { get; set; }

    /// <summary>
    /// Virtual key ID associated with this event (for quick filtering)
    /// </summary>
    public int? VirtualKeyId { get; set; }

    /// <summary>
    /// Cost associated with this event (if applicable)
    /// </summary>
    [Column(TypeName = "decimal(18,8)")]
    public decimal? Cost { get; set; }

    /// <summary>
    /// Whether the cost is estimated (true) or actual (false)
    /// </summary>
    public bool? IsEstimated { get; set; }

    /// <summary>
    /// Failure reason if this is a failure event
    /// </summary>
    [StringLength(2000)]
    public string? FailureReason { get; set; }
}
