#if !CONDUIT_NATIVE_AOT
using ConduitLLM.Functions.Entities;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>JIT reference implementation of uncached function pricing selection.</summary>
public sealed class EfFunctionPricingRuntimeStore : IFunctionPricingRuntimeStore
{
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfFunctionPricingRuntimeStore(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async Task<FunctionCost?> FindEligibleCostAsync(
        int functionConfigurationId, DateTime asOfUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.FunctionCosts.AsNoTracking()
            .Where(cost => context.FunctionCostMappings.Any(mapping =>
                mapping.FunctionConfigurationId == functionConfigurationId && mapping.IsActive &&
                mapping.FunctionCostId == cost.Id))
            .Where(cost => cost.IsActive && cost.EffectiveDate <= asOfUtc &&
                (cost.ExpiryDate == null || cost.ExpiryDate > asOfUtc))
            .OrderByDescending(cost => cost.Priority)
            .ThenByDescending(cost => cost.EffectiveDate)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
#endif
