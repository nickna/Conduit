using System.Text.Json;
using System.Text.Json.Nodes;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Consumers;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Tests.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Tests.Core.Caching;

[Collection(ApplicationCacheContractCollection.Name)]
public sealed class FusionFunctionDiscoveryCacheTests
{
    private static ServiceProvider Host(Mock<IGlobalSettingRepository> settings,
        Mock<IFunctionConfigurationRepository>? configurations = null, string? redis = null, string? environment = null,
        TimeSpan? outageReadTimeout = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationCache:Environment"] = environment ?? $"test-{Guid.NewGuid():N}",
            ["ApplicationCache:DistributedReadTimeout"] = outageReadTimeout?.ToString()
                ?? (redis is null ? "00:00:00.250" : RedisCacheTestReadiness.HealthyReadTimeout),
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddScoped(_ => settings.Object);
        services.AddScoped(_ => configurations?.Object ?? Mock.Of<IFunctionConfigurationRepository>());
        services.AddConduitApplicationCache(configuration, "test", redis ?? "");
        services.AddFunctionDiscoveryCache(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static Mock<IGlobalSettingRepository> Settings(Func<string?>? value = null)
    {
        var result = new Mock<IGlobalSettingRepository>();
        result.Setup(repo => repo.GetByKeyAsync("Functions.DiscoveryCacheEnabled", It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new GlobalSetting { Key = "Functions.DiscoveryCacheEnabled", Value = value?.Invoke() ?? "true" }));
        return result;
    }
    private static List<Tool> Tools(string description = "old") => [new() { Function = new()
    {
        Name = "search", Description = description,
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""")!.AsObject()
    } }];
    private static Task<FunctionDiscoveryLoad> Load(CancellationToken _) => Task.FromResult(new FunctionDiscoveryLoad(Tools(), 2));

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteIndependentRedisPayloadFallsBackToOneCurrentBusinessLoad(bool nullFunction)
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrEmpty(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        var environment = $"test-{Guid.NewGuid():N}";
        using var writer = Host(Settings(), redis: redis, environment: environment);
        await RedisCacheTestReadiness.WarmAsync(writer, ApplicationCacheDomain.Functions);
        var generation = await writer.GetRequiredService<ApplicationCacheGeneration>().GetAsync(ApplicationCacheDomain.Functions);
        List<Tool> incomplete = nullFunction ? [new() { Function = null! }] : [null!];
        await writer.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey)
            .SetAsync($"functions:{generation}:configs:1", incomplete, tags: ["functions"]);
        using var reader = Host(Settings(), redis: redis, environment: environment);
        await RedisCacheTestReadiness.WarmAsync(reader, ApplicationCacheDomain.Functions);
        using var scope = reader.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>();
        Assert.Null(await service.GetCachedToolsAsync([1]));
        var loads = 0;
        var result = await service.GetOrLoadAsync([1], _ =>
        { loads++; return Task.FromResult(new FunctionDiscoveryLoad(Tools("current"), 2)); });
        Assert.Equal(1, loads);
        Assert.Equal("current", Assert.Single(result).Function.Description);
    }

    [Fact]
    public async Task ConcurrentNormalizedSetsLoadOnceAndDetachSchemas()
    {
        using var host = Host(Settings());
        using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>();
        Assert.IsType<FusionFunctionDiscoveryCacheService>(cache);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = Tools();
        var loads = 0;
        async Task<FunctionDiscoveryLoad> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref loads);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(tools, 2);
        }
        var requests = Enumerable.Range(0, 32).Select(i => cache.GetOrLoadAsync(i % 2 == 0 ? [2, 1, 2] : [1, 2], Factory)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        var results = await Task.WhenAll(requests);
        Assert.Equal(1, loads);
        tools[0].Function.Parameters!.Clear();
        results[0][0].Function.Parameters!.Clear();
        Assert.All(results.Skip(1), result => Assert.Equal("object", result[0].Function.Parameters!["type"]!.GetValue<string>()));
        Assert.Equal("object", (await cache.GetCachedToolsAsync([1, 2]))![0].Function.Parameters!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("true", true)] [InlineData("1", true)] [InlineData("YES", true)] [InlineData("enabled", true)]
    [InlineData("", false)] [InlineData("false", false)] [InlineData("no", false)]
    public async Task TogglePreservesInterpretation(string setting, bool expected)
    {
        using var host = Host(Settings(() => setting));
        using var scope = host.CreateScope();
        Assert.Equal(expected, await scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>().IsCachingEnabledAsync());
    }

    [Fact]
    public async Task DisabledReadsBypassAndReenableEventRejectsOldCombinations()
    {
        var value = "true";
        using var host = Host(Settings(() => value));
        using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>();
        await cache.GetOrLoadAsync([1, 2], Load);
        value = "false";
        Assert.Null(await cache.GetCachedToolsAsync([1, 2]));
        var settingsCache = new Mock<IGlobalSettingsCacheService>();
        var handler = new GlobalSettingCacheInvalidationHandler(settingsCache.Object,
            NullLogger<GlobalSettingCacheInvalidationHandler>.Instance, cache);
        await handler.HandleAsync(new GlobalSettingChanged { SettingKey = "Functions.DiscoveryCacheEnabled" }, new TestEventContext());
        value = "true";
        Assert.Null(await cache.GetCachedToolsAsync([1, 2]));
    }

    [Fact]
    public async Task NoTtlDoesNotWriteAndBusinessFailuresNeverRetry()
    {
        using var host = Host(Settings());
        using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>();
        await cache.GetOrLoadAsync([1], _ => Task.FromResult(new FunctionDiscoveryLoad(Tools(), null)));
        Assert.Null(await cache.GetCachedToolsAsync([1]));
        var loads = 0;
        await Assert.ThrowsAsync<RedisException>(() => cache.GetOrLoadAsync([1], _ =>
        { loads++; throw new RedisException("business failure"); }));
        Assert.Equal(1, loads);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrLoadAsync([1], Load, cancellationToken: cancellation.Token));
    }

    [SkippableFact]
    public async Task RedisOverlappingSetsConvergeAndPersistSchemasAndConfiguredTtl()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        var environment = $"test-{Guid.NewGuid():N}";
        var configs = new Mock<IFunctionConfigurationRepository>();
        configs.Setup(repo => repo.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .Returns((List<int> ids, CancellationToken _) => Task.FromResult(ids.Select(id => new FunctionConfiguration
            { Id = id, ConfigurationName = $"Config {id}", ProviderType = FunctionProviderType.Exa, IsEnabled = true,
                CacheTtlMinutes = id == 1 ? 3 : 2, ParameterSchema = """{"type":"object","required":["query"]}""" }).ToList()));
        using var first = Host(Settings(), configs, redis, environment);
        await RedisCacheTestReadiness.WarmAsync(first, ApplicationCacheDomain.Functions);
        using var firstScope = first.CreateScope();
        var cache = firstScope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>();
        var service = new FunctionDiscoveryService(configs.Object, Mock.Of<IFunctionClientFactory>(), cache,
            NullLogger<FunctionDiscoveryService>.Instance);
        var cold = await service.GetToolsForFunctionConfigurationsAsync([2, 1, 2], 7);
        var warm = await service.GetToolsForFunctionConfigurationsAsync([1, 2], 8);
        Assert.Equal(2, cold.Count);
        Assert.Equal(cold[0].Function.Parameters!.ToJsonString(), warm[0].Function.Parameters!.ToJsonString());
        configs.Verify(repo => repo.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Once);
        await cache.GetOrLoadAsync([2, 3], Load);
        using var second = Host(Settings(), configs, redis, environment);
        await RedisCacheTestReadiness.WarmAsync(second, ApplicationCacheDomain.Functions);
        using var secondScope = second.CreateScope();
        var reader = secondScope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>();
        Assert.Equal(cold[0].Function.Parameters!.ToJsonString(), (await reader.GetCachedToolsAsync([1, 2]))![0].Function.Parameters!.ToJsonString());
        Assert.NotNull(await reader.GetCachedToolsAsync([2, 3]));
        using var observer = await ConnectionMultiplexer.ConnectAsync(redis);
        var keys = observer.GetServer(observer.GetEndPoints()[0]).Keys(pattern: $"*{environment}*configs:1,2*").ToArray();
        Assert.Single(keys);
        Assert.InRange((await observer.GetDatabase().KeyTimeToLiveAsync(keys[0]))!.Value.TotalSeconds, 110, 120);
        await cache.GetOrLoadAsync([9], Load, ttlMinutes: 1);
        var overrideKey = observer.GetServer(observer.GetEndPoints()[0]).Keys(pattern: $"*{environment}*configs:9").Single();
        Assert.InRange((await observer.GetDatabase().KeyTimeToLiveAsync(overrideKey))!.Value.TotalSeconds, 50, 60);
        var handler = new FunctionConfigurationCacheInvalidationHandler(cache, NullLogger<FunctionConfigurationCacheInvalidationHandler>.Instance);
        await handler.HandleAsync(new FunctionConfigurationChanged { FunctionConfigurationId = 2 }, new TestEventContext());
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (await reader.GetCachedToolsAsync([1, 2]) is not null && deadline.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        Assert.Null(await reader.GetCachedToolsAsync([1, 2]));
        Assert.Null(await reader.GetCachedToolsAsync([2, 3]));
        using var restart = Host(Settings(), configs, redis, environment);
        await RedisCacheTestReadiness.WarmAsync(restart, ApplicationCacheDomain.Functions);
        using var restartScope = restart.CreateScope();
        Assert.Null(await restartScope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>().GetCachedToolsAsync([1, 2]));
    }

    [SkippableFact]
    public async Task McpExpansionSurvivesL1AndIndependentL2AndKeepsItsSchemas()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        var environment = $"test-{Guid.NewGuid():N}";
        var configs = new Mock<IFunctionConfigurationRepository>();
        configs.Setup(repo => repo.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionConfiguration { Id = 42, ConfigurationName = "Acme MCP", ProviderType = FunctionProviderType.Mcp,
                IsEnabled = true, CacheTtlMinutes = 1 }]);
        var client = new Mock<IFunctionClient>();
        client.As<IDynamicToolProvider>().Setup(provider => provider.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DiscoveredTool { Name = "search", ParametersSchema = Tools()[0].Function.Parameters },
                new DiscoveredTool { Name = "fetch", ParametersSchema = JsonNode.Parse("""{"type":"object","properties":{"url":{"type":"string"}}}""")!.AsObject() }]);
        var factory = new Mock<IFunctionClientFactory>();
        factory.Setup(item => item.GetClientAsync(FunctionProviderType.Mcp, 42)).ReturnsAsync(client.Object);
        using var writer = Host(Settings(), configs, redis, environment);
        await RedisCacheTestReadiness.WarmAsync(writer, ApplicationCacheDomain.Functions);
        using var scope = writer.CreateScope();
        var service = new FunctionDiscoveryService(configs.Object, factory.Object,
            scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>(), NullLogger<FunctionDiscoveryService>.Instance);
        var cold = await service.GetToolsForFunctionConfigurationsAsync([42], 1);
        var warm = await service.GetToolsForFunctionConfigurationsAsync([42], 2);
        Assert.Equal(new[] { "acme_mcp__search", "acme_mcp__fetch" }, cold.Select(tool => tool.Function.Name));
        using var reader = Host(Settings(), configs, redis, environment);
        await RedisCacheTestReadiness.WarmAsync(reader, ApplicationCacheDomain.Functions);
        using var readerScope = reader.CreateScope();
        var l2 = await readerScope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>().GetCachedToolsAsync([42]);
        Assert.Equal(cold.Select(tool => tool.Function.Parameters!.ToJsonString()), l2!.Select(tool => tool.Function.Parameters!.ToJsonString()));
        Assert.Equal(cold.Select(tool => tool.Function.Parameters!.ToJsonString()), warm.Select(tool => tool.Function.Parameters!.ToJsonString()));
        factory.Verify(item => item.GetClientAsync(FunctionProviderType.Mcp, 42), Times.Once);
    }

    [Fact]
    public async Task LateSchemaLoadCannotRestoreAnInvalidatedCombination()
    {
        using var host = Host(Settings());
        using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = cache.GetOrLoadAsync([1, 2], async token =>
        { entered.SetResult(); await release.Task.WaitAsync(token); return new(Tools("old"), 2); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cache.InvalidateFunctionConfigurationAsync(2);
        release.SetResult();
        await old;
        var fresh = await cache.GetOrLoadAsync([2, 1], _ => Task.FromResult(new FunctionDiscoveryLoad(Tools("new"), 2)));
        Assert.Equal("new", fresh[0].Function.Description);
    }

    [SkippableFact]
    public async Task ServiceInvalidationFailurePropagatesThroughRealHandler()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        await using var proxy = new RedisNetworkProxy(redis!);
        using var host = Host(Settings(), redis: proxy.ConnectionString, outageReadTimeout: TimeSpan.FromMilliseconds(250));
        await RedisCacheTestReadiness.WarmAsync(host, ApplicationCacheDomain.Functions);
        using var scope = host.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IFunctionDiscoveryCacheService>();
        await cache.GetOrLoadAsync([1], Load);
        proxy.Disconnect();
        var handler = new FunctionConfigurationCacheInvalidationHandler(cache, NullLogger<FunctionConfigurationCacheInvalidationHandler>.Instance);
        await Assert.ThrowsAsync<ApplicationCacheInvalidationException>(() =>
            handler.HandleAsync(new FunctionConfigurationChanged { FunctionConfigurationId = 1 }, new TestEventContext()));
    }
}
