using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Persistence.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConduitLLM.Core.Extensions;

/// <summary>Composes persistence implementations with shared application decorators for the hosts.</summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers the shared EF application services and cache decorators.</summary>
    public static IServiceCollection AddSharedApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<IGlobalSettingsCacheService, GlobalSettingsCacheService>();
        services.AddHostedService(provider =>
            provider.GetRequiredService<IGlobalSettingsCacheService>() as GlobalSettingsCacheService
            ?? throw new InvalidOperationException("GlobalSettingsCacheService must be registered as singleton"));

        services.AddScoped<IProviderService, ProviderService>();
        services.TryAddScoped<IModelRoutePolicyReader, EfModelRoutePolicyReader>();
        services.AddSingleton<IModelMappingCacheInvalidator, ConduitLLM.Core.Caching.ModelMappingCacheInvalidator>();
        services.AddScoped<ModelProviderMappingService>();
        services.AddScoped<IModelProviderMappingService>(provider =>
        {
            var inner = provider.GetRequiredService<ModelProviderMappingService>();
            return ActivatorUtilities.CreateInstance<FusionModelProviderMappingService>(provider, inner);
        });
        return services;
    }

    /// <summary>Registers the EF pricing service with the shared pricing cache decorator.</summary>
    public static IServiceCollection AddModelCostCache(this IServiceCollection services)
    {
        services.AddScoped<ModelCostService>();
        services.AddScoped<IModelCostService>(provider =>
        {
            var inner = provider.GetRequiredService<ModelCostService>();
            return ActivatorUtilities.CreateInstance<FusionModelCostService>(provider, inner);
        });
        return services;
    }
}
