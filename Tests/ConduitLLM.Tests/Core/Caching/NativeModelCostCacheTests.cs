using System.Collections.Concurrent;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Services;
using ConduitLLM.Persistence.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Tests.Core.Caching;

/// <summary>Contracts for Native's positive-only scalar cost cache and its shared invalidation.</summary>
[Collection(ApplicationCacheContractCollection.Name)]
public sealed class NativeModelCostCacheTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DistributedStore : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        public readonly ConcurrentQueue<string> Writes = new();
        public bool FailReads;
        /// <inheritdoc />
        public byte[]? Get(string key)
        {
            if (FailReads) throw new RedisException("Cache storage unavailable");
            return _inner.Get(key);
        }
        /// <inheritdoc />
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Get(key)); }
        /// <inheritdoc />
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        { Writes.Enqueue(key); _inner.Set(key, value, options); }
        /// <inheritdoc />
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Set(key, value, options); return Task.CompletedTask; }
        /// <inheritdoc />
        public void Refresh(string key) => _inner.Refresh(key);
        /// <inheritdoc />
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        /// <inheritdoc />
        public void Remove(string key) => _inner.Remove(key);
        /// <inheritdoc />
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
    }

    /// <summary>Builds a local cache host with generated serialization and an observable L2.</summary>
    private static ServiceProvider Host(Clock? clock = null, bool enabled = true)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationCache:Environment"] = $"native-cost-{Guid.NewGuid():N}",
            ["ApplicationCache:Domains:Costs:Enabled"] = enabled.ToString()
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(clock ?? new Clock());
        services.AddSingleton<IDistributedCache>(_ => new DistributedStore());
        services.AddConduitApplicationCache(configuration, "test", "");
        var host = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey)
            .SetupDistributedCache(host.GetRequiredService<IDistributedCache>());
        return host;
    }

    /// <summary>Uses the same explicit profile activation as the Gateway factory.</summary>
    private static FusionModelCostService Service(ServiceProvider host, IModelCostService inner,
        ModelCostCacheProfile profile = ModelCostCacheProfile.NativeRuntime) =>
        ActivatorUtilities.CreateInstance<FusionModelCostService>(host, inner, profile);

    /// <summary>Creates an active scalar-only runtime billing cost.</summary>
    private static ModelCost Cost(decimal price = 0.25m) => new()
    {
        Id = 42, CostName = "scalar cost", InputCostPerMillionTokens = price,
        OutputCostPerMillionTokens = 10m, IsActive = true,
        EffectiveDate = DateTime.UtcNow.AddDays(-1)
    };

    /// <summary>Provides an observable loader without persistence or background workers.</summary>
    private static Mock<IModelCostService> Inner(ModelCost? cost)
    {
        var inner = new Mock<IModelCostService>();
        inner.Setup(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        inner.Setup(value => value.GetCostForModelAsync("model", It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        return inner;
    }

    /// <summary>Concurrent ID misses coalesce and generated L2 envelopes retain detached owned prices.</summary>
    [Fact]
    public async Task GetCostByIdAsync_ConcurrentMisses_CoalescesAndReturnsOwnedResultsAsync()
    {
        var cost = Cost(); var inner = Inner(cost);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Setup(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>())).Returns(async () =>
        { entered.TrySetResult(); await release.Task; return cost; });
        using var host = Host(); var service = Service(host, inner.Object);
        var first = service.GetCostByIdAsync(42);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requests = Enumerable.Range(0, 15).Select(_ => service.GetCostByIdAsync(42)).Prepend(first).ToArray();
        try
        {
            Assert.All(requests, request => Assert.False(request.IsCompleted));
            inner.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { release.SetResult(); }
        var values = await Task.WhenAll(requests);
        values[0]!.InputCostPerMillionTokens = 99m;
        values[0]!.ModelProviderTypeAssociations.Add(new() { Id = 123 });
        Assert.All(values.Skip(1), value =>
        { Assert.Equal(0.25m, value!.InputCostPerMillionTokens); Assert.Empty(value.ModelProviderTypeAssociations); });
        // Force L2 reconstruction, exercising generated FusionCache envelope metadata.
        var generation = await host.GetRequiredService<ApplicationCacheGeneration>().GetAsync(ApplicationCacheDomain.Costs);
        var localOnly = host.GetRequiredService<ApplicationCacheOptions>().Entry(TimeSpan.FromHours(1));
        localOnly.SkipDistributedCacheRead = localOnly.SkipDistributedCacheWrite = true;
        await host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey)
            .RemoveAsync($"costs:{generation}:runtime-scalar-id-v1:modelcost:id:42", localOnly);
        Assert.Equal(0.25m, (await service.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
        inner.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Missing/future IDs create no negative payload and activate on the next read.</summary>
    [Fact]
    public async Task GetCostByIdAsync_MissingCost_RemainsUncachedAndObservesActivationAsync()
    {
        var clock = new Clock(); var cost = Cost(); cost.EffectiveDate = clock.Now.AddSeconds(10).UtcDateTime;
        var inner = Inner(null);
        // Match StoreBacked's active-only contract: the future date is hidden.
        inner.Setup(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => clock.Now.UtcDateTime < cost.EffectiveDate ? null : cost);
        using var host = Host(clock); var service = Service(host, inner.Object);
        Assert.Null(await service.GetCostByIdAsync(42)); Assert.Null(await service.GetCostByIdAsync(42));
        inner.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.DoesNotContain(((DistributedStore)host.GetRequiredService<IDistributedCache>()).Writes,
            key => key.Contains("runtime-scalar-id-v1:", StringComparison.Ordinal));
        var generation = await host.GetRequiredService<ApplicationCacheGeneration>().GetAsync(ApplicationCacheDomain.Costs);
        Assert.False((await host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey)
            .TryGetAsync<CostLookupResult>($"costs:{generation}:runtime-scalar-id-v1:modelcost:id:42")).HasValue);
        clock.Now = clock.Now.AddSeconds(10);
        Assert.NotNull(await service.GetCostByIdAsync(42));
        inner.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    /// <summary>Identifier reads always select from the source, including newly effective alternatives.</summary>
    [Fact]
    public async Task GetCostForModelAsync_IdentifierSelection_RemainsUncachedAsync()
    {
        var inner = Inner(Cost()); using var host = Host(); var service = Service(host, inner.Object);
        Assert.Equal(0.25m, (await service.GetCostForModelAsync("model"))!.InputCostPerMillionTokens);
        inner.Setup(value => value.GetCostForModelAsync("model", It.IsAny<CancellationToken>())).ReturnsAsync(Cost(0.75m));
        Assert.Equal(0.75m, (await service.GetCostForModelAsync("model"))!.InputCostPerMillionTokens);
        inner.Verify(value => value.GetCostForModelAsync("model", It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Empty(((DistributedStore)host.GetRequiredService<IDistributedCache>()).Writes);
    }

    /// <summary>Native scalar payloads and full management graphs are isolated but share invalidation.</summary>
    [Fact]
    public async Task ClearCacheAsync_RuntimeAndManagementProfiles_IsolatesAndInvalidatesBothAsync()
    {
        var graph = Cost(0.75m);
        graph.ModelProviderTypeAssociations.Add(new() { Id = 3, Identifier = "model", ModelId = 4,
            Model = new() { Id = 4, Name = "canonical model" } });
        var nativeInner = Inner(Cost()); var completeInner = Inner(graph);
        using var host = Host(); var native = Service(host, nativeInner.Object);
        var complete = Service(host, completeInner.Object, ModelCostCacheProfile.Complete);
        Assert.Empty((await native.GetCostByIdAsync(42))!.ModelProviderTypeAssociations);
        Assert.Equal("canonical model", (await complete.GetCostByIdAsync(42))!.ModelProviderTypeAssociations.Single().Model!.Name);
        Assert.Equal(0.25m, (await native.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
        nativeInner.Setup(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(Cost(1m));
        var freshGraph = Cost(2m); freshGraph.ModelProviderTypeAssociations = graph.ModelProviderTypeAssociations;
        completeInner.Setup(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(freshGraph);
        await native.ClearCacheAsync();
        Assert.Equal(1m, (await native.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
        Assert.Equal(2m, (await complete.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
    }

    /// <summary>A warm TTL never extends a price through its exclusive expiry boundary.</summary>
    [Fact]
    public async Task GetCostByIdAsync_ExpiredOrInactiveCost_ReturnsNullAsync()
    {
        var clock = new Clock(); var cost = Cost(); cost.ExpiryDate = clock.Now.AddSeconds(10).UtcDateTime;
        var inner = Inner(cost); using var host = Host(clock); var service = Service(host, inner.Object);
        Assert.NotNull(await service.GetCostByIdAsync(42));
        clock.Now = clock.Now.AddSeconds(10);
        Assert.Null(await service.GetCostByIdAsync(42));
        cost.ExpiryDate = null; cost.IsActive = false;
        Assert.Null(await service.GetCostByIdAsync(42));
    }

    /// <summary>Storage failures fall back once without disguising source failures or cancellation.</summary>
    [Fact]
    public async Task ReadCostAsync_StorageFailureOrCancellation_PreservesSourceSemanticsAsync()
    {
        var inner = Inner(Cost()); using var host = Host(); var service = Service(host, inner.Object);
        ((DistributedStore)host.GetRequiredService<IDistributedCache>()).FailReads = true;
        Assert.Equal(0.25m, (await service.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
        inner.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCostByIdAsync(42, new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCostForModelAsync("model", new CancellationToken(true)));
        inner.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        using var healthyHost = Host(); var failed = Inner(null);
        failed.Setup(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>())).ThrowsAsync(new RedisException("Source failure"));
        await Assert.ThrowsAsync<RedisException>(() => Service(healthyHost, failed.Object).GetCostByIdAsync(42));
        failed.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>An unexpected cached missing payload cannot hide an active native price.</summary>
    [Fact]
    public async Task GetCostByIdAsync_UnexpectedNegativePayload_LoadsCurrentPriceAsync()
    {
        var inner = Inner(Cost()); using var host = Host();
        var generation = await host.GetRequiredService<ApplicationCacheGeneration>().GetAsync(ApplicationCacheDomain.Costs);
        await host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey).SetAsync(
            $"costs:{generation}:runtime-scalar-id-v1:modelcost:id:42", new CostLookupResult(null, DateTime.UtcNow.AddHours(1)),
            host.GetRequiredService<ApplicationCacheOptions>().Entry(TimeSpan.FromHours(1)));
        Assert.Equal(0.25m, (await Service(host, inner.Object).GetCostByIdAsync(42))!.InputCostPerMillionTokens);
        inner.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Disabled caching remains direct, while a real active zero price remains valid.</summary>
    [Fact]
    public async Task GetCostByIdAsync_DisabledCache_ReadsDirectlyAndPreservesZeroPriceAsync()
    {
        var free = Cost(0); free.OutputCostPerMillionTokens = 0;
        var inner = Inner(free); using var host = Host(enabled: false); var service = Service(host, inner.Object);
        Assert.Equal(0m, (await service.GetCostByIdAsync(42))!.InputCostPerMillionTokens);
        Assert.NotNull(await service.GetCostByIdAsync(42));
        inner.Verify(value => value.GetCostByIdAsync(42, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>The decorator cannot expose native management operations through a cached list.</summary>
    [Fact]
    public async Task ManagementOperations_NativeRuntimeProfile_RemainUnsupportedAsync()
    {
        using var host = Host(); var service = Service(host,
            new StoreBackedModelCostService(Mock.Of<IModelProviderMappingRuntimeStore>()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ListModelCostsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => service.AddModelCostAsync(Cost()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.UpdateModelCostAsync(Cost()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.DeleteModelCostAsync(42));
    }
}
