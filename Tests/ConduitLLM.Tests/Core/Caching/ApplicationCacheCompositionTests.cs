using System.Text.Json;
using System.Text;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Core.Caching;

public class ApplicationCacheCompositionTests
{
    private static IConfiguration Configuration(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();

    [Fact]
    public async Task LocalComposition_IsIdempotentAndLeavesHostDistributedStoreAlone()
    {
        var services = new ServiceCollection().AddLogging().AddDistributedMemoryCache();
        services.AddConduitApplicationCache(Configuration(), "Test", "");
        services.AddConduitApplicationCache(Configuration(), "Test", "");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var host = provider.GetRequiredService<IDistributedCache>();
        var cache = provider.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey);
        Assert.Same(cache, provider.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey));
        Assert.IsType<Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache>(host);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IFusionCache));
        Assert.False(cache.HasDistributedCache);
        Assert.False(cache.HasBackplane);
        await cache.SetAsync("discovery:owned", new DiscoveryModelsResult { Count = 1 });
        var first = await cache.GetOrDefaultAsync<DiscoveryModelsResult>("discovery:owned");
        first!.Count = 99;
        Assert.Equal(1, (await cache.GetOrDefaultAsync<DiscoveryModelsResult>("discovery:owned"))!.Count);
    }

    [Fact]
    public void Options_DefaultToLegacyAndSelectDomainsIndependently()
    {
        var services = new ServiceCollection();
        services.AddConduitApplicationCache(Configuration(new()
        {
            ["ApplicationCache:Environment"] = "Shared-Test",
            ["ApplicationCache:Implementations:Discovery"] = "FusionCache"
        }), "Admin", "");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var options = provider.GetRequiredService<ApplicationCacheOptions>();
        Assert.Equal("conduit:app-cache:shared-test:v1:", options.Prefix);
        Assert.True(options.UsesFusionCache(ApplicationCacheDomain.Discovery));
        Assert.False(options.UsesFusionCache(ApplicationCacheDomain.Functions));
        Assert.False(options.UsesFusionCache(ApplicationCacheDomain.Mappings));
        Assert.False(options.UsesFusionCache(ApplicationCacheDomain.Costs));
        Assert.False(options.UsesFusionCache(ApplicationCacheDomain.PricingRules));
        var entry = options.Entry(TimeSpan.FromHours(12));
        Assert.Equal(TimeSpan.FromSeconds(5), entry.Duration);
        Assert.Equal(TimeSpan.FromHours(12), entry.DistributedCacheDuration);
        Assert.False(entry.IsFailSafeEnabled);
        Assert.False(entry.AllowBackgroundDistributedCacheOperations);
        Assert.False(entry.AllowBackgroundBackplaneOperations);
        Assert.False(entry.AllowTimedOutFactoryBackgroundCompletion);
        Assert.True(entry.ReThrowDistributedCacheExceptions);
        Assert.True(entry.ReThrowBackplaneExceptions);
        Assert.True(entry.ReThrowSerializationExceptions);
        Assert.False(options.FusionOptions().TagsDefaultEntryOptions.IsFailSafeEnabled);
        Assert.True(options.FusionOptions().TagsDefaultEntryOptions.DistributedCacheDuration > options.MaximumDuration);
    }

    [Theory]
    [InlineData("Environment", "bad:namespace")]
    [InlineData("LocalDuration", "00:00:06")]
    [InlineData("MaximumDuration", "00:01:00")]
    [InlineData("DistributedReadTimeout", "00:00:02")]
    [InlineData("Implementations:Costs", "999")]
    public void InvalidPoliciesFailBeforeCacheResolution(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddConduitApplicationCache(
            Configuration(new() { [$"ApplicationCache:{key}"] = value }), "Test", ""));
    }

    [Fact]
    public void Serializer_UsesOnlyGeneratedPayloadAndEnvelopeMetadata()
    {
        var serializer = new ApplicationCacheSerializer();
        var payload = new DiscoveryModelsResult { Count = 1, Data = [JsonDocument.Parse("""{"pricing":{"cost":0.25}}""").RootElement.Clone()] };
        var clone = serializer.Deserialize<DiscoveryModelsResult>(serializer.Serialize(payload));
        Assert.NotSame(payload, clone);
        Assert.Equal(0.25m, clone!.Data[0].GetProperty("pricing").GetProperty("cost").GetDecimal());
        Assert.Throws<NotSupportedException>(() => serializer.Serialize(new Uri("https://example.invalid")));
    }

    [Fact]
    public async Task RedisFailure_IsVisibleAndDistinctFromBusinessLoadFailure()
    {
        using var adapter = new ApplicationRedisCache("127.0.0.1:1,abortConnect=false,connectTimeout=100,asyncTimeout=100,syncTimeout=100,connectRetry=0");
        var failure = await Record.ExceptionAsync(() => adapter.GetAsync("discovery:unavailable").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(failure is RedisException or TimeoutException);
        using var output = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(output);
        var metrics = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("domain=\"shared\",operation=\"redis_read\",result=\"redis_error\"", metrics);
        Assert.DoesNotContain("discovery:unavailable", metrics);
    }
}
