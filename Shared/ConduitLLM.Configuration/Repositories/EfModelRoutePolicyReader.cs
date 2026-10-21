using ConduitLLM.Configuration.Entities;
using ConduitLLM.Persistence.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>Preserves the JIT route-policy query behind a named persistence operation.</summary>
public sealed class EfModelRoutePolicyReader(IDbContextFactory<ConduitDbContext> dbContextFactory)
    : IModelRoutePolicyReader
{
    /// <inheritdoc />
    public async Task<ModelRoutePolicy?> GetByAliasAsync(
        string modelAlias, CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ModelRoutePolicies.AsNoTracking()
            .SingleOrDefaultAsync(policy => policy.ModelAlias == modelAlias, cancellationToken);
    }
}
