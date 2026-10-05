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

    private static void Resolve(bool gateway, string? redis, bool migrated = false)
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
            if (migrated)
                foreach (var domain in new[] { "Discovery", "Functions", "Mappings" })
                    builder.Configuration[$"ApplicationCache:Implementations:{domain}"] = "FusionCache";
            if (gateway)
            {
                if (migrated) global::Program.ConfigureCoreServices(builder);
                global::Program.ConfigureCachingServices(builder);
            }
            else ConduitLLM.Admin.Program.ConfigureCoreServices(builder, NullLogger.Instance);
            using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
            var cache = provider.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey);
            Assert.Equal(redis is not null, cache.HasDistributedCache);
            Assert.Equal(redis is not null, cache.HasBackplane);
            Assert.NotNull(provider.GetRequiredService<ICacheManager>());
            if (migrated)
            {
                using var scope = provider.CreateScope();
                if (gateway) Assert.IsType<FusionDiscoveryCacheService>(provider.GetRequiredService<IDiscoveryCacheService>());
                else Assert.Null(provider.GetService<IDiscoveryCacheService>()); // Admin discovery is intentionally optional.
                Assert.IsType<FusionFunctionDiscoveryCacheService>(scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>());
                Assert.IsType<FusionModelProviderMappingService>(scope.ServiceProvider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelProviderMappingService>());
                Assert.IsType<ModelMappingCacheInvalidator>(provider.GetRequiredService<IModelMappingCacheInvalidator>());
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
