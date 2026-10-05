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
    [InlineData(true)]
    [InlineData(false)]
    public void HostsResolveCacheGraphWithScopeValidation(bool gateway) => Resolve(gateway, null);

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void RedisConfiguredHostsResolveCacheGraphWithScopeValidation(bool gateway)
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrEmpty(redis), "Set CONDUIT_CACHE_TEST_REDIS to run real Redis host composition.");
        Resolve(gateway, redis);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void MigratedDomainSelectionsResolveActualHostServices(bool gateway) => Resolve(gateway, null, migrated: true);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void IncrementalRolloutResolvesBothHostsInDomainOrder(int stage)
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Resolve(true, redis, stage: stage); Resolve(false, redis, stage: stage);
    }

    private static void Resolve(bool gateway, string? redis, bool migrated = false, int? stage = null)
    {
        var originals = new[] { "DATABASE_URL", "REDIS_URL", "CONDUIT_REDIS_CONNECTION_STRING" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
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
            if (migrated || stage.HasValue)
                foreach (var domain in new[] { "Discovery", "Functions", "Mappings", "Costs", "PricingRules" }.Take(stage ?? 5))
                    builder.Configuration[$"ApplicationCache:Implementations:{domain}"] = "FusionCache";
            if (gateway)
            {
                if (migrated || stage.HasValue) global::Program.ConfigureCoreServices(builder);
                global::Program.ConfigureCachingServices(builder);
            }
            else ConduitLLM.Admin.Program.ConfigureCoreServices(builder, NullLogger.Instance);
            using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
            var cache = provider.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey);
            Assert.Equal(redis is not null, cache.HasDistributedCache);
            Assert.Equal(redis is not null, cache.HasBackplane);
            Assert.NotNull(provider.GetRequiredService<ICacheManager>());
            if (stage is { } selected)
            {
                using var scope = provider.CreateScope();
                if (gateway) Assert.Equal(selected >= 1, provider.GetRequiredService<IDiscoveryCacheService>() is FusionDiscoveryCacheService);
                Assert.Equal(selected >= 2, scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>() is FusionFunctionDiscoveryCacheService);
                Assert.Equal(selected >= 3, scope.ServiceProvider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelProviderMappingService>() is FusionModelProviderMappingService);
                Assert.Equal(selected >= 4, scope.ServiceProvider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelCostService>() is FusionModelCostService);
                Assert.Equal(selected >= 5, provider.GetRequiredService<ICachedPricingRulesService>() is FusionPricingRulesService);
            }
            if (migrated)
            {
                using var scope = provider.CreateScope();
                if (gateway) Assert.IsType<FusionDiscoveryCacheService>(provider.GetRequiredService<IDiscoveryCacheService>());
                else Assert.Null(provider.GetService<IDiscoveryCacheService>()); // Admin discovery is intentionally optional.
                Assert.IsType<FusionFunctionDiscoveryCacheService>(scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>());
                Assert.IsType<FusionModelProviderMappingService>(scope.ServiceProvider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelProviderMappingService>());
                Assert.IsType<ModelMappingCacheInvalidator>(provider.GetRequiredService<IModelMappingCacheInvalidator>());
                Assert.IsType<FusionModelCostService>(scope.ServiceProvider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelCostService>());
                Assert.IsType<FusionPricingRulesService>(provider.GetRequiredService<ICachedPricingRulesService>());
                Assert.IsType<CostCalculationService>(scope.ServiceProvider.GetRequiredService<ICostCalculationService>());
            }
            if (redis is null) Assert.IsType<MemoryDistributedCache>(provider.GetRequiredService<IDistributedCache>());
            else Assert.IsAssignableFrom<Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache>(provider.GetRequiredService<IDistributedCache>());
        }
        finally
        {
            foreach (var (name, value) in originals) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
