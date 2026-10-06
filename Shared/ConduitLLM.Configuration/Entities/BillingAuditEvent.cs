using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ConduitLLM.Functions.Interfaces;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// Represents an audit event for billing operations, tracking all billing decisions and failures
    /// </summary>
    public class BillingAuditEvent : IAuditEvent
    {
        /// <summary>
        /// Unique identifier for the audit event
        /// </summary>
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Timestamp when the audit event occurred
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Type of billing event that occurred
        /// </summary>
        public BillingAuditEventType EventType { get; set; }

        /// <summary>
        /// ID of the virtual key associated with this event
        /// </summary>
        public int? VirtualKeyId { get; set; }

        /// <summary>
        /// ID of the virtual key group associated with a group-level billing event.
        /// </summary>
        public int? VirtualKeyGroupId { get; set; }

        /// <summary>
        /// Model name used in the request
        /// </summary>
        [StringLength(100)]
        public string? Model { get; set; }

        /// <summary>
        /// Unique request identifier for correlation
        /// </summary>
        [StringLength(100)]
        public string? RequestId { get; set; }

        /// <summary>
        /// JSON serialized usage data
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? UsageJson { get; set; }

        /// <summary>
        /// Calculated cost for the request (null if not calculated)
        /// </summary>
        [Column(TypeName = "decimal(10, 6)")]
        public decimal? CalculatedCost { get; set; }

        /// <summary>
        /// Reason for failure or special handling
        /// </summary>
        [StringLength(500)]
        public string? FailureReason { get; set; }

        /// <summary>
        /// Provider type used for the request
        /// </summary>
        [StringLength(50)]
        public string? ProviderType { get; set; }

        /// <summary>
        /// HTTP status code of the response
        /// </summary>
        public int? HttpStatusCode { get; set; }

        /// <summary>
        /// Additional metadata as JSON
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? MetadataJson { get; set; }

        /// <summary>
        /// Request path that triggered this event
        /// </summary>
        [StringLength(256)]
        public string? RequestPath { get; set; }

        /// <summary>
        /// Indicates if the cost was estimated rather than calculated
        /// </summary>
        public bool IsEstimated { get; set; }

        /// <summary>
        /// JSON array of tools used in this request
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? ToolUsageJson { get; set; }

        /// <summary>
        /// Total cost from tool usage (separate from token costs)
        /// </summary>
        [Column(TypeName = "decimal(10, 6)")]
        public decimal? ToolUsageCost { get; set; }

        /// <summary>
        /// Navigation property to the virtual key
        /// </summary>
        [ForeignKey("VirtualKeyId")]
        public virtual VirtualKey? VirtualKey { get; set; }
    }

    /// <summary>
    /// Types of billing audit events
    /// </summary>
    public enum BillingAuditEventType
    {
        /// <summary>
        /// Successful usage tracking and billing
        /// </summary>
        UsageTracked = 1,

        /// <summary>
        /// Usage was estimated due to missing data
        /// </summary>
        UsageEstimated = 2,

        /// <summary>
        /// Zero cost calculated, no billing occurred
        /// </summary>
        ZeroCostSkipped = 3,

        /// <summary>
        /// Model has no cost configuration
        /// </summary>
        MissingCostConfig = 4,

        /// <summary>
        /// No usage data in response
        /// </summary>
        MissingUsageData = 5,

        /// <summary>
        /// Failed to update spend (Redis/DB)
        /// </summary>
        SpendUpdateFailed = 6,

        /// <summary>
        /// Error response not billed (4xx/5xx)
        /// </summary>
        ErrorResponseSkipped = 7,

        /// <summary>
        /// Streaming response missing usage data
        /// </summary>
        StreamingUsageMissing = 8,

        /// <summary>
        /// No virtual key found for request
        /// </summary>
        NoVirtualKey = 9,

        /// <summary>
        /// JSON parsing error prevented tracking
        /// </summary>
        JsonParseError = 10,

        /// <summary>
        /// Unexpected error during tracking
        /// </summary>
        UnexpectedError = 11,

        /// <summary>
        /// Tool usage was tracked and billed
        /// </summary>
        ToolUsageTracked = 12,

        /// <summary>
        /// Tool usage occurred but no cost config found
        /// </summary>
        ToolUsageMissingCostConfig = 13,

        /// <summary>
        /// Model cost calculation failed; the request was retained for reconciliation.
        /// </summary>
        PricingCalculationFailed = 14,

        /// <summary>
        /// Request-log, ledger, or provider-cost totals diverged during reconciliation.
        /// </summary>
        BillingReconciliationMismatch = 15,

        /// <summary>
        /// A persisted pricing configuration violates current billing invariants.
        /// </summary>
        InvalidPricingConfiguration = 16,

        /// <summary>
        /// Positive usage was intentionally priced at zero by an active configuration.
        /// </summary>
        ConfiguredZeroCost = 17,

        /// <summary>
        /// Positive usage was observed without an applicable price.
        /// </summary>
        UnpricedUsage = 18,

        /// <summary>
        /// A synthetic active-model cost check failed or calculated a non-positive cost.
        /// </summary>
        ModelCostCanaryFailed = 19
    }
}
