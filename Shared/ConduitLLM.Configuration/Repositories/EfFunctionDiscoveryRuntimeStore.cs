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
        var queryCancellationToken = cancellationToken;
        if (purpose.HasValue && providerType.HasValue)
        {
            var selectedPurpose = purpose.Value;
            var selectedProvider = providerType.Value;
            return await context.FunctionConfigurations.AsNoTracking()
                .Where(configuration => configuration.IsEnabled &&
                    configuration.Purpose == selectedPurpose &&
                    configuration.ProviderType == selectedProvider)
                .ToListAsync(queryCancellationToken);
        }

        if (purpose.HasValue)
        {
            var selectedPurpose = purpose.Value;
            return await context.FunctionConfigurations.AsNoTracking()
                .Where(configuration => configuration.IsEnabled && configuration.Purpose == selectedPurpose)
                .ToListAsync(queryCancellationToken);
        }

        if (providerType.HasValue)
        {
            var selectedProvider = providerType.Value;
            return await context.FunctionConfigurations.AsNoTracking()
                .Where(configuration => configuration.IsEnabled && configuration.ProviderType == selectedProvider)
                .ToListAsync(queryCancellationToken);
        }

        return await context.FunctionConfigurations.AsNoTracking()
            .Where(configuration => configuration.IsEnabled)
            .ToListAsync(queryCancellationToken);
    }

    public async Task<FunctionConfiguration?> GetEnabledByIdAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var queryId = id;
        var queryCancellationToken = cancellationToken;
        return await context.FunctionConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(configuration => configuration.Id == queryId && configuration.IsEnabled, queryCancellationToken);
    }
}
#endif
