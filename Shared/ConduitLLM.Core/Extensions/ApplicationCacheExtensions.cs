using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;

namespace ConduitLLM.Core.Extensions;

public static class ApplicationCacheExtensions
{
    /// <summary>Registers a dedicated shared application cache without rebinding the host IDistributedCache.</summary>
    public static IServiceCollection AddConduitApplicationCache(this IServiceCollection services,
        IConfiguration configuration, string hostEnvironment, string? redisConnectionString = null)
    {
        var options = ApplicationCacheOptions.Read(configuration, hostEnvironment);
        var redis = redisConnectionString ?? RedisUrlParser.ResolveConnectionString();
        services.AddMemoryCache(); // existing host-local consumers keep their independently registered cache
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ApplicationCacheSerializer>();
        services.TryAddSingleton(provider => new ApplicationCacheGeneration(
            provider.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey), options, redis));
        if (!string.IsNullOrWhiteSpace(redis))
            services.TryAddKeyedSingleton<IDistributedCache>(ApplicationCacheOptions.ServiceKey,
                (_, _) => new ApplicationRedisCache(redis, options.DistributedReadTimeout));

        services.TryAddKeyedSingleton<IFusionCache>(ApplicationCacheOptions.ServiceKey, (provider, _) =>
        {
            // A dedicated L1 is owned by FusionCache; no singleton captures a scoped loader.
            var cache = new FusionCache(options.FusionOptions(), logger: provider.GetService<ILogger<FusionCache>>());
            cache.SetupSerializer(provider.GetRequiredService<ApplicationCacheSerializer>());
            ApplicationCacheMetrics.Attach(cache, options.Prefix);
            if (!string.IsNullOrWhiteSpace(redis))
            {
                // DI owns this RedisCache; FusionCache owns its separately-connected backplane.
                cache.SetupDistributedCache(provider.GetRequiredKeyedService<IDistributedCache>(ApplicationCacheOptions.ServiceKey));
                cache.SetupBackplane(new RedisBackplane(new RedisBackplaneOptions { Configuration = redis }));
            }
            return cache;
        });
        return services;
    }
}
