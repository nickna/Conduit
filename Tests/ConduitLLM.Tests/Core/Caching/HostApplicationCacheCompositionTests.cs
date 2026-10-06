using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Tests.Core.Caching;

[Collection("AdminCompositionEnvironment")]
public sealed class HostApplicationCacheCompositionTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void HostsResolveCompleteCacheGraphWithScopeValidation(bool gateway) => Resolve(gateway, null);

    [SkippableTheory]
    [InlineData(true)] [InlineData(false)]
    public void RedisConfiguredHostsResolveCompleteCacheGraphWithScopeValidation(bool gateway)
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrEmpty(redis), "Set CONDUIT_CACHE_TEST_REDIS for real Redis host composition.");
        Resolve(gateway, redis);
    }

    private static void Resolve(bool gateway, string? redis)
    {
        var originals = new[] { "DATABASE_URL", "REDIS_URL", "CONDUIT_REDIS_CONNECTION_STRING" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", "postgresql://conduit:conduit@localhost:5432/conduit_tests");
            Environment.SetEnvironmentVariable("REDIS_URL", null);
            Environment.SetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING", redis);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = gateway ? typeof(global::Program).Assembly.GetName().Name : typeof(ConduitLLM.Admin.Program).Assembly.GetName().Name,
                EnvironmentName = "Testing"
            });
            builder.Configuration["ApplicationCache:Environment"] = $"probe-{Guid.NewGuid():N}";
            if (gateway) { global::Program.ConfigureCoreServices(builder); global::Program.ConfigureCachingServices(builder); }
            else ConduitLLM.Admin.Program.ConfigureCoreServices(builder, NullLogger.Instance);
            using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
            using var scope = provider.CreateScope();
            var locks = provider.GetRequiredService<IDistributedLockProvider>();
            Assert.IsType<PostgresDistributedLockProvider>(locks);
            Assert.Same(locks, scope.ServiceProvider.GetRequiredService<IDistributedLockProvider>());
            var cache = provider.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey);
            Assert.Equal(redis is not null, cache.HasDistributedCache); Assert.Equal(redis is not null, cache.HasBackplane);
            if (gateway) Assert.IsType<FusionDiscoveryCacheService>(provider.GetRequiredService<IDiscoveryCacheService>());
            else Assert.Null(provider.GetService<IDiscoveryCacheService>());
            Assert.IsType<FusionFunctionDiscoveryCacheService>(scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>());
            Assert.IsType<FusionModelProviderMappingService>(scope.ServiceProvider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelProviderMappingService>());
            Assert.IsType<ModelMappingCacheInvalidator>(provider.GetRequiredService<IModelMappingCacheInvalidator>());
            Assert.IsType<FusionModelCostService>(scope.ServiceProvider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelCostService>());
            Assert.IsType<FusionPricingRulesService>(provider.GetRequiredService<ICachedPricingRulesService>());
            Assert.IsType<CostCalculationService>(scope.ServiceProvider.GetRequiredService<ICostCalculationService>());
            if (redis is null) Assert.IsType<MemoryDistributedCache>(provider.GetRequiredService<IDistributedCache>());
            else Assert.IsAssignableFrom<Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache>(provider.GetRequiredService<IDistributedCache>());
        }
        finally { foreach (var (name, value) in originals) Environment.SetEnvironmentVariable(name, value); }
    }
}
