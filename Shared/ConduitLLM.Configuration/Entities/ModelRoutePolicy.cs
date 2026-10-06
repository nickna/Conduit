using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities;

/// <summary>Provider routing policy for a chat model alias.</summary>
public sealed class ModelRoutePolicy : IEntity<int>, IAuditableEntity
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    [Required, StringLength(100)]
    public string ModelAlias { get; set; } = string.Empty;
    [Required, StringLength(30)]
    public string Strategy { get; set; } = "Balanced";
    [Column(TypeName = "decimal(4, 3)")] public decimal CostWeight { get; set; } = 0.40m;
    [Column(TypeName = "decimal(4, 3)")] public decimal SpeedWeight { get; set; } = 0.30m;
    [Column(TypeName = "decimal(4, 3)")] public decimal QualityWeight { get; set; } = 0.30m;
    public bool CacheAffinityEnabled { get; set; } = true;
    public int AffinityTtlSeconds { get; set; } = 1800;
    [Column(TypeName = "decimal(4, 3)")] public decimal MaxAffinityScorePenalty { get; set; } = 0.10m;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
