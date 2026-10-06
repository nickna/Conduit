using System.ComponentModel.DataAnnotations;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Represents an IP address or subnet filter used for API access control.
/// Supports both IPv4 and IPv6 addresses with CIDR notation.
/// </summary>
public class IpFilterEntity : IEntity<int>, IAuditableEntity
{
    /// <summary>
    /// Unique identifier for the IP filter
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Type of the IP filter (whitelist or blacklist)
    /// </summary>
    [Required]
    [StringLength(10)]
    public string FilterType { get; set; } = "blacklist";

    /// <summary>
    /// The IP address or subnet in CIDR notation.
    /// Supports IPv4 (e.g., "192.168.1.1" or "192.168.1.0/24") and
    /// IPv6 (e.g., "2001:db8::1" or "2001:db8::/32")
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
    /// Optional description of the filter
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether the filter is currently active
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Date when the filter was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date when the filter was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Username or identifier of who created the filter (for audit trail)
    /// </summary>
    [StringLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Username or identifier of who last updated the filter (for audit trail)
    /// </summary>
    [StringLength(100)]
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Optional virtual key this filter is scoped to. When <c>null</c> the filter is GLOBAL (applies to
    /// all requests). When set, the filter applies only to requests authenticated with that virtual key,
    /// further restricting it on top of any global rules.
    /// </summary>
    public int? VirtualKeyId { get; set; }

    /// <summary>
    /// Concurrency token for optimistic concurrency control
    /// </summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
