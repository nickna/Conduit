using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Represents a global application setting.
/// </summary>
/// <remarks>
/// The compatibility namespace is retained while the persistence surface is
/// extracted incrementally. This type has no dependency on EF Core.
/// </remarks>
public class GlobalSetting
{
    /// <summary>
    /// Unique identifier for the setting.
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Setting key.
    /// </summary>
    [Required]
    [StringLength(100)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Setting value.
    /// </summary>
    [Required]
    [StringLength(2000)]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the setting.
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Date when the setting was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date when the setting was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
