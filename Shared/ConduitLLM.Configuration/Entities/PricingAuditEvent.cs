using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ConduitLLM.Functions.Interfaces;

namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Represents an audit event for rules-based pricing evaluations.
/// Tracks pricing decisions for billing disputes and analytics.
/// </summary>
public class PricingAuditEvent : IAuditEvent
{
    /// <summary>
    /// Unique identifier for the audit event.
    /// </summary>
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// Timestamp when the pricing evaluation occurred.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// ID of the virtual key that was charged.
    /// </summary>
    public int VirtualKeyId { get; set; }

    /// <summary>
    /// Model identifier used in the request.
    /// </summary>
    [StringLength(100)]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// ID of the ModelCost configuration used.
    /// </summary>
    public int ModelCostId { get; set; }

    /// <summary>
    /// Pricing type used: per_unit, per_second, or per_step.
    /// </summary>
    [StringLength(20)]
    public string PricingType { get; set; } = string.Empty;

    /// <summary>
    /// JSON serialized input parameters that were evaluated against rules.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string InputParameters { get; set; } = "{}";

    /// <summary>
    /// JSON serialized rule that matched (null if default rate was used).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? MatchedRule { get; set; }

    /// <summary>
    /// Whether the default rate was used (no rule matched).
    /// </summary>
    public bool UsedDefaultRate { get; set; }

    /// <summary>
    /// The rate that was applied.
    /// </summary>
    [Column(TypeName = "decimal(10, 8)")]
    public decimal AppliedRate { get; set; }

    /// <summary>
    /// The quantity (duration, count, steps) used in calculation.
    /// </summary>
    [Column(TypeName = "decimal(10, 4)")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Final calculated cost (Rate * Quantity).
    /// </summary>
    [Column(TypeName = "decimal(10, 6)")]
    public decimal CalculatedCost { get; set; }

    /// <summary>
    /// Request ID for correlation with request logs.
    /// </summary>
    [StringLength(100)]
    public string? RequestId { get; set; }

    /// <summary>
    /// Navigation property to the virtual key.
    /// </summary>
    [ForeignKey("VirtualKeyId")]
    public virtual VirtualKey? VirtualKey { get; set; }
}
