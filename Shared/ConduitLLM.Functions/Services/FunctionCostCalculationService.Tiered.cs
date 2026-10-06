using System.Text.Json;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Models.Pricing;

namespace ConduitLLM.Functions.Services;

/// <summary>
/// Tiered pricing model calculations for FunctionCostCalculationService.
/// </summary>
public partial class FunctionCostCalculationService
{
    /// <summary>
    /// Calculates cost using tiered pricing model.
    /// </summary>
    /// <param name="functionCost">The function cost configuration.</param>
    /// <param name="usage">The usage data containing result count or other billable units.</param>
    /// <returns>The calculated cost based on tiered pricing.</returns>
    /// <remarks>
    /// Tiered pricing applies different rates based on usage volume:
    ///
    /// Example configuration:
    /// - Tier 1: 1-100 results @ $0.01 per result = $1.00 max
    /// - Tier 2: 101-1000 results @ $0.008 per result
    /// - Tier 3: 1001+ results @ $0.005 per result
    ///
    /// For 150 results:
    /// - First 100 @ $0.01 = $1.00
    /// - Next 50 @ $0.008 = $0.40
    /// - Total = $1.40
    ///
    /// Configuration is stored as JSON in TieredPricing field.
    /// This model is ideal for volume-based discounts.
    /// </remarks>
    private decimal CalculateTieredCost(FunctionCost functionCost, FunctionExecutionUsage usage)
    {
        // Parse tiered pricing configuration
        if (string.IsNullOrWhiteSpace(functionCost.TieredPricing))
        {
            _logger.LogWarning("Tiered pricing model configured but TieredPricing JSON is null/empty for cost {CostName}. Returning 0.",
                functionCost.CostName);
            return 0m;
        }

        TieredPricingConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(
                functionCost.TieredPricing,
                FunctionsJsonContext.Default.TieredPricingConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse tiered pricing configuration for cost {CostName}. Returning 0.",
                functionCost.CostName);
            return 0m;
        }

        if (config == null || config.Tiers == null || !config.Tiers.Any())
        {
            _logger.LogWarning("Tiered pricing configuration is empty for cost {CostName}. Returning 0.",
                functionCost.CostName);
            return 0m;
        }

        // Determine the billable unit count (defaults to ResultCount)
        int unitCount = usage.ResultCount ?? 0;

        if (unitCount == 0)
        {
            _logger.LogDebug("No units to bill for tiered pricing. Returning 0 cost.");
            return 0m;
        }

        // Calculate cost across tiers
        decimal totalCost = 0m;
        // Null minima depend on the preceding tier, so their declaration order
        // is significant. Fully explicit configurations can still be normalized.
        var sortedTiers = config.Tiers.Any(t => !t.MinThreshold.HasValue)
            ? config.Tiers
            : config.Tiers.OrderBy(t => t.MinThreshold).ToList();

        int nextInferredMin = 1;

        foreach (var tier in sortedTiers)
        {
            // An omitted minimum continues after the preceding tier. Explicit
            // minima are preserved so that gaps in a configuration are not
            // silently charged at the following tier's rate.
            int tierMin = tier.MinThreshold ?? nextInferredMin;
            int tierMax = tier.MaxThreshold ?? int.MaxValue;

            if (tier.MaxThreshold.HasValue)
            {
                nextInferredMin = tierMax == int.MaxValue ? int.MaxValue : tierMax + 1;
            }

            // Usage is a count, so its first billable unit is 1 even when a
            // configuration explicitly uses a zero-based lower threshold.
            int firstBillableUnit = Math.Max(1, tierMin);
            int lastBillableUnit = Math.Min(unitCount, tierMax);
            if (lastBillableUnit >= firstBillableUnit)
            {
                int unitsInTier = lastBillableUnit - firstBillableUnit + 1;

                decimal tierCost = unitsInTier * tier.CostPerUnit;
                totalCost += tierCost;

                _logger.LogDebug("Tier [{Min}-{Max}]: {UnitsInTier} units × ${CostPerUnit} = ${TierCost}",
                    tierMin, tierMax == int.MaxValue ? "∞" : tierMax.ToString(),
                    unitsInTier, tier.CostPerUnit, tierCost);
            }
        }

        _logger.LogDebug("Tiered pricing total: {TotalUnits} {BillingUnit}s across {TierCount} tiers = ${TotalCost}",
            unitCount, config.BillingUnit, sortedTiers.Count, totalCost);

        return totalCost;
    }
}
