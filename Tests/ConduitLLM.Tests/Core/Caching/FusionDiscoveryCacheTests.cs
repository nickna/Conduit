using System.Text.Json;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Internals.Distributed;

namespace ConduitLLM.Tests.Core.Caching;

public sealed class FusionDiscoveryCacheTests
{
    internal static ServiceProvider Host(string? redis = null, string? environment = null,
        Dictionary<string, string?>? settings = null, IDistributedCache? storage = null, TimeProvider? clock = null)
    {
        var values = settings ?? [];
        values["ApplicationCache:Environment"] = environment ?? $"test-{Guid.NewGuid():N}";
        values.TryAdd("ApplicationCache:DistributedReadTimeout", redis is null ? "00:00:00.250" : RedisCacheTestReadiness.HealthyReadTimeout);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddLogging();
        if (clock is not null) services.AddSingleton(clock);
        if (storage is not null) services.AddKeyedSingleton(ApplicationCacheOptions.ServiceKey, storage);
        services.AddConduitApplicationCache(configuration, "test", redis ?? "");
        services.AddDiscoveryCache(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static DiscoveryModelsResult Payload(bool pricing = false) => new()
    {
        Count = 1, Data = [JsonDocument.Parse(pricing ? """{"id":"model","pricing":{"input_cost":0.25}}""" : """{"id":"model"}""").RootElement.Clone()]
    };

    [Fact]
    public async Task ExpiredPricingFromLateLoaderIsRejectedAndDeadlineSurvivesIndependentL2()
    {
        var clock = new FusionPricingCacheTests.Clock();
        using var host = Host(clock: clock);
        var service = host.GetRequiredService<IDiscoveryCacheService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Payload(pricing: true); old.PricingRefreshAt = clock.Now.AddSeconds(10).UtcDateTime;
        var request = service.GetOrLoadAsync("priced", async _ => { entered.TrySetResult(); await release.Task; return old; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Now = clock.Now.AddSeconds(11); release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Null(await service.GetDiscoveryResultsAsync("priced"));
        Assert.False((await service.GetOrLoadAsync("priced", _ => Task.FromResult(Payload()))).Data[0].TryGetProperty("pricing", out _));
    }

    [SkippableFact]
    public async Task PricingDeadlineSurvivesIndependentRedisL2AndRejectsExpiredWarmSnapshot()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrEmpty(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        var clock = new FusionPricingCacheTests.Clock(); var environment = $"test-{Guid.NewGuid():N}";
        using var first = Host(redis, environment, settings: new() { ["ApplicationCache:DistributedReadTimeout"] = "00:00:01" }, clock: clock);
        using var second = Host(redis, environment, settings: new() { ["ApplicationCache:DistributedReadTimeout"] = "00:00:01" }, clock: clock);
        await RedisCacheTestReadiness.WarmAsync(first, ApplicationCacheDomain.Discovery);
        await RedisCacheTestReadiness.WarmAsync(second, ApplicationCacheDomain.Discovery);
        var old = Payload(pricing: true); old.PricingRefreshAt = clock.Now.AddSeconds(10).UtcDateTime;
        await first.GetRequiredService<IDiscoveryCacheService>().SetDiscoveryResultsAsync("priced", old);
        var reader = second.GetRequiredService<IDiscoveryCacheService>();
        Assert.Equal(old.PricingRefreshAt, (await reader.GetDiscoveryResultsAsync("priced"))!.PricingRefreshAt);
        clock.Now = clock.Now.AddSeconds(11);
        Assert.Null(await reader.GetDiscoveryResultsAsync("priced"));
        Assert.False((await reader.GetOrLoadAsync("priced", _ => Task.FromResult(Payload()))).Data[0].TryGetProperty("pricing", out _));
    }

    [Fact]
    public async Task HealthyConcurrentMisses_LoadOnceAndProtectReturnedAndInputOwnership()
    {
        using var host = Host();
        var service = host.GetRequiredService<IDiscoveryCacheService>();
        Assert.IsType<FusionDiscoveryCacheService>(service);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var source = Payload();
        async Task<DiscoveryModelsResult> Load(CancellationToken token)
        {
            Interlocked.Increment(ref loads);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return source;
        }
        var requests = Enumerable.Range(0, 32).Select(_ => service.GetOrLoadAsync("all", Load)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        var results = await Task.WhenAll(requests);
        Assert.Equal(1, loads);
        source.Data.Clear();
        results[0].Data.Clear();
        Assert.All(results.Skip(1), result => Assert.Single(result.Data));
        Assert.Single((await service.GetDiscoveryResultsAsync("all"))!.Data);
    }

    [Fact]
    public async Task KeysPreserveCapabilityVirtualKeyAndPricingVariantsAndBroadDependencies()
    {
        using var host = Host();
        var service = host.GetRequiredService<IDiscoveryCacheService>();
        var keys = new[] { DiscoveryCacheKeys.Build(), DiscoveryCacheKeys.Build(includePricing: true),
            DiscoveryCacheKeys.Build("chat"), DiscoveryCacheKeys.Build("chat", 1),
            DiscoveryCacheKeys.Build("chat", 2, true) };
        foreach (var key in keys) await service.SetDiscoveryResultsAsync(key, Payload(key.EndsWith("with_pricing")));
        foreach (var key in keys)
            Assert.Equal(key.EndsWith("with_pricing"), (await service.GetDiscoveryResultsAsync(key))!.Data[0].TryGetProperty("pricing", out _));
        await service.InvalidatePatternAsync("virtualkey:1:*");
        foreach (var key in keys) Assert.Null(await service.GetDiscoveryResultsAsync(key));
    }

    [Theory]
    [InlineData("Discovery:EnableCaching")]
    [InlineData("ApplicationCache:Domains:Discovery:Enabled")]
    public async Task DisabledCacheBypassesExistingEntriesAndLoadsEachRequest(string setting)
    {
        using var host = Host(settings: new() { [setting] = "false" });
        var service = host.GetRequiredService<IDiscoveryCacheService>();
        var loads = 0;
        await service.SetDiscoveryResultsAsync("all", Payload());
        for (var i = 0; i < 2; i++) await service.GetOrLoadAsync("all", _ => { loads++; return Task.FromResult(Payload()); });
        Assert.Equal(2, loads);
        Assert.Null(await service.GetDiscoveryResultsAsync("all"));
    }

    [Fact]
    public async Task BusinessFailuresAndCancellationPropagateWithoutDuplicateLoads()
    {
        using var host = Host();
        var service = host.GetRequiredService<IDiscoveryCacheService>();
        var failure = new RedisException("business loader exception, not a cache error");
        var loads = 0;
        Assert.Same(failure, await Assert.ThrowsAsync<RedisException>(() => service.GetOrLoadAsync("failure", _ =>
        {
            loads++;
            return Task.FromException<DiscoveryModelsResult>(failure);
        })));
        Assert.Equal(1, loads);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetOrLoadAsync("cancel", _ =>
        {
            loads++;
            return Task.FromResult(Payload());
        }, cancellation.Token));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task FailedCacheWriteUsesSuccessfulLoadOnceAndInvalidationFailureRemainsVisible()
    {
        var storage = new Mock<IDistributedCache>();
        storage.Setup(cache => cache.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RedisException("write failure"));
        var options = new ApplicationCacheOptions();
        var serializer = new ApplicationCacheSerializer();
        using var cache = new FusionCache(options.FusionOptions());
        cache.SetupDistributedCache(storage.Object, serializer);
        using var generation = new ApplicationCacheGeneration(cache, options, null);
        var service = new FusionDiscoveryCacheService(cache, options, generation,
            Options.Create(new DiscoveryCacheOptions()),
            NullLogger<FusionDiscoveryCacheService>.Instance);
        var loads = 0;
        var result = await service.GetOrLoadAsync("all", _ => { loads++; return Task.FromResult(Payload()); });
        Assert.Equal(1, loads);
        Assert.Single(result.Data);
        var failure = await Assert.ThrowsAsync<ApplicationCacheInvalidationException>(() => service.InvalidateAllDiscoveryAsync());
        Assert.Equal(ApplicationCacheDomain.Discovery, failure.Domain);
        Assert.NotNull(failure.InnerException);
    }

    [Fact]
    public async Task DomainMaximumTtlCapsExplicitDiscoveryDuration()
    {
        using var host = Host(settings: new() { ["ApplicationCache:Domains:Discovery:MaximumDuration"] = "00:00:00.050" });
        var service = host.GetRequiredService<IDiscoveryCacheService>();
        await service.SetDiscoveryResultsAsync("all", Payload());
        Assert.NotNull(await service.GetDiscoveryResultsAsync("all"));
        await Task.Delay(100);
        Assert.Null(await service.GetDiscoveryResultsAsync("all"));
    }

    [SkippableFact]
    public async Task RedisL2AndRestartReadWithoutLoadAndHonorInvalidationMarkers()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS to run real Redis contracts.");
        var environment = $"test-{Guid.NewGuid():N}";
        using (var writer = Host(redis, environment))
        {
            await RedisCacheTestReadiness.WarmAsync(writer, ApplicationCacheDomain.Discovery);
            await writer.GetRequiredService<IDiscoveryCacheService>().SetDiscoveryResultsAsync("all:with_pricing", Payload(true));
        }
        using (var reader = Host(redis, environment))
        {
            await RedisCacheTestReadiness.WarmAsync(reader, ApplicationCacheDomain.Discovery);
            var service = reader.GetRequiredService<IDiscoveryCacheService>();
            var result = await service.GetOrLoadAsync("all:with_pricing", _ => throw new InvalidOperationException("L2 must not load"));
            Assert.Equal(0.25m, result.Data[0].GetProperty("pricing").GetProperty("input_cost").GetDecimal());
            await service.InvalidateAllDiscoveryAsync();
        }
        using var restart = Host(redis, environment);
        await RedisCacheTestReadiness.WarmAsync(restart, ApplicationCacheDomain.Discovery);
        Assert.Null(await restart.GetRequiredService<IDiscoveryCacheService>().GetDiscoveryResultsAsync("all:with_pricing"));
    }

    [Fact]
    public async Task LoadRacingInvalidationCannotRemainCurrent()
    {
        using var host = Host();
        var service = host.GetRequiredService<IDiscoveryCacheService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequest = service.GetOrLoadAsync("race", async token =>
        {
            var snapshot = Payload();
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return snapshot;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.InvalidateAllDiscoveryAsync();
        release.SetResult();
        await oldRequest;
        var current = await service.GetOrLoadAsync("race", _ => Task.FromResult(new DiscoveryModelsResult { Count = 2 }));
        Assert.Equal(2, current.Count);
    }

    [SkippableFact]
    public async Task RedisLoadRacingRemoteInvalidationCannotRemainCurrentAfterRestart()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS to run real Redis contracts.");
        var environment = $"test-{Guid.NewGuid():N}";
        using var admin = Host(redis, environment);
        using var gateway = Host(redis, environment);
        await RedisCacheTestReadiness.WarmAsync(admin, ApplicationCacheDomain.Discovery);
        await RedisCacheTestReadiness.WarmAsync(gateway, ApplicationCacheDomain.Discovery);
        var writer = admin.GetRequiredService<IDiscoveryCacheService>();
        var reader = gateway.GetRequiredService<IDiscoveryCacheService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = reader.GetOrLoadAsync("race", async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return Payload();
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.InvalidateAllDiscoveryAsync().WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        using var restart = Host(redis, environment);
        await RedisCacheTestReadiness.WarmAsync(restart, ApplicationCacheDomain.Discovery);
        var current = await restart.GetRequiredService<IDiscoveryCacheService>().GetOrLoadAsync("race",
            _ => Task.FromResult(new DiscoveryModelsResult { Count = 2 }));
        Assert.Equal(2, current.Count);
    }

    [SkippableFact]
    public async Task RedisInvalidationRejectsEntryWrittenByClockAheadNode()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS to run real Redis contracts.");
        var environment = $"test-{Guid.NewGuid():N}";
        using var writer = Host(redis, environment);
        await RedisCacheTestReadiness.WarmAsync(writer, ApplicationCacheDomain.Discovery);
        var service = writer.GetRequiredService<IDiscoveryCacheService>();
        await service.SetDiscoveryResultsAsync("clock", Payload());
        var configuration = ConfigurationOptions.Parse(redis);
        configuration.AllowAdmin = true;
        using var observer = await ConnectionMultiplexer.ConnectAsync(configuration);
        var key = observer.GetServer(observer.GetEndPoints()[0]).Keys(pattern: $"*{environment}*clock*").Single().ToString();
        var storage = writer.GetRequiredKeyedService<IDistributedCache>(ApplicationCacheOptions.ServiceKey);
        var serializer = writer.GetRequiredService<ApplicationCacheSerializer>();
        var envelope = serializer.Deserialize<FusionCacheDistributedEntry<DiscoveryModelsResult>>((await storage.GetAsync(key))!)!;
        // Reproduce the persisted timestamp from a different node whose clock runs ahead.
        envelope.Timestamp = DateTime.UtcNow.AddSeconds(30).Ticks;
        await storage.SetAsync(key, serializer.Serialize(envelope), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        await service.InvalidateAllDiscoveryAsync();
        using var restart = Host(redis, environment);
        await RedisCacheTestReadiness.WarmAsync(restart, ApplicationCacheDomain.Discovery);
        Assert.Null(await restart.GetRequiredService<IDiscoveryCacheService>().GetDiscoveryResultsAsync("clock"));
    }

    [SkippableFact]
    public async Task RedisGenerationInitializationIsAtomicAcrossIndependentNodes()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS to run real Redis contracts.");
        var environment = $"test-{Guid.NewGuid():N}";
        var hosts = Enumerable.Range(0, 8).Select(_ => Host(redis, environment)).ToArray();
        try
        {
            var generations = await Task.WhenAll(hosts.Select(host => host.GetRequiredService<ApplicationCacheGeneration>()
                .GetAsync(ApplicationCacheDomain.Discovery).AsTask()));
            Assert.Single(generations.Distinct());
        }
        finally { foreach (var host in hosts) host.Dispose(); }
    }

    [SkippableFact]
    public async Task RedisGenerationMetadataLossCannotResurrectPreviousPayloads()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS to run real Redis contracts.");
        var environment = $"test-{Guid.NewGuid():N}";
        using var writer = Host(redis, environment);
        await RedisCacheTestReadiness.WarmAsync(writer, ApplicationCacheDomain.Discovery);
        var service = writer.GetRequiredService<IDiscoveryCacheService>();
        await service.SetDiscoveryResultsAsync("all", Payload());
        using var observer = await ConnectionMultiplexer.ConnectAsync(redis);
        var metadataKey = writer.GetRequiredService<ApplicationCacheOptions>().Prefix + "generation:discovery";
        Assert.True(await observer.GetDatabase().KeyDeleteAsync(metadataKey));
        using var restart = Host(redis, environment);
        await RedisCacheTestReadiness.WarmAsync(restart, ApplicationCacheDomain.Discovery);
        Assert.Null(await restart.GetRequiredService<IDiscoveryCacheService>().GetDiscoveryResultsAsync("all"));
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (await service.GetDiscoveryResultsAsync("all") is not null && deadline.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(20);
        Assert.Null(await service.GetDiscoveryResultsAsync("all"));
    }
}
