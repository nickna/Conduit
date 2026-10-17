using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Services;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

namespace ConduitLLM.Tests.Functions.Services;

[Collection("Function cost cache")]
public sealed class FunctionCostCacheConsistencyTests
{
    private const int CostId = 11;
    private const int ConfigurationId = 27;
    private const string GenerationKey = "FunctionCost:Generation:v1";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ColdClearRefreshesAllReadShapesAcrossWarmInstances(bool distributed, bool typedClear)
    {
        using var fixture = new Fixture(distributed);
        var first = fixture.Legacy();
        var second = fixture.Legacy();
        var native = fixture.Typed();
        await AssertPriceAsync(first, 0.01m);
        await AssertPriceAsync(second, 0.01m);
        Assert.Equal(0.01m, (await native.GetCostForConfigurationAsync(ConfigurationId))!.BaseCost);
        fixture.Current = Cost(0.02m);

        IFunctionCostService cold = typedClear ? fixture.Typed() : fixture.Legacy();
        await cold.ClearCacheAsync();

        await AssertPriceAsync(first, 0.02m);
        await AssertPriceAsync(second, 0.02m);
        Assert.Equal(0.02m, (await native.GetCostForConfigurationAsync(ConfigurationId))!.BaseCost);
        if (distributed)
            Assert.Equal("unrelated", await fixture.Distributed.GetStringAsync("ModelCapability:sentinel"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreClearDatabaseReadCannotRepopulateTheNewGeneration(bool typed)
    {
        using var fixture = new Fixture(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<FunctionCost>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (typed)
            fixture.Store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(() => { entered.TrySetResult(); return release.Task; });
        else
            fixture.Repository.Setup(value => value.GetByIdAsync(CostId, It.IsAny<CancellationToken>()))
                .Returns(() => { entered.TrySetResult(); return release.Task; });
        var reader = typed ? (IFunctionCostService)fixture.Typed() : fixture.Legacy();
        var pending = reader.GetCostForConfigurationAsync(ConfigurationId);
        Exception primary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Current = Cost(0.02m);
            fixture.ResetReads();
            await fixture.Legacy().ClearCacheAsync();
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            release.TrySetResult(Cost(0.01m));
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception cleanup) when (primary is not null)
            {
                throw new AggregateException("Cache concurrency proof and pending-read cleanup both failed.", primary, cleanup);
            }
        }
        Assert.Equal(0.01m, (await pending.WaitAsync(TimeSpan.FromSeconds(5)))!.BaseCost);
        Assert.Equal(0.02m, (await reader.GetCostForConfigurationAsync(ConfigurationId))!.BaseCost);
        Assert.Equal(0.02m, (await fixture.Typed().GetCostForConfigurationAsync(ConfigurationId))!.BaseCost);
    }

    [Fact]
    public async Task GenerationReadFailureBypassesWarmMemoryAndFailedClearIsObservable()
    {
        using var fixture = new Fixture(true);
        var reader = fixture.Legacy();
        await AssertPriceAsync(reader, 0.01m);
        fixture.Current = Cost(0.02m);
        fixture.Distributed.FailGenerationReads = true;
        await AssertPriceAsync(reader, 0.02m);
        fixture.Distributed.FailGenerationWrites = true;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Typed().ClearCacheAsync());
        Assert.Equal("Generation write unavailable", failure.Message);
    }

    [Fact]
    public async Task MissingGenerationMarkerCannotResurrectRetiredEntries()
    {
        using var fixture = new Fixture(true);
        var reader = fixture.Legacy();
        await AssertPriceAsync(reader, 0.01m);
        var first = await fixture.Distributed.GetStringAsync(GenerationKey);
        await reader.ClearCacheAsync();
        fixture.Current = Cost(0.02m);
        await AssertPriceAsync(reader, 0.02m);
        var second = await fixture.Distributed.GetStringAsync(GenerationKey);
        Assert.NotEqual(first, second);
        await fixture.Distributed.RemoveAsync(GenerationKey);
        fixture.Current = Cost(0.03m);

        await AssertPriceAsync(reader, 0.03m);

        var third = await fixture.Distributed.GetStringAsync(GenerationKey);
        Assert.True(Guid.TryParseExact(third, "N", out _));
        Assert.NotEqual(first, third);
        Assert.NotEqual(second, third);
    }

    [Fact]
    public async Task MutationInvalidatesAnotherInstancesWarmMemoryAndLists()
    {
        using var fixture = new Fixture(true);
        var reader = fixture.Legacy();
        await AssertPriceAsync(reader, 0.01m);
        var native = fixture.Typed();
        Assert.Equal(0.01m, (await native.GetCostForConfigurationAsync(ConfigurationId))!.BaseCost);
        await fixture.Legacy().UpdateCostAsync(Cost(0.02m));
        await AssertPriceAsync(reader, 0.02m);
        Assert.Equal(0.02m, (await native.GetCostForConfigurationAsync(ConfigurationId))!.BaseCost);
    }

    private static async Task AssertPriceAsync(IFunctionCostService service, decimal expected)
    {
        Assert.Equal(expected, (await service.GetCostByIdAsync(CostId))!.BaseCost);
        Assert.Equal(expected, (await service.GetCostForConfigurationAsync(ConfigurationId))!.BaseCost);
        Assert.Equal(expected, Assert.Single(await service.ListCostsAsync()).BaseCost);
        Assert.Equal(expected, Assert.Single(await service.ListCostsAsync(activeOnly: true)).BaseCost);
    }

    private static FunctionCost Cost(decimal price) => new()
    {
        Id = CostId, CostName = "Cost namespace regression", BaseCost = price,
        IsActive = true, EffectiveDate = DateTime.UtcNow.AddDays(-1)
    };

    private sealed class Fixture : IDisposable
    {
        private readonly bool _distributed;
        private readonly List<MemoryCache> _memories = [];
        public FunctionCost Current { get; set; } = Cost(0.01m);
        public Mock<IFunctionCostRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionPricingRuntimeStore> Store { get; } = new(MockBehavior.Strict);
        public FaultingDistributedCache Distributed { get; } = new();
        private readonly Mock<IFunctionCostMappingRepository> _mappings = new(MockBehavior.Strict);

        public Fixture(bool distributed)
        {
            _distributed = distributed;
            ResetReads();
            Repository.Setup(value => value.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => [Current]);
            Repository.Setup(value => value.UpdateAsync(It.IsAny<FunctionCost>(), It.IsAny<CancellationToken>()))
                .Callback<FunctionCost, CancellationToken>((cost, _) => Current = cost).ReturnsAsync(true);
            _mappings.Setup(value => value.GetByFunctionConfigurationIdAsync(ConfigurationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new FunctionCostMapping { FunctionCostId = CostId, FunctionConfigurationId = ConfigurationId, IsActive = true }]);
            Distributed.SetString("ModelCapability:sentinel", "unrelated");
        }

        public void ResetReads()
        {
            Repository.Setup(value => value.GetByIdAsync(CostId, It.IsAny<CancellationToken>())).ReturnsAsync(() => Current);
            Store.Setup(value => value.FindEligibleCostAsync(ConfigurationId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Current);
        }

        public FunctionCostService Legacy() => new(Repository.Object, _mappings.Object, Memory(),
            NullLogger<FunctionCostService>.Instance, _distributed ? Distributed : null);
        public StoreBackedFunctionCostService Typed() => new(Store.Object, Memory(),
            NullLogger<StoreBackedFunctionCostService>.Instance, _distributed ? Distributed : null);
        private MemoryCache Memory()
        {
            var memory = new MemoryCache(new MemoryCacheOptions());
            _memories.Add(memory);
            return memory;
        }
        public void Dispose()
        {
            foreach (var memory in _memories) memory.Dispose();
            Distributed.Dispose();
        }
    }

    private sealed class FaultingDistributedCache : IDistributedCache, IDisposable
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        public bool FailGenerationReads { get; set; }
        public bool FailGenerationWrites { get; set; }
        public byte[] Get(string key) => _inner.Get(key);
        public Task<byte[]> GetAsync(string key, CancellationToken token = default)
        {
            if (key == GenerationKey && FailGenerationReads) throw new InvalidOperationException("Generation read unavailable");
            return _inner.GetAsync(key, token);
        }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            if (key == GenerationKey && FailGenerationWrites) throw new InvalidOperationException("Generation write unavailable");
            return _inner.SetAsync(key, value, options, token);
        }
        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
        public void Dispose() { }
    }
}
