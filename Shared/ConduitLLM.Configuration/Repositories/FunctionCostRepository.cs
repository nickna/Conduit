using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// Repository implementation for function costs using RepositoryBase.
/// </summary>
public class FunctionCostRepository : RepositoryBase<FunctionCost, int>, IFunctionCostRepository
{
    public FunctionCostRepository(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        ILogger<FunctionCostRepository> logger)
        : base(dbContextFactory, logger) { }

    protected override DbSet<FunctionCost> GetDbSet(ConduitDbContext context)
        => context.FunctionCosts;

    protected override IQueryable<FunctionCost> ApplyDefaultIncludes(IQueryable<FunctionCost> query)
        => query.Include(c => c.FunctionMappings);

    protected override IQueryable<FunctionCost> ApplyDefaultOrdering(IQueryable<FunctionCost> query)
        => query.OrderBy(c => c.CostName);

    public async Task<FunctionCost?> GetByCostNameAsync(string costName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(costName))
        {
            throw new ArgumentException("Cost name cannot be null or empty", nameof(costName));
        }

        return await ExecuteAsync(async db =>
        {
            return await ApplyDefaultIncludes(GetDbSet(db).AsNoTracking())
                .FirstOrDefaultAsync(c => c.CostName == costName, cancellationToken);
        }, cancellationToken, "GetByCostName");
    }

    public async Task<List<FunctionCost>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async db =>
        {
            var now = DateTime.UtcNow;
            return await ApplyDefaultIncludes(GetDbSet(db).AsNoTracking())
                .Where(c => c.IsActive
                    && c.EffectiveDate <= now
                    && (c.ExpiryDate == null || c.ExpiryDate > now))
                .OrderBy(c => c.CostName)
                .ToListAsync(cancellationToken);
        }, cancellationToken, "GetAllActive");
    }

    public async Task<FunctionCost?> GetActiveCostForFunctionAsync(int functionConfigurationId, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async db =>
        {
            var now = DateTime.UtcNow;

            var mapping = await db.FunctionCostMappings
                .AsNoTracking()
                .Include(m => m.FunctionCost)
                .Where(m => m.FunctionConfigurationId == functionConfigurationId && m.IsActive)
                .Where(m => m.FunctionCost != null
                    && m.FunctionCost.IsActive
                    && m.FunctionCost.EffectiveDate <= now
                    && (m.FunctionCost.ExpiryDate == null || m.FunctionCost.ExpiryDate > now))
                .OrderByDescending(m => m.FunctionCost!.Priority)
                .ThenByDescending(m => m.FunctionCost!.EffectiveDate)
                .FirstOrDefaultAsync(cancellationToken);

            return mapping?.FunctionCost;
        }, cancellationToken, "GetActiveCostForFunction");
    }
}
