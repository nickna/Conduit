using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Represents an IP address or subnet filter used for API access control.
/// Supports both IPv4 and IPv6 addresses with CIDR notation.
/// </summary>
/// <remarks>
/// The legacy namespace is retained so extracting this persisted contract does not
/// change callers or the EF model. The type intentionally has no dependency on the
/// Configuration or Functions entity hierarchies.
/// </remarks>
public class IpFilterEntity
{
    /// <summary>
    /// Unique identifier for the IP filter.
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Type of the IP filter (whitelist or blacklist).
    /// </summary>
    [Required]
    [StringLength(10)]
    public string FilterType { get; set; } = "blacklist";

    /// <summary>
    /// The IP address or subnet in CIDR notation.
    /// </summary>
    [Required]
    [StringLength(50)]
    public string IpAddressOrCidr { get; set; } = string.Empty;

    /// <summary>
    /// Optional display name for the filter rule.
    /// </summary>
    [StringLength(100)]
    public string? Name { get; set; }

    /// <summary>
    /// Optional description of the filter.
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether the filter is currently active.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Date when the filter was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date when the filter was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Username or identifier of who created the filter.
    /// </summary>
    [StringLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Username or identifier of who last updated the filter.
    /// </summary>
    [StringLength(100)]
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Optional virtual key this filter is scoped to. A null value denotes a
    /// global filter.
    /// </summary>
    public int? VirtualKeyId { get; set; }

    /// <summary>
    /// Concurrency token for optimistic concurrency control.
    /// </summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
