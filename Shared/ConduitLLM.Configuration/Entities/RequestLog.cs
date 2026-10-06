using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using ConduitLLM.Configuration.Entities.Interfaces;
using ConduitLLM.Functions.Interfaces;

namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Represents a log of API requests made using a virtual key
/// </summary>
public class RequestLog : IEntity<int>, IAuditEvent
{
    /// <summary>
    /// Unique identifier for the request log
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// ID of the virtual key used for the request
    /// </summary>
    public int VirtualKeyId { get; set; }

    /// <summary>
    /// Foreign key relationship to the virtual key
    /// </summary>
    [ForeignKey("VirtualKeyId")]
    public virtual VirtualKey? VirtualKey { get; set; }

    /// <summary>
    /// Name of the model used for the request
    /// </summary>
    [Required]
    [StringLength(100)]
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// ID of the provider that processed the request.
    /// References the Provider entity for accurate provider tracking.
    /// </summary>
    public int? ProviderId { get; set; }

    /// <summary>
    /// Type of the provider that processed the request.
    /// Stored as string for flexibility and query performance.
    /// </summary>
    [StringLength(50)]
    public string? ProviderType { get; set; }

    public int? ModelProviderMappingId { get; set; }
    public bool PromptCachingEligible { get; set; }
    public bool PromptCachingPolicyApplied { get; set; }
    [Column(TypeName = "decimal(18, 8)")] public decimal CachedReadSavings { get; set; }
    [Column(TypeName = "decimal(18, 8)")] public decimal CacheWritePremium { get; set; }
    [NotMapped] public decimal PromptCachingNetSavings => CachedReadSavings - CacheWritePremium;
    public bool RoutingAffinityUsed { get; set; }
    [StringLength(50)] public string? RoutingDecisionReason { get; set; }
    public int RoutingFailoverCount { get; set; }

    /// <summary>
    /// Type of the request (chat, completion, embedding, etc.)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string RequestType { get; set; } = string.Empty;

    /// <summary>
    /// Number of input tokens in the request
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// Number of output tokens in the response
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// Number of input tokens read from cache. Null if caching was not used.
    /// </summary>
    public int? CachedInputTokens { get; set; }

    /// <summary>
    /// Number of tokens written to cache. Null if caching was not used.
    /// </summary>
    public int? CachedWriteTokens { get; set; }

    /// <summary>
    /// Cost of the request
    /// </summary>
    [Column(TypeName = "decimal(10, 6)")]
    public decimal Cost { get; set; }

    /// <summary>
    /// How <see cref="Cost"/> was determined. Null (or ModelCost) means it was computed from the
    /// configured ModelCost rates; ProviderReportedCost means it was billed from the provider's
    /// reported per-request cost (times markup), which changes how refunds are calculated.
    /// </summary>
    public Enums.RequestBillingMethod? BillingMethod { get; set; }

    /// <summary>
    /// The raw provider-reported cost (USD, pre-markup) when the request was billed from provider
    /// cost. Recorded for reconciliation/margin analysis; null for ModelCost-billed requests.
    /// </summary>
    [Column(TypeName = "decimal(18, 8)")]
    public decimal? ProviderReportedCostUsd { get; set; }

    /// <summary>
    /// The provider-cost markup multiplier applied to <see cref="ProviderReportedCostUsd"/>.
    /// Snapshotted at billing time so later provider configuration changes do not alter reconciliation.
    /// </summary>
    [Column(TypeName = "decimal(18, 8)")]
    public decimal? ProviderCostMarkupMultiplier { get; set; }

    /// <summary>
    /// When the charge represented by this row occurred. Unlike <see cref="Timestamp"/>, this is
    /// updated when an asynchronous operation is billed on completion and is null for unbilled rows.
    /// </summary>
    public DateTime? BilledAtUtc { get; set; }

    /// <summary>
    /// Response time in milliseconds
    /// </summary>
    public double ResponseTimeMs { get; set; }

    /// <summary>
    /// Timestamp of the request
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional identifier of the user making the request
    /// </summary>
    [StringLength(100)]
    public string? UserId { get; set; }

    /// <summary>
    /// Optional IP address of the client making the request
    /// </summary>
    [StringLength(50)]
    public string? ClientIp { get; set; }

    /// <summary>
    /// Optional request path
    /// </summary>
    [StringLength(256)]
    public string? RequestPath { get; set; }

    /// <summary>
    /// Optional status code of the response
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// Optional metadata as JSON for request-type-specific details.
    /// Used for functions, images, video, audio, and other execution types
    /// that have additional fields beyond the standard token-based schema.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Metadata { get; set; }
}
