using ConduitLLM.Functions.Entities;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>Uncached scalar pricing reads for function execution.</summary>
public interface IFunctionPricingRuntimeStore
{
    /// <summary>
    /// Finds costs referenced by active mappings through their canonical FunctionCostId.
    /// Eligible costs are active, effective at or before the supplied UTC instant, and
    /// unexpired (expiry is strictly after that instant). Selection orders priority then
    /// effective date descending; equal-ranked ties are unspecified. Provider/purpose do
    /// not restrict selection. IDs without eligible costs return null. Navigation graphs are not
    /// loaded; caching and validation of an execution request belong to the caller.
    /// </summary>
    Task<FunctionCost?> FindEligibleCostAsync(
        int functionConfigurationId, DateTime asOfUtc, CancellationToken cancellationToken = default);
}
