#if !CONDUIT_NATIVE_AOT
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>JIT reference implementation of pre-execution scalar reads.</summary>
public sealed class EfFunctionExecutionConfigurationStore : IFunctionExecutionConfigurationStore
{
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfFunctionExecutionConfigurationStore(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async Task<FunctionConfiguration?> GetConfigurationAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var configurationId = id;
        var queryCancellationToken = cancellationToken;
        return await context.FunctionConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(configuration => configuration.Id == configurationId, queryCancellationToken);
    }

    public async Task<IReadOnlyList<FunctionConfiguration>> GetConfigurationsAsync(
        IReadOnlyList<int>? ids, CancellationToken cancellationToken = default)
    {
        if (ids is null || ids.Count == 0) return Array.Empty<FunctionConfiguration>();
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var configurationIds = ids;
        var queryCancellationToken = cancellationToken;
        return await context.FunctionConfigurations.AsNoTracking()
            .Where(configuration => configurationIds.Contains(configuration.Id)).ToListAsync(queryCancellationToken);
    }

    public async Task<IReadOnlyList<FunctionCredential>> ListCredentialsForProviderAsync(
        FunctionProviderType providerType, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var credentialProviderType = providerType;
        var queryCancellationToken = cancellationToken;
        return await context.FunctionCredentials.AsNoTracking()
            .Where(credential => credential.ProviderType == credentialProviderType)
            .OrderByDescending(credential => credential.IsPrimary).ThenBy(credential => credential.KeyName)
            .ToListAsync(queryCancellationToken);
    }
}
#endif
