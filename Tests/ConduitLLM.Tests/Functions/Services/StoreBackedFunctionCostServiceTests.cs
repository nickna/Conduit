using System.Text.Json;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Services;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

namespace ConduitLLM.Tests.Functions.Services;

public sealed class StoreBackedFunctionCostServiceTests
{
    private const int ConfigurationId = 9027;
    private const string Key = "FunctionCost:Config:9027";

    [Fact]
    public async Task CacheMissUsesUtcInstantAndCallerTokenThenCachesForBothTiers()
    {
        var cost = Cost();
        var store = Store(cost);
        var memory = new Mock<IMemoryCache>();
        var entry = new Mock<ICacheEntry>();
        entry.SetupAllProperties();
        memory.Setup(cache => cache.CreateEntry(Key)).Returns(entry.Object);
        var distributed = new Mock<IDistributedCache>();
        var service = Service(store.Object, memory.Object, distributed.Object);
        using var cancellation = new CancellationTokenSource();
        var before = DateTime.UtcNow;
        Assert.Same(cost, await service.GetCostForConfigurationAsync(ConfigurationId, cancellation.Token));
        var after = DateTime.UtcNow;
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId,
            It.Is<DateTime>(instant => instant.Kind == DateTimeKind.Utc && instant >= before && instant <= after), cancellation.Token), Times.Once);
        Assert.Equal(TimeSpan.FromMinutes(15), entry.Object.AbsoluteExpirationRelativeToNow);
        Assert.Same(cost, entry.Object.Value);
        distributed.Verify(cache => cache.SetAsync(Key,
            It.Is<byte[]>(payload => JsonSerializer.Deserialize(payload, FunctionsJsonContext.Default.FunctionCost)!.Id == cost.Id),
            It.Is<DistributedCacheEntryOptions>(options => options.AbsoluteExpirationRelativeToNow == TimeSpan.FromHours(1)),
            cancellation.Token), Times.Once);
    }

    [Fact]
    public async Task ExistingLegacyPayloadReturnsDirectlyAndPopulatesMemoryWithoutExpiryRecheck()
    {
        using var legacyMemory = new MemoryCache(new MemoryCacheOptions());
        using var newMemory = new MemoryCache(new MemoryCacheOptions());
        var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cost = Cost();
        var repository = new Mock<IFunctionCostRepository>();
        repository.Setup(value => value.GetByIdAsync(cost.Id, It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        var mappings = new Mock<IFunctionCostMappingRepository>();
        mappings.Setup(value => value.GetByFunctionConfigurationIdAsync(ConfigurationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionCostMapping { FunctionCostId = cost.Id, FunctionConfigurationId = ConfigurationId, IsActive = true }]);
        var legacy = new FunctionCostService(repository.Object, mappings.Object, legacyMemory,
            NullLogger<FunctionCostService>.Instance, distributed);
        Assert.Same(cost, await legacy.GetCostForConfigurationAsync(ConfigurationId));
        var payload = (await distributed.GetAsync(Key))!;
        var persisted = JsonSerializer.Deserialize(payload, FunctionsJsonContext.Default.FunctionCost)!;
        persisted.IsActive = false;
        persisted.ExpiryDate = DateTime.UtcNow.AddDays(-1);
        await distributed.SetAsync(Key, JsonSerializer.SerializeToUtf8Bytes(persisted, FunctionsJsonContext.Default.FunctionCost),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
        var store = new Mock<IFunctionPricingRuntimeStore>(MockBehavior.Strict);
        var service = Service(store.Object, newMemory, distributed);
        var fromRedis = (await service.GetCostForConfigurationAsync(ConfigurationId))!;
        Assert.Equal(cost.Id, fromRedis.Id);
        Assert.False(fromRedis.IsActive);
        Assert.Equal(persisted.ExpiryDate, fromRedis.ExpiryDate);
        Assert.Equal(cost.BaseCost, fromRedis.BaseCost);
        Assert.Equal(cost.PricingConfiguration, fromRedis.PricingConfiguration);
        Assert.Same(fromRedis, await service.GetCostForConfigurationAsync(ConfigurationId));
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NullResultsAreNotCachedAndLegacyUpdatesInvalidateTheSharedKey()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = Store(null);
        var service = Service(store.Object, memory, distributed);
        Assert.Null(await service.GetCostForConfigurationAsync(ConfigurationId));
        Assert.Null(await service.GetCostForConfigurationAsync(ConfigurationId));
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Null(await distributed.GetAsync(Key));
        store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(Cost());
        Assert.NotNull(await service.GetCostForConfigurationAsync(ConfigurationId));
        var cost = Cost();
        cost.FunctionMappings.Add(new FunctionCostMapping { FunctionConfigurationId = ConfigurationId, FunctionCostId = cost.Id });
        var repository = new Mock<IFunctionCostRepository>();
        repository.Setup(value => value.GetByIdAsync(cost.Id, It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        repository.Setup(value => value.UpdateAsync(It.IsAny<FunctionCost>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var legacy = new FunctionCostService(repository.Object, Mock.Of<IFunctionCostMappingRepository>(),
            memory, NullLogger<FunctionCostService>.Instance, distributed);
        await legacy.UpdateCostAsync(cost);
        Assert.False(memory.TryGetValue(Key, out _));
        Assert.Null(await distributed.GetAsync(Key));
        Assert.NotNull(await service.GetCostForConfigurationAsync(ConfigurationId));
        // Match the existing list-only clear behavior; this inherited quirk is separate.
        await SeedListsAsync();
        await legacy.ClearCacheAsync();
        await AssertListsRemovedAsync();
        Assert.True(memory.TryGetValue(Key, out _));
        await SeedListsAsync();
        await service.ClearCacheAsync();
        await AssertListsRemovedAsync();
        Assert.True(memory.TryGetValue(Key, out _));
        Assert.NotNull(await distributed.GetAsync(Key));

        async Task SeedListsAsync()
        {
            foreach (var key in new[] { "FunctionCost:All", "FunctionCost:All_Active" })
            {
                memory.Set(key, new List<FunctionCost> { cost });
                await distributed.SetAsync(key, "[]"u8.ToArray(), new DistributedCacheEntryOptions());
            }
        }

        async Task AssertListsRemovedAsync()
        {
            foreach (var key in new[] { "FunctionCost:All", "FunctionCost:All_Active" })
            {
                Assert.False(memory.TryGetValue(key, out _));
                Assert.Null(await distributed.GetAsync(key));
            }
        }
    }

    [Fact]
    public async Task RedisReadAndWriteFailuresStillUseDatabaseThenMemory()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(cache => cache.GetAsync(Key, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Redis unavailable"));
        distributed.Setup(cache => cache.SetAsync(Key, It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis unavailable"));
        var cost = Cost();
        var store = Store(cost);
        var service = Service(store.Object, memory, distributed.Object);
        Assert.Same(cost, await service.GetCostForConfigurationAsync(ConfigurationId));
        Assert.Same(cost, await service.GetCostForConfigurationAsync(ConfigurationId));
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UncachedCancellationPropagatesButMemoryHitRetainsLegacyBehavior()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var store = new Mock<IFunctionPricingRuntimeStore>(MockBehavior.Strict);
        store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), cancelled.Token))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));
        var service = Service(store.Object, memory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCostForConfigurationAsync(ConfigurationId, cancelled.Token));
        Assert.False(memory.TryGetValue(Key, out _));
        var cost = Cost();
        memory.Set(Key, cost);
        Assert.Same(cost, await service.GetCostForConfigurationAsync(ConfigurationId, cancelled.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidConfigurationIdsFailBeforeCachesOrStore(int id)
    {
        var store = new Mock<IFunctionPricingRuntimeStore>(MockBehavior.Strict);
        var memory = new Mock<IMemoryCache>(MockBehavior.Strict);
        var service = Service(store.Object, memory.Object);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.GetCostForConfigurationAsync(id));
        Assert.Equal("functionConfigurationId", exception.ParamName);
        store.VerifyNoOtherCalls();
        memory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExistingCalculatorUsesAdapterForEstimatesAndActualCosts()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = Store(Cost());
        var calculator = new FunctionCostCalculationService(Service(store.Object, memory),
            NullLogger<FunctionCostCalculationService>.Instance);
        Assert.Equal(0.12500001m, await calculator.EstimateCostAsync(ConfigurationId, new Dictionary<string, object> { ["query"] = "snow" }));
        Assert.Equal(0.12500001m, await calculator.CalculateCostAsync(ConfigurationId, new FunctionExecutionUsage()));
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnextractedManagementHasNoRepositoryFallback()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new Mock<IFunctionPricingRuntimeStore>(MockBehavior.Strict);
        var service = Service(store.Object, memory);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetCostByIdAsync(1));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ListCostsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CreateCostAsync(Cost()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.UpdateCostAsync(Cost()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.DeleteCostAsync(1));
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MalformedRedisFallsThroughButStoreFailuresPropagateWithoutCaching()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(cache => cache.GetAsync(Key, It.IsAny<CancellationToken>())).ReturnsAsync("invalid json"u8.ToArray());
        var store = new Mock<IFunctionPricingRuntimeStore>(MockBehavior.Strict);
        store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("pricing store unavailable"));
        var service = Service(store.Object, memory, distributed.Object);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCostForConfigurationAsync(ConfigurationId));
        Assert.Equal("pricing store unavailable", error.Message);
        Assert.False(memory.TryGetValue(Key, out _));
        distributed.Verify(cache => cache.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingPricingRetainsCalculatorFailureAndAuthoritativeCostFallback()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var calculator = new FunctionCostCalculationService(Service(Store(null).Object, memory),
            NullLogger<FunctionCostCalculationService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => calculator.EstimateCostAsync(ConfigurationId,
            new Dictionary<string, object> { ["query"] = "snow" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => calculator.CalculateCostAsync(ConfigurationId, new FunctionExecutionUsage()));
        Assert.Equal(0.0625m, await calculator.CalculateCostAsync(ConfigurationId, new FunctionExecutionUsage { ProviderReportedCost = 0.0625m }));
        Assert.Equal(0m, await calculator.CalculateCostAsync(ConfigurationId, new FunctionExecutionUsage { ProviderReportedCost = 0m }));
    }

    private static Mock<IFunctionPricingRuntimeStore> Store(FunctionCost? cost)
    {
        var store = new Mock<IFunctionPricingRuntimeStore>(MockBehavior.Strict);
        store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        return store;
    }

    private static StoreBackedFunctionCostService Service(IFunctionPricingRuntimeStore store, IMemoryCache memory, IDistributedCache? distributed = null) =>
        new(store, memory, NullLogger<StoreBackedFunctionCostService>.Instance, distributed);

    private static FunctionCost Cost() => new()
    {
        Id = 91, CostName = "O'Hare 雪", ProviderType = FunctionProviderType.Exa, Purpose = null,
        PricingModel = FunctionPricingModel.FlatRate, CostPerExecution = 0.12500001m, BaseCost = 0.12500001m,
        PricingConfiguration = """{"nested":{"unknown":[null,true,"雪"]}}""", IsActive = true,
        EffectiveDate = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow.AddDays(-2), UpdatedAt = DateTime.UtcNow
    };
}
