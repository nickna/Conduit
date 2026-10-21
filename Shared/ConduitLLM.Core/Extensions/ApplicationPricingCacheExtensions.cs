using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Core.Extensions;

public static class ApplicationPricingCacheExtensions
{
    public static IServiceCollection AddPricingRulesCache(this IServiceCollection services)
    {
        services.AddSingleton<ICachedPricingRulesService, FusionPricingRulesService>();
        return services;
    }
}
