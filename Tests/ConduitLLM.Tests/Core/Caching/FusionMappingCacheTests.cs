using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Events;
using ConduitLLM.Gateway.Consumers;
using ConduitLLM.Gateway.EventHandlers;
using ConduitLLM.Gateway.Interfaces;
using ConduitLLM.Tests.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ConduitLLM.Configuration.Constants;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Tests.Core.Caching;

public sealed class FusionMappingCacheTests
{
    private static ServiceProvider Host(IModelProviderMappingService inner, string? redis = null, string? environment = null,
        bool enabled = true)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ApplicationCache:Environment"] = environment ?? $"test-{Guid.NewGuid():N}",
            ["ApplicationCache:Domains:Mappings:Enabled"] = enabled.ToString() }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddConduitApplicationCache(configuration, "test", redis ?? "");
        services.AddSingleton<IModelMappingCacheInvalidator, ModelMappingCacheInvalidator>();
        services.AddScoped<IModelProviderMappingService>(provider => ActivatorUtilities.CreateInstance<FusionModelProviderMappingService>(provider, inner));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
    private static ModelProviderMapping Graph() => new()
    {
        Id = 1, ModelAlias = "old", ProviderId = 2, ProviderModelId = "provider-model", ModelProviderTypeAssociationId = 3,
        RoutingPriority = 7, RoutingWeight = 1.2m, ProviderOptions = """{"route":"fallback"}""",
        Provider = new() { Id = 2, ProviderType = ProviderType.OpenAI, BaseUrl = "https://fixture.invalid", Settings = new() { ["account"] = "fixture" },
            TrustProviderReportedCosts = true, ProviderCostMarkupMultiplier = 1.1m },
        ModelProviderTypeAssociation = new() { Id = 3, ModelId = 4, ModelCostId = 5, MaxInputTokens = 2048,
            OperationalCapabilitiesJson = """{"supports_streaming":true}""", QualityScore = 0.9m, ProviderVariation = "int8",
            Model = new() { Id = 4, SupportsChat = true, SupportsVision = true, SupportsImageGeneration = true, ModelParameters = null,
                Series = new() { Id = 6, Name = "Series", Parameters = """{"temperature":{"default":0.5}}""" } },
            ModelCost = new() { Id = 5, IsActive = true, EffectiveDate = DateTime.UtcNow.AddDays(-1), InputCostPerMillionTokens = 0.25m,
                CachedInputWriteCostPerMillionTokens = 0.05m, PricingConfiguration = """{"rules":[]}""" } }
    };
    private static Mock<IModelProviderMappingService> Inner(ModelProviderMapping value)
    {
        var mock = new Mock<IModelProviderMappingService>();
        mock.Setup(service => service.GetMappingByModelAliasAsync(It.IsAny<string>())).Returns((string alias) => Task.FromResult(alias == value.ModelAlias ? value : null));
        mock.Setup(service => service.GetMappingsByModelAliasAsync(It.IsAny<string>())).Returns((string alias) => Task.FromResult(alias == value.ModelAlias ? new List<ModelProviderMapping> { value } : []));
        mock.Setup(service => service.GetMappingByIdAsync(1)).ReturnsAsync(value);
        mock.Setup(service => service.GetAllMappingsAsync()).ReturnsAsync([value]);
        return mock;
    }
    private static void AssertGraph(ModelProviderMapping value)
    {
        Assert.True(value.ModelProviderTypeAssociation.Model.SupportsImageGeneration);
        Assert.True(value.Provider.TrustProviderReportedCosts);
        Assert.Equal(1.1m, value.Provider.ProviderCostMarkupMultiplier);
        Assert.Equal("fixture", value.Provider.Settings!["account"]);
        Assert.Equal(2048, value.ModelProviderTypeAssociation.MaxInputTokens);
        Assert.Equal(0.25m, value.ModelProviderTypeAssociation.ModelCost!.InputCostPerMillionTokens);
        Assert.Contains("temperature", value.ModelProviderTypeAssociation.Model.Parameters);
        Assert.Equal(7, value.RoutingPriority);
    }

    [Fact]
    public async Task ConcurrentMissesCoalesceAndEveryGraphIsOwnedByItsCaller()
    {
        var source = Graph();
        var inner = Inner(source);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Setup(service => service.GetMappingByModelAliasAsync("old")).Returns(async () =>
        { entered.TrySetResult(); await release.Task; return source; });
        using var host = Host(inner.Object);
        var stored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey).Events.Memory.Set += (_, entry) =>
        {
            if (entry.Key.EndsWith(CacheKeys.ModelMapping.ByAlias("old"), StringComparison.Ordinal)) stored.TrySetResult();
        };
        using var scope = host.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IModelProviderMappingService>();
        var requests = Enumerable.Range(0, 32).Select(_ => service.GetMappingByModelAliasAsync("old")).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // All 32 calls are pending on the same loader. Check coalescing while it is
        // blocked: draining the continuations can outlive the production 100 ms TTL.
        inner.Verify(item => item.GetMappingByModelAliasAsync("old"), Times.Once);
        release.SetResult();
        var results = await Task.WhenAll(requests);
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        results[0]!.Provider.Settings!.Clear();
        results[0]!.ModelProviderTypeAssociation.Model.SupportsImageGeneration = false;
        source.Provider.Settings!.Clear();
        Assert.All(results.Skip(1), value => AssertGraph(value!));
    }

    [Fact]
    public async Task RetainedCacheHitOwnsItsGraphAndNeverReloads()
    {
        var source = Graph();
        var inner = Inner(source);
        using var host = Host(inner.Object);
        using var scope = host.CreateScope();
        var generation = await host.GetRequiredService<ApplicationCacheGeneration>().GetAsync(ApplicationCacheDomain.Mappings);
        // This tests retained reads independently of the production 100 ms write TTL.
        await host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey).SetAsync<List<MappingCacheSnapshot>>(
            $"mappings:{generation}:{CacheKeys.ModelMapping.ByAlias("old")}", [MappingCacheSnapshot.From(source)],
            new FusionCacheEntryOptions { Duration = TimeSpan.FromMinutes(1) });
        var service = scope.ServiceProvider.GetRequiredService<IModelProviderMappingService>();
        var first = await service.GetMappingByModelAliasAsync("old");
        first!.Provider.Settings!.Clear();
        source.Provider.Settings!.Clear();
        var cached = await service.GetMappingByModelAliasAsync("old");
        AssertGraph(cached!);
        inner.Verify(item => item.GetMappingByModelAliasAsync("old"), Times.Never);
    }

    [SkippableFact]
    public async Task CompleteIndependentL2NeedsNoRepairAndRenamesExpireEveryReadVariant()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        var value = Graph(); var inner = Inner(value); var environment = $"test-{Guid.NewGuid():N}";
        using var first = Host(inner.Object, redis, environment); using var firstScope = first.CreateScope();
        var writer = firstScope.ServiceProvider.GetRequiredService<IModelProviderMappingService>();
        await writer.GetMappingByIdAsync(1); await writer.GetMappingByModelAliasAsync("old");
        await writer.GetMappingsByModelAliasAsync("old"); await writer.GetAllMappingsAsync();
        using var second = Host(inner.Object, redis, environment); using var secondScope = second.CreateScope();
        var reader = secondScope.ServiceProvider.GetRequiredService<IModelProviderMappingService>();
        AssertGraph((await reader.GetMappingByIdAsync(1))!); AssertGraph((await reader.GetMappingByModelAliasAsync("old"))!);
        AssertGraph((await reader.GetMappingsByModelAliasAsync("old")).Single()); AssertGraph((await reader.GetAllMappingsAsync()).Single());
        inner.Verify(item => item.GetMappingByIdAsync(1), Times.Once);
        inner.Verify(item => item.GetMappingByModelAliasAsync("old"), Times.Once);
        inner.Verify(item => item.GetAllMappingsAsync(), Times.Once);
        value.ModelAlias = "new"; value.IsEnabled = false;
        await writer.UpdateMappingAsync(value);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while ((await reader.GetMappingByIdAsync(1))!.ModelAlias == "old" && deadline.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(5);
        Assert.Null(await reader.GetMappingByModelAliasAsync("old"));
        Assert.Empty(await reader.GetMappingsByModelAliasAsync("old"));
        Assert.Equal("new", (await reader.GetMappingByModelAliasAsync("new"))!.ModelAlias);
        Assert.False((await reader.GetAllMappingsAsync()).Single().IsEnabled);
        inner.Setup(service => service.GetMappingByIdAsync(1)).ReturnsAsync((ModelProviderMapping?)null);
        inner.Setup(service => service.GetAllMappingsAsync()).ReturnsAsync([]);
        inner.Setup(service => service.GetMappingByModelAliasAsync("new")).ReturnsAsync((ModelProviderMapping?)null);
        await writer.DeleteMappingAsync(1);
        using var restart = Host(inner.Object, redis, environment); using var restartScope = restart.CreateScope();
        var restarted = restartScope.ServiceProvider.GetRequiredService<IModelProviderMappingService>();
        Assert.Null(await restarted.GetMappingByIdAsync(1)); Assert.Null(await restarted.GetMappingByModelAliasAsync("new"));
        Assert.Empty(await restarted.GetAllMappingsAsync());
    }

    [SkippableFact]
    public async Task ProviderModelAndAssociationEventsExpireRoutingGraphs()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        var value = Graph(); var inner = Inner(value); var environment = $"test-{Guid.NewGuid():N}";
        using var first = Host(inner.Object, redis, environment); using var scope = first.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IModelProviderMappingService>();
        var invalidation = first.GetRequiredService<IModelMappingCacheInvalidator>();
        var discovery = Mock.Of<IDiscoveryCacheService>();
        await cache.GetMappingByIdAsync(1);
        value.Provider.BaseUrl = "https://changed.invalid";
        await new ProviderCacheInvalidationHandler(Mock.Of<ISettingsRefreshService>(), discovery,
            NullLogger<ProviderCacheInvalidationHandler>.Instance, invalidation).HandleAsync(new ProviderUpdated { ProviderId = 2 }, new TestEventContext());
        Assert.Equal(value.Provider.BaseUrl, (await cache.GetMappingByIdAsync(1))!.Provider.BaseUrl);
        value.ModelProviderTypeAssociation.Model.SupportsImageGeneration = false;
        await new ModelCacheInvalidationHandler(discovery, Mock.Of<IModelCapabilityService>(),
            NullLogger<ModelCacheInvalidationHandler>.Instance, invalidation).HandleAsync(new ModelUpdated { ModelId = 4 }, new TestEventContext());
        Assert.False((await cache.GetMappingByIdAsync(1))!.ModelProviderTypeAssociation.Model.SupportsImageGeneration);
        value.ModelProviderTypeAssociation.MaxInputTokens = 8192;
        await new DiscoveryCacheInvalidationHandler(discovery, NullLogger<DiscoveryCacheInvalidationHandler>.Instance, invalidation)
            .HandleAsync(new DiscoveryCacheInvalidationRequested(), new TestEventContext());
        using var second = Host(inner.Object, redis, environment); using var secondScope = second.CreateScope();
        Assert.Equal(8192, (await secondScope.ServiceProvider.GetRequiredService<IModelProviderMappingService>().GetMappingByIdAsync(1))!.ModelProviderTypeAssociation.MaxInputTokens);
        value.ModelProviderTypeAssociation.ModelCost!.InputCostPerMillionTokens = 0.75m;
        await new ModelCostCacheInvalidationHandler(Mock.Of<ConduitLLM.Configuration.Interfaces.IModelCostService>(), null, discovery,
            NullLogger<ModelCostCacheInvalidationHandler>.Instance, invalidation).HandleAsync(new ModelCostChanged { ModelCostId = 5 }, new TestEventContext());
        Assert.Equal(0.75m, (await cache.GetMappingByIdAsync(1))!.ModelProviderTypeAssociation.ModelCost!.InputCostPerMillionTokens);
    }

    [SkippableFact]
    public async Task OutageFallbackAndReconnectCannotResurrectOldRoutingBeforeRetry()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        await using var proxy = new RedisNetworkProxy(redis!);
        var value = Graph(); var inner = Inner(value);
        using var host = Host(inner.Object, proxy.ConnectionString); using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IModelProviderMappingService>();
        var generations = host.GetRequiredService<ApplicationCacheGeneration>();
        var originalGeneration = await generations.GetAsync(ApplicationCacheDomain.Mappings);
        await cache.GetMappingByIdAsync(1);
        proxy.Disconnect(); value.Provider.BaseUrl = "https://current.invalid";
        var handler = new ModelMappingCacheInvalidationHandler(host.GetRequiredService<IModelMappingCacheInvalidator>(),
            Mock.Of<IDiscoveryCacheService>(), NullLogger<ModelMappingCacheInvalidationHandler>.Instance);
        await Assert.ThrowsAsync<ApplicationCacheInvalidationException>(() => handler.HandleAsync(new ModelMappingChanged(), new TestEventContext()));
        Assert.Equal(value.Provider.BaseUrl, (await cache.GetMappingByIdAsync(1))!.Provider.BaseUrl);
        proxy.Reconnect();
        // Deliberately do not retry the invalidation yet. Recovery must fence the old L2 namespace itself.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        string? repairedGeneration = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try { repairedGeneration = await generations.GetAsync(ApplicationCacheDomain.Mappings); break; }
            catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException) { await Task.Delay(10); }
        }
        Assert.NotNull(repairedGeneration);
        Assert.NotEqual(originalGeneration, repairedGeneration);
        Assert.Equal(value.Provider.BaseUrl, (await cache.GetMappingByIdAsync(1))!.Provider.BaseUrl);
        await handler.HandleAsync(new ModelMappingChanged(), new TestEventContext());
        Assert.Equal(value.Provider.BaseUrl, (await cache.GetMappingByIdAsync(1))!.Provider.BaseUrl);
    }

    [Fact]
    public async Task LateMappingLoadCannotRemainInTheCurrentGeneration()
    {
        var old = Graph(); var fresh = Graph(); fresh.Provider.BaseUrl = "https://fresh.invalid";
        var inner = Inner(old);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Setup(service => service.GetMappingByIdAsync(1)).Returns(async () =>
        { entered.SetResult(); await release.Task; return old; });
        using var host = Host(inner.Object); using var scope = host.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IModelProviderMappingService>();
        var request = service.GetMappingByIdAsync(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.GetRequiredService<IModelMappingCacheInvalidator>().InvalidateAsync();
        inner.Setup(item => item.GetMappingByIdAsync(1)).ReturnsAsync(fresh);
        release.SetResult(); await request;
        Assert.Equal(fresh.Provider.BaseUrl, (await service.GetMappingByIdAsync(1))!.Provider.BaseUrl);
    }

    [Fact]
    public async Task BusinessFailureIsNotRetriedAndDisabledRegionNeverReads()
    {
        var inner = Inner(Graph());
        inner.Setup(service => service.GetMappingByIdAsync(1)).ThrowsAsync(new InvalidOperationException("repository failure"));
        using var host = Host(inner.Object); using var scope = host.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IModelProviderMappingService>().GetMappingByIdAsync(1));
        inner.Verify(service => service.GetMappingByIdAsync(1), Times.Once);
        using var disabled = Host(Inner(Graph()).Object, enabled: false); using var disabledScope = disabled.CreateScope();
        AssertGraph((await disabledScope.ServiceProvider.GetRequiredService<IModelProviderMappingService>().GetMappingByIdAsync(1))!);
    }
}
