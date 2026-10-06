using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Represents a virtual API key for accessing LLM services
/// </summary>
public partial class VirtualKey : IEntity<int>, IAuditableEntity
{
    /// <summary>
    /// Unique identifier for the virtual key
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Name of the virtual key
    /// </summary>
    [Required]
    [StringLength(100)]
    public string KeyName { get; set; } = string.Empty;

    /// <summary>
    /// The hash of the key value used for authentication
    /// </summary>
    [Required]
    [StringLength(128)]
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the key
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether the key is currently active
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The ID of the virtual key group this key belongs to
    /// </summary>
    public int VirtualKeyGroupId { get; set; }

    /// <summary>
    /// Navigation property to the virtual key group
    /// </summary>
    public virtual VirtualKeyGroup VirtualKeyGroup { get; set; } = null!;

    /// <summary>
    /// Optional date when the key expires
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Date when the key was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date when the key was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional metadata associated with the key
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>
    /// Optional comma-separated list of allowed models
    /// </summary>
    public string? AllowedModels { get; set; }

    /// <summary>
    /// Requests per minute rate limit for this key
    /// </summary>
    public int? RateLimitRpm { get; set; }

    /// <summary>
    /// Requests per day rate limit for this key
    /// </summary>
    public int? RateLimitRpd { get; set; }

    /// <summary>
    /// Tokens per minute rate limit for this key. Null means no token ceiling.
    /// </summary>
    /// <remarks>
    /// Counts prompt plus completion tokens over a rolling minute. Request counting alone
    /// cannot police cost when request sizes differ by two orders of magnitude, which is what
    /// this limit is for. Enforcement reserves an estimate up front and reconciles it to the
    /// actual usage once the response is billed.
    /// </remarks>
    public int? RateLimitTpm { get; set; }

    /// <summary>
    /// Maximum number of requests this key may have in flight at once. Null means no cap.
    /// </summary>
    /// <remarks>
    /// Distinct from RPM: a key well inside its per-minute allowance can still hold hundreds of
    /// simultaneous streaming connections open. The slot is held for the duration of the HTTP
    /// request only — for asynchronous jobs that means the submit call, not the job.
    /// </remarks>
    public int? MaxParallelRequests { get; set; }

    /// <summary>
    /// Priority tier for saturation-aware group rate limiting. 0 = low, 1 = normal,
    /// 2 = high. Null means normal.
    /// </summary>
    /// <remarks>
    /// Only matters for keys whose group has rate-limit ceilings. A low-priority key is
    /// admitted against a reduced fraction of each group ceiling (the deployment-wide
    /// saturation threshold), so once the group is busy, low-priority keys are shed before
    /// the group's ceiling is reached — a noisy batch key cannot crowd out a critical one
    /// in the same group. Normal and high tiers behave identically today; high is reserved
    /// for future refinement.
    /// </remarks>
    public int? RateLimitPriority { get; set; }

    /// <summary>
    /// Per-model rate limit overrides, keyed by the model alias the caller sends.
    /// JSON of the form <c>{"gpt-5": {"rpm": 1000, "tpm": 200000}, "sora*": {"rpm": 10}}</c>.
    /// </summary>
    /// <remarks>
    /// A key's overall ceiling says nothing about which models it burns it on. An override lets
    /// the same key call a cheap chat model freely while being held to a handful of requests a
    /// minute against an expensive video model. Overrides apply on top of the key and group
    /// ceilings; they narrow, never widen. A trailing <c>*</c> matches by prefix, and an exact
    /// alias always wins over a prefix rule.
    /// </remarks>
    [Column(TypeName = "jsonb")]
    public string? ModelRateLimits { get; set; }

    /// <summary>
    /// Virtual collection of request logs
    /// </summary>
    public virtual ICollection<RequestLog>? RequestLogs { get; set; }

    /// <summary>
    /// Virtual collection of spend history
    /// </summary>
    public virtual ICollection<VirtualKeySpendHistory>? SpendHistory { get; set; }

    /// <summary>
    /// Virtual collection of notifications
    /// </summary>
    public virtual ICollection<Notification>? Notifications { get; set; }

    /// <summary>
    /// Virtual collection of per-key IP filters (allow/deny rules scoped to this key). Deleting the key
    /// cascades to its IP filters.
    /// </summary>
    public virtual ICollection<IpFilterEntity>? IpFilters { get; set; }


    /// <summary>
    /// Concurrency token for optimistic concurrency control
    /// </summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
