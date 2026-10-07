using System.Text.Json;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Tests.Core.Caching;

public sealed class FusionPricingCacheTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static ServiceProvider Host(IModelCostService inner, Clock? clock = null, string? redis = null,
        string? environment = null, bool enabled = true, TimeSpan? healthyReadTimeout = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ApplicationCache:Environment"] = environment ?? $"test-{Guid.NewGuid():N}",
            ["ApplicationCache:DistributedReadTimeout"] = (healthyReadTimeout ?? TimeSpan.FromMilliseconds(250)).ToString(),
            ["ApplicationCache:Domains:Costs:Enabled"] = enabled.ToString(),
            ["ApplicationCache:Domains:PricingRules:Enabled"] = enabled.ToString() }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(clock ?? new Clock());
        services.AddConduitApplicationCache(configuration, "test", redis ?? "");
        services.AddSingleton<ICachedPricingRulesService, FusionPricingRulesService>();
        services.AddScoped<IModelCostService>(provider => ActivatorUtilities.CreateInstance<FusionModelCostService>(provider, inner));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
    private static ModelCost Cost() => new()
    { Id = 42, IsActive = true, EffectiveDate = DateTime.UtcNow.AddDays(-1), CostName = "fixture",
        InputCostPerMillionTokens = 0.25m, PricingConfiguration = Rules,
        ModelProviderTypeAssociations = [new() { Id = 3, Identifier = "model", ModelId = 4,
            Model = new() { Id = 4, Name = "model", SupportsChat = true, Series = new() { Id = 5, Name = "series" } } }] };
    private const string Rules = """{"DefaultRate":0.25,"Rules":[{"Rate":0.5,"Conditions":{"resolution":"1024x1024"}}],"Constraints":{"AllowedResolutions":["1024x1024"]}}""";
    private static Mock<IModelCostService> Inner(ModelCost? cost)
    {
        var inner = new Mock<IModelCostService>();
        inner.Setup(service => service.GetCostForModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        inner.Setup(service => service.GetCostByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        inner.Setup(service => service.ListModelCostsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(cost is null ? [] : [cost]);
        inner.Setup(service => service.UpdateModelCostAsync(It.IsAny<ModelCost>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        inner.Setup(service => service.DeleteModelCostAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return inner;
    }

    [Fact]
    public async Task ConcurrentBillingMissesCoalesceAndCostGraphsAreOwned()
    {
        var cost = Cost(); var inner = Inner(cost);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Setup(service => service.GetCostForModelAsync("model", It.IsAny<CancellationToken>())).Returns(async () =>
        { entered.TrySetResult(); await release.Task; return cost; });
        using var host = Host(inner.Object); using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IModelCostService>();
        var requests = Enumerable.Range(0, 32).Select(_ => cache.GetCostForModelAsync("model")).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); release.SetResult();
        var results = await Task.WhenAll(requests);
        inner.Verify(service => service.GetCostForModelAsync("model", It.IsAny<CancellationToken>()), Times.Once);
        results[0]!.InputCostPerMillionTokens = 99;
        results[0]!.ModelProviderTypeAssociations!.Single().Model!.Name = "changed";
        cost.ModelProviderTypeAssociations!.Clear();
        Assert.All(results.Skip(1), value => Assert.Equal("model", value!.ModelProviderTypeAssociations!.Single().Model!.Name));
        Assert.Equal(0.25m, (await cache.GetCostForModelAsync("model"))!.InputCostPerMillionTokens);
    }

    [Fact]
    public async Task MissingCostExpiresAfterOneMinuteAndCreationClearsEveryNegativeVariant()
    {
        var clock = new Clock(); var inner = Inner(null);
        using var host = Host(inner.Object, clock); using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IModelCostService>();
        Assert.Null(await cache.GetCostForModelAsync("model")); Assert.Null(await cache.GetCostForModelAsync("model"));
        inner.Verify(service => service.GetCostForModelAsync("model", It.IsAny<CancellationToken>()), Times.Once);
        clock.Now = clock.Now.AddSeconds(61);
        Assert.Null(await cache.GetCostForModelAsync("model"));
        inner.Verify(service => service.GetCostForModelAsync("model", It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Null(await cache.GetCostByIdAsync(42)); Assert.Empty(await cache.ListModelCostsAsync());
        var billing = new CostCalculationService(cache, NullLogger<CostCalculationService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => billing.CalculateCostAsync("model", new Usage { PromptTokens = 1_000_000 }));
        var cost = Cost();
        inner.Setup(service => service.GetCostForModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        inner.Setup(service => service.GetCostByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        inner.Setup(service => service.ListModelCostsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([cost]);
        await cache.AddModelCostAsync(cost);
        Assert.Equal(0.25m, await billing.CalculateCostAsync("model", new Usage { PromptTokens = 1_000_000 }));
        Assert.NotNull(await cache.GetCostByIdAsync(42)); Assert.Single(await cache.ListModelCostsAsync());
        cost.InputCostPerMillionTokens = 0; await cache.UpdateModelCostAsync(cost);
        Assert.Equal(0, await billing.CalculateCostAsync("model", new Usage { PromptTokens = 1_000_000 })); // real free price is valid
        inner.Setup(service => service.GetCostForModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((ModelCost?)null);
        await cache.DeleteModelCostAsync(42); Assert.Null(await cache.GetCostForModelAsync("model"));
    }

    [Fact]
    public async Task EffectiveAndExpiryTimesOverrideColdAndWarmTtl()
    {
        var clock = new Clock(); var cost = Cost(); cost.EffectiveDate = clock.Now.AddSeconds(10).UtcDateTime;
        cost.ExpiryDate = clock.Now.AddSeconds(20).UtcDateTime;
        using var host = Host(Inner(cost).Object, clock); using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IModelCostService>();
        Assert.Null(await cache.GetCostForModelAsync("model")); Assert.Null(await cache.GetCostByIdAsync(42));
        clock.Now = clock.Now.AddSeconds(10);
        Assert.NotNull(await cache.GetCostForModelAsync("model")); Assert.NotNull(await cache.GetCostByIdAsync(42));
        clock.Now = clock.Now.AddSeconds(10);
        Assert.Null(await cache.GetCostForModelAsync("model")); Assert.Null(await cache.GetCostByIdAsync(42));
        Assert.Single(await cache.ListModelCostsAsync()); // administrative list retains expired rows
        cost.ExpiryDate = null; cost.IsActive = false; await cache.ClearCacheAsync();
        Assert.Null(await cache.GetCostForModelAsync("model")); Assert.Null(await cache.GetCostByIdAsync(42));
    }

    [Fact]
    public async Task LateCostLoaderCannotRepopulateCurrentGenerationOrReturnExpiredPrice()
    {
        var clock = new Clock(); var old = Cost(); old.ExpiryDate = clock.Now.AddSeconds(10).UtcDateTime;
        var fresh = Cost(); fresh.InputCostPerMillionTokens = 0.75m; var inner = Inner(old);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Setup(service => service.GetCostByIdAsync(42, It.IsAny<CancellationToken>())).Returns(async () =>
        { entered.SetResult(); await release.Task; return old; });
        using var host = Host(inner.Object, clock); using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IModelCostService>();
        var request = cache.GetCostByIdAsync(42); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cache.ClearCacheAsync(); clock.Now = clock.Now.AddSeconds(11);
        inner.Setup(service => service.GetCostByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(fresh);
        release.SetResult(); Assert.Null(await request);
        Assert.Equal(0.75m, (await cache.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
    }

    [Fact]
    public async Task RuleContentVariantsAndMutableConditionsStayIsolatedAndInvalidJsonIsNeverCached()
    {
        using var host = Host(Inner(null).Object);
        var service = host.GetRequiredService<ICachedPricingRulesService>();
        var values = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => service.GetConfigAsync(42, Rules)));
        values[0]!.Rules[0].Conditions.Clear(); values[0]!.Constraints!.AllowedResolutions!.Clear();
        Assert.All(values.Skip(1), value => Assert.Single(value!.Rules[0].Conditions));
        Assert.Single((await service.GetConfigAsync(42, Rules))!.Constraints!.AllowedResolutions!);
        Assert.Equal(0.75m, (await service.GetConfigAsync(42, Rules.Replace("0.25", "0.75")))!.DefaultRate);
        Assert.Null(await service.GetConfigAsync(42, "{")); Assert.Null(await service.GetConfigAsync(42, "{\"rules\":null}"));
        await service.InvalidateCacheAsync(42);
        Assert.Equal(0.25m, (await service.GetConfigAsync(42, Rules))!.DefaultRate);
    }

    [Fact]
    public async Task ObsoleteCostPayloadFallsBackToCurrentBillingData()
    {
        var cost = Cost(); var inner = Inner(cost);
        using var host = Host(inner.Object); using var scope = host.CreateScope();
        var generation = await host.GetRequiredService<ApplicationCacheGeneration>().GetAsync(ApplicationCacheDomain.Costs);
        var snapshot = CostCacheSnapshot.From(cost) with { Version = 0 };
        await host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey).SetAsync(
            $"costs:{generation}:modelcost:id:42", new CostLookupResult(snapshot, DateTime.UtcNow.AddHours(1)),
            host.GetRequiredService<ApplicationCacheOptions>().Entry(TimeSpan.FromHours(1)));
        var value = await scope.ServiceProvider.GetRequiredService<IModelCostService>().GetCostByIdAsync(42);
        Assert.Equal(0.25m, value!.InputCostPerMillionTokens);
        inner.Verify(service => service.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BusinessFailuresAreNotRetriedAndCancellationAndDisabledRegionsAreHonored()
    {
        var inner = Inner(Cost());
        inner.Setup(service => service.GetCostByIdAsync(42, It.IsAny<CancellationToken>())).ThrowsAsync(new RedisException("repository failure"));
        using var host = Host(inner.Object); using var scope = host.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IModelCostService>();
        await Assert.ThrowsAsync<RedisException>(() => service.GetCostByIdAsync(42));
        inner.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCostForModelAsync("model", new CancellationToken(true)));
        var current = Inner(Cost()); using var disabled = Host(current.Object, enabled: false); using var disabledScope = disabled.CreateScope();
        var bypass = disabledScope.ServiceProvider.GetRequiredService<IModelCostService>();
        await bypass.GetCostByIdAsync(42); await bypass.GetCostByIdAsync(42);
        current.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [SkippableFact]
    public async Task BillingOutageFallsBackAndRecoveryFencesOldL2BeforeRetry()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrEmpty(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        await using var proxy = new RedisNetworkProxy(redis!);
        var cost = Cost(); var inner = Inner(cost);
        using var host = Host(inner.Object, redis: proxy.ConnectionString); using var scope = host.CreateScope();
        await RedisCacheTestReadiness.WarmAsync(host, ApplicationCacheDomain.Costs, ApplicationCacheDomain.PricingRules);
        var cache = scope.ServiceProvider.GetRequiredService<IModelCostService>();
        var rules = host.GetRequiredService<ICachedPricingRulesService>();
        var generation = host.GetRequiredService<ApplicationCacheGeneration>();
        var original = await generation.GetAsync(ApplicationCacheDomain.Costs);
        await cache.GetCostByIdAsync(42); await rules.GetConfigAsync(42, Rules);
        proxy.Disconnect(); cost.InputCostPerMillionTokens = 0.75m;
        await Assert.ThrowsAsync<ApplicationCacheInvalidationException>(() => cache.ClearCacheAsync());
        await Assert.ThrowsAsync<ApplicationCacheInvalidationException>(() => rules.InvalidateCacheAsync(42));
        Assert.Equal(0.75m, (await cache.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
        Assert.Equal(0.75m, (await rules.GetConfigAsync(42, Rules.Replace("0.25", "0.75")))!.DefaultRate);
        proxy.Reconnect();
        var deadline = System.Diagnostics.Stopwatch.StartNew(); string? current = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try { current = await generation.GetAsync(ApplicationCacheDomain.Costs); break; }
            catch (Exception ex) when (ex is RedisException or TimeoutException) { await Task.Delay(10); }
        }
        Assert.NotNull(current); Assert.NotEqual(original, current);
        await RedisCacheTestReadiness.WarmAsync(host, ApplicationCacheDomain.Costs, ApplicationCacheDomain.PricingRules);
        Assert.Equal(0.75m, (await cache.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
        // Prove fencing above before explicitly retrying invalidation. The separate Redis
        // connections can reconnect at different times; successful post-outage writes are eventual.
        using var recovered = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (;;)
        {
            try { await cache.ClearCacheAsync(recovered.Token); await rules.InvalidateAllAsync(recovered.Token); break; }
            catch (ApplicationCacheInvalidationException exception) when (exception.InnerException is RedisException or TimeoutException
                or FusionCacheDistributedCacheException or FusionCacheBackplaneException)
            { await Task.Delay(25, recovered.Token); }
        }
        Assert.Equal(0.75m, (await cache.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
    }

    [SkippableFact]
    public async Task IndependentRedisL2RoundTripsCostGraphRulesAndOneMinuteMissingContract()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrEmpty(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        var environment = $"test-{Guid.NewGuid():N}"; var cost = Cost(); var inner = Inner(cost);
        // This contract proves healthy L2 values and TTLs, not the default 250 ms outage budget.
        // Use the supported one-second read budget so instrumented scheduling is not an assertion.
        using var writer = Host(inner.Object, redis: redis, environment: environment, healthyReadTimeout: TimeSpan.FromSeconds(1)); using var writerScope = writer.CreateScope();
        using var reader = Host(inner.Object, redis: redis, environment: environment, healthyReadTimeout: TimeSpan.FromSeconds(1)); using var readerScope = reader.CreateScope();
        await RedisCacheTestReadiness.WarmAsync(writer, ApplicationCacheDomain.Costs, ApplicationCacheDomain.PricingRules);
        await RedisCacheTestReadiness.WarmAsync(reader, ApplicationCacheDomain.Costs, ApplicationCacheDomain.PricingRules);
        var first = writerScope.ServiceProvider.GetRequiredService<IModelCostService>();
        await first.GetCostByIdAsync(42); await first.ListModelCostsAsync();
        await writer.GetRequiredService<ICachedPricingRulesService>().GetConfigAsync(42, Rules);
        var second = readerScope.ServiceProvider.GetRequiredService<IModelCostService>();
        Assert.Equal("model", (await second.GetCostByIdAsync(42))!.ModelProviderTypeAssociations!.Single().Model!.Name);
        Assert.Single(await second.ListModelCostsAsync());
        inner.Verify(service => service.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        inner.Verify(service => service.ListModelCostsAsync(It.IsAny<CancellationToken>()), Times.Once);
        var rule = (await reader.GetRequiredService<ICachedPricingRulesService>().GetConfigAsync(42, Rules))!.Rules.Single();
        Assert.Equal("1024x1024", ((JsonElement)rule.Conditions["resolution"]).GetString());
        inner.Setup(service => service.GetCostForModelAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((ModelCost?)null);
        Assert.Null(await first.GetCostForModelAsync("missing")); Assert.Null(await second.GetCostForModelAsync("missing"));
        inner.Verify(service => service.GetCostForModelAsync("missing", It.IsAny<CancellationToken>()), Times.Once);
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis!);
        var server = connection.GetServer(connection.GetEndPoints()[0]);
        var key = server.Keys(pattern: $"*conduit:app-cache:{environment}:v1:*missing*").Single();
        Assert.InRange((await connection.GetDatabase().KeyTimeToLiveAsync(key))!.Value.TotalSeconds, 50, 60);
        cost.InputCostPerMillionTokens = 0.75m; await first.UpdateModelCostAsync(cost);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while ((await second.GetCostByIdAsync(42))!.InputCostPerMillionTokens != 0.75m && deadline.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(5);
        Assert.Equal(0.75m, (await second.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
    }
}
