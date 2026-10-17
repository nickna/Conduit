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

[Collection("Function cost cache")]
public sealed class StoreBackedFunctionCostServiceTests
{
    private const int ConfigurationId = 9027;

    [Fact]
    public async Task CacheMissUsesUtcInstantAndCallerTokenThenCachesForBothTiers()
    {
        var cost = Cost();
        var store = Store(cost);
        var memory = new Mock<IMemoryCache>();
        var entries = new Dictionary<object, Mock<ICacheEntry>>();
        memory.Setup(cache => cache.CreateEntry(It.IsAny<object>())).Returns((object key) =>
        {
            var entry = new Mock<ICacheEntry>();
            entry.SetupAllProperties();
            entries.Add(key, entry);
            return entry.Object;
        });
        var distributed = new RecordingDistributedCache();
        var service = Service(store.Object, memory.Object, distributed);
        using var cancellation = new CancellationTokenSource();
        var before = DateTime.UtcNow;
        Assert.Same(cost, await service.GetCostForConfigurationAsync(ConfigurationId, cancellation.Token));
        var after = DateTime.UtcNow;
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId,
            It.Is<DateTime>(instant => instant.Kind == DateTimeKind.Utc && instant >= before && instant <= after), cancellation.Token), Times.Once);
        var payload = Assert.Single(distributed.PayloadWrites);
        Assert.Equal(cost.Id, JsonSerializer.Deserialize(payload.Value, FunctionsJsonContext.Default.FunctionCost)!.Id);
        Assert.Equal(TimeSpan.FromHours(1), payload.Options.AbsoluteExpirationRelativeToNow);
        Assert.Equal(cancellation.Token, payload.Token);
        var cachedEntry = entries[payload.Key].Object;
        Assert.Equal(TimeSpan.FromMinutes(15), cachedEntry.AbsoluteExpirationRelativeToNow);
        Assert.Same(cost, cachedEntry.Value);
        var marker = Assert.Single(distributed.MarkerWrites);
        Assert.NotEqual(marker.Key, payload.Key);
        Assert.True(Guid.TryParseExact(System.Text.Encoding.UTF8.GetString(marker.Value), "N", out var generation));
        Assert.NotEqual(Guid.Empty, generation);
        Assert.Null(marker.Options.AbsoluteExpiration);
        Assert.Null(marker.Options.AbsoluteExpirationRelativeToNow);
        Assert.Null(marker.Options.SlidingExpiration);
    }

    [Fact]
    public async Task LegacyServiceGeneratedPayloadReturnsDirectlyAndPopulatesMemoryWithoutExpiryRecheck()
    {
        using var legacyMemory = new MemoryCache(new MemoryCacheOptions());
        using var newMemory = new MemoryCache(new MemoryCacheOptions());
        var distributed = new RecordingDistributedCache();
        var cost = Cost();
        var repository = new Mock<IFunctionCostRepository>();
        repository.Setup(value => value.GetByIdAsync(cost.Id, It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        var mappings = new Mock<IFunctionCostMappingRepository>();
        mappings.Setup(value => value.GetByFunctionConfigurationIdAsync(ConfigurationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionCostMapping { FunctionCostId = cost.Id, FunctionConfigurationId = ConfigurationId, IsActive = true }]);
        var legacy = new FunctionCostService(repository.Object, mappings.Object, legacyMemory,
            NullLogger<FunctionCostService>.Instance, distributed);
        Assert.Same(cost, await legacy.GetCostForConfigurationAsync(ConfigurationId));
        var payload = Assert.Single(distributed.PayloadWrites);
        var persisted = JsonSerializer.Deserialize(payload.Value, FunctionsJsonContext.Default.FunctionCost)!;
        persisted.IsActive = false;
        persisted.ExpiryDate = DateTime.UtcNow.AddDays(-1);
        await distributed.ReplaceAsync(payload.Key, JsonSerializer.SerializeToUtf8Bytes(persisted, FunctionsJsonContext.Default.FunctionCost),
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
    public async Task NullResultsAreNotCached()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var distributed = new RecordingDistributedCache();
        var store = Store(null);
        var service = Service(store.Object, memory, distributed);
        Assert.Null(await service.GetCostForConfigurationAsync(ConfigurationId));
        Assert.Null(await service.GetCostForConfigurationAsync(ConfigurationId));
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Empty(distributed.PayloadWrites);
    }

    [Fact]
    public async Task LegacyUpdatesAndBothExplicitClearsRefreshConfigurationPricing()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var legacyMemory = new MemoryCache(new MemoryCacheOptions());
        var distributed = new RecordingDistributedCache();
        var currentCost = Cost();
        var store = new Mock<IFunctionPricingRuntimeStore>(MockBehavior.Strict);
        store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => currentCost);
        var service = Service(store.Object, memory, distributed);
        Assert.Same(currentCost, await service.GetCostForConfigurationAsync(ConfigurationId));
        var repository = new Mock<IFunctionCostRepository>();
        repository.Setup(value => value.GetByIdAsync(currentCost.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => currentCost);
        repository.Setup(value => value.UpdateAsync(It.IsAny<FunctionCost>(), It.IsAny<CancellationToken>()))
            .Callback<FunctionCost, CancellationToken>((cost, _) => currentCost = cost).ReturnsAsync(true);
        var legacy = new FunctionCostService(repository.Object, Mock.Of<IFunctionCostMappingRepository>(),
            legacyMemory, NullLogger<FunctionCostService>.Instance, distributed);

        var updated = Cost();
        updated.BaseCost = 0.25m;
        await legacy.UpdateCostAsync(updated);
        Assert.Same(updated, await service.GetCostForConfigurationAsync(ConfigurationId));

        currentCost = Cost();
        currentCost.BaseCost = 0.5m;
        await legacy.ClearCacheAsync();
        Assert.Same(currentCost, await service.GetCostForConfigurationAsync(ConfigurationId));

        currentCost = Cost();
        currentCost.BaseCost = 0.75m;
        await service.ClearCacheAsync();
        Assert.Same(currentCost, await service.GetCostForConfigurationAsync(ConfigurationId));
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task MarkerReadFailureBypassesWarmMemoryAndDistributedPayloads()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cost = Cost();
        var store = Store(cost);
        var distributed = new RecordingDistributedCache();
        var service = Service(store.Object, memory, distributed);
        Assert.Same(cost, await service.GetCostForConfigurationAsync(ConfigurationId));
        var payloadReads = distributed.Reads.Count(value => value.Key != distributed.MarkerKey);
        var payloadWrites = distributed.PayloadWrites.Count();
        var fresh = Cost();
        fresh.BaseCost = 0.25m;
        store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(fresh);
        distributed.FailMarkerReads = true;
        Assert.Same(fresh, await service.GetCostForConfigurationAsync(ConfigurationId));
        Assert.Same(fresh, await service.GetCostForConfigurationAsync(ConfigurationId));
        Assert.Equal(payloadReads, distributed.Reads.Count(value => value.Key != distributed.MarkerKey));
        Assert.Equal(payloadWrites, distributed.PayloadWrites.Count());
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task PayloadReadAndWriteFailuresStillUseDatabaseThenMemory()
    {
        using var bootstrapMemory = new MemoryCache(new MemoryCacheOptions());
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var distributed = new RecordingDistributedCache();
        await Service(Store(Cost()).Object, bootstrapMemory, distributed).GetCostForConfigurationAsync(ConfigurationId);
        distributed.FailPayloadReads = true;
        distributed.FailPayloadWrites = true;
        var cost = Cost();
        var store = Store(cost);
        var service = Service(store.Object, memory, distributed);
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
        var service = Service(store.Object, memory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCostForConfigurationAsync(ConfigurationId, cancelled.Token));
        store.VerifyNoOtherCalls();
        var cost = Cost();
        store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), CancellationToken.None)).ReturnsAsync(cost);
        Assert.Same(cost, await service.GetCostForConfigurationAsync(ConfigurationId));
        Assert.Same(cost, await service.GetCostForConfigurationAsync(ConfigurationId, cancelled.Token));
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), cancelled.Token), Times.Never);
        store.Verify(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), CancellationToken.None), Times.Once);
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
        using var bootstrapMemory = new MemoryCache(new MemoryCacheOptions());
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var distributed = new RecordingDistributedCache();
        await Service(Store(Cost()).Object, bootstrapMemory, distributed).GetCostForConfigurationAsync(ConfigurationId);
        var payload = Assert.Single(distributed.PayloadWrites);
        await distributed.ReplaceAsync(payload.Key, "invalid json"u8.ToArray(), payload.Options);
        var store = new Mock<IFunctionPricingRuntimeStore>(MockBehavior.Strict);
        store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("pricing store unavailable"));
        var service = Service(store.Object, memory, distributed);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCostForConfigurationAsync(ConfigurationId));
        Assert.Equal("pricing store unavailable", error.Message);
        Assert.False(memory.TryGetValue(payload.Key, out _));
        Assert.Single(distributed.PayloadWrites);
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

    private sealed record CacheWrite(string Key, byte[] Value, DistributedCacheEntryOptions Options, CancellationToken Token, bool IsPayload);

    private sealed class RecordingDistributedCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        private readonly List<CacheWrite> _writes = [];

        public List<(string Key, CancellationToken Token)> Reads { get; } = [];
        public IEnumerable<CacheWrite> PayloadWrites => _writes.Where(value => value.IsPayload);
        public IEnumerable<CacheWrite> MarkerWrites => _writes.Where(value => !value.IsPayload);
        public string? MarkerKey => MarkerWrites.FirstOrDefault()?.Key;
        public bool FailMarkerReads { get; set; }
        public bool FailPayloadReads { get; set; }
        public bool FailPayloadWrites { get; set; }

        public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Reads.Add((key, token));
            if ((FailMarkerReads && key == MarkerKey) ||
                (FailPayloadReads && MarkerKey is { } marker && key != marker))
                throw new InvalidOperationException("Redis read unavailable");
            return _inner.GetAsync(key, token);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            SetAsync(key, value, options).GetAwaiter().GetResult();

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            var payload = IsCostPayload(value);
            _writes.Add(new CacheWrite(key, value.ToArray(), options, token, payload));
            if (FailPayloadWrites && payload) throw new InvalidOperationException("Redis payload write unavailable");
            return _inner.SetAsync(key, value, options, token);
        }

        public Task ReplaceAsync(string key, byte[] value, DistributedCacheEntryOptions options) =>
            _inner.SetAsync(key, value, options);

        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);

        private static bool IsCostPayload(byte[] value)
        {
            try { return JsonSerializer.Deserialize(value, FunctionsJsonContext.Default.FunctionCost) is not null; }
            catch (JsonException) { return false; }
        }
    }

    private static FunctionCost Cost() => new()
    {
        Id = 91, CostName = "O'Hare 雪", ProviderType = FunctionProviderType.Exa, Purpose = null,
        PricingModel = FunctionPricingModel.FlatRate, CostPerExecution = 0.12500001m, BaseCost = 0.12500001m,
        PricingConfiguration = """{"nested":{"unknown":[null,true,"雪"]}}""", IsActive = true,
        EffectiveDate = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow.AddDays(-2), UpdatedAt = DateTime.UtcNow
    };
}
