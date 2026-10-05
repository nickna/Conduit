using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Extensions;

public static class ApplicationPricingCacheExtensions
{
    public static IServiceCollection AddModelCostCache(this IServiceCollection services)
    {
        services.AddScoped<ModelCostService>();
        services.AddScoped<IModelCostService>(provider =>
        {
            var inner = provider.GetRequiredService<ModelCostService>();
            if (provider.GetService<ApplicationCacheOptions>()?.UsesFusionCache(ApplicationCacheDomain.Costs) == true)
                return ActivatorUtilities.CreateInstance<FusionModelCostService>(provider, inner);
            return new CachedModelCostService(inner, provider.GetRequiredService<ICacheManager>(), provider.GetRequiredService<ILogger<CachedModelCostService>>());
        });
        return services;
    }
    public static IServiceCollection AddPricingRulesCache(this IServiceCollection services)
    {
        services.AddSingleton<ICachedPricingRulesService>(provider =>
            provider.GetService<ApplicationCacheOptions>()?.UsesFusionCache(ApplicationCacheDomain.PricingRules) == true
                ? ActivatorUtilities.CreateInstance<FusionPricingRulesService>(provider)
                : ActivatorUtilities.CreateInstance<CachedPricingRulesService>(provider));
        return services;
    }
}
