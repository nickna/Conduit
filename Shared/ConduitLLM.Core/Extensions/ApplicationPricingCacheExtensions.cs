using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Core.Extensions;

public static class ApplicationPricingCacheExtensions
{
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
    public static IServiceCollection AddPricingRulesCache(this IServiceCollection services)
    {
        services.AddSingleton<ICachedPricingRulesService, FusionPricingRulesService>();
        return services;
    }
}
