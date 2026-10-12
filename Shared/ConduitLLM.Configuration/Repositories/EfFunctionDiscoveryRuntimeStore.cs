#if !CONDUIT_NATIVE_AOT
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>JIT reference implementation of function discovery reads.</summary>
public sealed class EfFunctionDiscoveryRuntimeStore : IFunctionDiscoveryRuntimeStore
{
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfFunctionDiscoveryRuntimeStore(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async Task<IReadOnlyList<FunctionConfiguration>> ListEnabledAsync(
        FunctionPurpose? purpose = null,
        FunctionProviderType? providerType = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (purpose.HasValue && providerType.HasValue)
        {
            return await context.FunctionConfigurations.AsNoTracking()
                .Where(configuration => configuration.IsEnabled &&
                    configuration.Purpose == purpose.Value &&
                    configuration.ProviderType == providerType.Value)
                .ToListAsync(cancellationToken);
        }

        if (purpose.HasValue)
        {
            return await context.FunctionConfigurations.AsNoTracking()
                .Where(configuration => configuration.IsEnabled && configuration.Purpose == purpose.Value)
                .ToListAsync(cancellationToken);
        }

        if (providerType.HasValue)
        {
            return await context.FunctionConfigurations.AsNoTracking()
                .Where(configuration => configuration.IsEnabled && configuration.ProviderType == providerType.Value)
                .ToListAsync(cancellationToken);
        }

        return await context.FunctionConfigurations.AsNoTracking()
            .Where(configuration => configuration.IsEnabled)
            .ToListAsync(cancellationToken);
    }

    public async Task<FunctionConfiguration?> GetEnabledByIdAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.FunctionConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(configuration => configuration.Id == id && configuration.IsEnabled, cancellationToken);
    }
}
#endif
