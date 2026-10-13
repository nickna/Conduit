using ConduitLLM.Configuration.Utilities;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

using ConduitLLM.Configuration.Constants;

namespace ConduitLLM.Configuration.DTOs.IpFilter;

/// <summary>
/// Data Transfer Object for IP filtering rules
/// </summary>
public class IpFilterDto : IVersionedRepresentation
{
    /// <summary>Version of the database snapshot represented by this response.</summary>
    [JsonIgnore]
    public string? ResourceETag { get; set; }

    /// <summary>
    /// Unique identifier for the IP filter
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Type of the IP filter (whitelist or blacklist)
    /// </summary>
    [Required]
    [StringLength(10)]
    public string FilterType { get; set; } = IpFilterConstants.BLACKLIST;

    /// <summary>
    /// The IP address or subnet in CIDR notation (e.g., "192.168.1.1" or "192.168.1.0/24")
    /// </summary>
    [Required]
    [StringLength(50)]
    public string IpAddressOrCidr { get; set; } = string.Empty;


    /// <summary>
    /// Name of the IP filter rule
    /// </summary>
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

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
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Date when the filter was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Username or identifier of who created the filter
    /// </summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Username or identifier of who last updated the filter
    /// </summary>
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Virtual key this filter is scoped to, or null if it is a global filter.
    /// </summary>
    public int? VirtualKeyId { get; set; }
}
