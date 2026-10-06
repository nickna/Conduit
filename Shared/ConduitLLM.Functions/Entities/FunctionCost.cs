using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ConduitLLM.Functions.Entities.Interfaces;
using ConduitLLM.Functions.Enums;

namespace ConduitLLM.Functions.Entities;

/// <summary>
/// Represents a cost configuration for function executions.
/// Supports multiple pricing models via the Strategy pattern.
/// </summary>
[Table("FunctionCosts")]
public class FunctionCost : IIdentifiableEntity<int>
{
    /// <summary>
    /// Unique identifier for this cost configuration
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Name for this cost configuration (e.g., "Exa Basic Search")
    /// </summary>
    [Required]
    [StringLength(200)]
    public required string CostName { get; set; }

    /// <summary>
    /// Provider type this cost applies to (e.g., Exa, Tavily, Perplexity)
    /// </summary>
    [Required]
    public FunctionProviderType ProviderType { get; set; }

    /// <summary>
    /// Optional purpose filter (Search, Answer, etc.). Null means applies to any purpose.
    /// </summary>
    public FunctionPurpose? Purpose { get; set; }

    /// <summary>
    /// Optional description of this cost configuration
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional base cost added to all executions regardless of pricing model
    /// </summary>
    [Column(TypeName = "decimal(18,8)")]
    public decimal? BaseCost { get; set; }

    /// <summary>
    /// Pricing model strategy used for cost calculation
    /// </summary>
    [Required]
    public FunctionPricingModel PricingModel { get; set; }

    /// <summary>
    /// Cost per execution (used for FlatRate model)
    /// </summary>
    [Column(TypeName = "decimal(18,8)")]
    public decimal? CostPerExecution { get; set; }

    /// <summary>
    /// Cost per result returned (used for PerResult model, e.g., per search result)
    /// </summary>
    [Column(TypeName = "decimal(18,8)")]
    public decimal? CostPerResult { get; set; }

    /// <summary>
    /// Cost per token (used for PerToken model, for Answer functions)
    /// </summary>
    [Column(TypeName = "decimal(18,8)")]
    public decimal? CostPerToken { get; set; }

    /// <summary>
    /// Cost per minute of execution (used for TimeBased model)
    /// </summary>
    [Column(TypeName = "decimal(18,8)")]
    public decimal? CostPerMinute { get; set; }

    /// <summary>
    /// Tiered pricing configuration (used for Tiered model)
    /// Stored as JSON: {"tiers": [{"min": 1, "max": 100, "costPerResult": 0.00025}, ...]}
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? TieredPricing { get; set; }

    /// <summary>
    /// Complex pricing configuration for Hybrid model
    /// Stored as JSON for maximum flexibility
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? PricingConfiguration { get; set; }

    /// <summary>
    /// Whether this cost configuration is currently active
    /// </summary>
    [Required]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When this pricing becomes effective
    /// </summary>
    [Required]
    public DateTime EffectiveDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this pricing expires (null = no expiration)
    /// </summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Priority for selecting cost config when multiple are active (higher number = higher priority)
    /// </summary>
    [Required]
    public int Priority { get; set; } = 1;

    /// <summary>
    /// When this cost configuration was created
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this cost configuration was last updated
    /// </summary>
    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties

    /// <summary>
    /// Mappings to function configurations that use this cost configuration
    /// </summary>
    public ICollection<FunctionCostMapping> FunctionMappings { get; set; } = new List<FunctionCostMapping>();
}
