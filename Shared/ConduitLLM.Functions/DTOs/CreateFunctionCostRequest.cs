using System.ComponentModel.DataAnnotations;
using ConduitLLM.Functions.Enums;

namespace ConduitLLM.Functions.DTOs;

/// <summary>
/// Request DTO for creating a new function cost configuration
/// </summary>
public class CreateFunctionCostRequest
{
    /// <summary>
    /// Name for this cost configuration
    /// </summary>
    [Required]
    [StringLength(200)]
    public required string CostName { get; set; }

    /// <summary>
    /// Pricing model strategy
    /// </summary>
    [Required]
    public FunctionPricingModel PricingModel { get; set; }

    /// <summary>
    /// Cost per execution (for FlatRate model)
    /// </summary>
    public decimal? CostPerExecution { get; set; }

    /// <summary>
    /// Cost per result (for PerResult model)
    /// </summary>
    public decimal? CostPerResult { get; set; }

    /// <summary>
    /// Cost per token (for PerToken model)
    /// </summary>
    public decimal? CostPerToken { get; set; }

    /// <summary>
    /// Cost per minute (for TimeBased model)
    /// </summary>
    public decimal? CostPerMinute { get; set; }

    /// <summary>
    /// Tiered pricing configuration as JSON
    /// </summary>
    public string? TieredPricing { get; set; }

    /// <summary>
    /// Complex pricing configuration for Hybrid model
    /// </summary>
    public string? PricingConfiguration { get; set; }

    /// <summary>
    /// Whether this cost configuration is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When this pricing becomes effective
    /// </summary>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// When this pricing expires
    /// </summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Priority for selecting cost config
    /// </summary>
    public int Priority { get; set; } = 1;
}
