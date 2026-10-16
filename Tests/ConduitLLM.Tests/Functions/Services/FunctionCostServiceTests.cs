using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Functions.Services;

[Collection("Function cost cache")]
public class FunctionCostServiceTests
{
    private const int CostId = 11;
    private const int FunctionConfigurationId = 27;

    [Fact]
    public async Task UpdateCostAsync_InvalidatesPerCostAndPerConfigurationMemoryCacheEntries()
    {
        var fixture = new ServiceFixture();
        var service = fixture.CreateService(new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal(0.01m, (await service.GetCostByIdAsync(CostId))?.BaseCost);
        Assert.Equal(0.01m, (await service.GetCostForConfigurationAsync(FunctionConfigurationId))?.BaseCost);

        await service.UpdateCostAsync(fixture.CreateUpdatedCost());

        Assert.Equal(0.02m, (await service.GetCostByIdAsync(CostId))?.BaseCost);
        Assert.Equal(0.02m, (await service.GetCostForConfigurationAsync(FunctionConfigurationId))?.BaseCost);
    }

    [Fact]
    public async Task UpdateCostAsync_InvalidatesPerCostAndPerConfigurationDistributedCacheEntries()
    {
        var fixture = new ServiceFixture();
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var firstInstance = fixture.CreateService(
            new MemoryCache(new MemoryCacheOptions()),
            distributedCache);

        Assert.Equal(0.01m, (await firstInstance.GetCostByIdAsync(CostId))?.BaseCost);
        Assert.Equal(0.01m, (await firstInstance.GetCostForConfigurationAsync(FunctionConfigurationId))?.BaseCost);

        await firstInstance.UpdateCostAsync(fixture.CreateUpdatedCost());

        var secondInstance = fixture.CreateService(
            new MemoryCache(new MemoryCacheOptions()),
            distributedCache);

        Assert.Equal(0.02m, (await secondInstance.GetCostByIdAsync(CostId))?.BaseCost);
        Assert.Equal(0.02m, (await secondInstance.GetCostForConfigurationAsync(FunctionConfigurationId))?.BaseCost);
    }

    private sealed class ServiceFixture
    {
        private readonly Mock<IFunctionCostRepository> _costRepository = new();
        private readonly Mock<IFunctionCostMappingRepository> _mappingRepository = new();
        private FunctionCost _currentCost;

        public ServiceFixture()
        {
            _currentCost = CreateCost(0.01m);

            _costRepository
                .Setup(repository => repository.GetByIdAsync(CostId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => _currentCost);
            _costRepository
                .Setup(repository => repository.UpdateAsync(It.IsAny<FunctionCost>(), It.IsAny<CancellationToken>()))
                .Callback<FunctionCost, CancellationToken>((cost, _) => _currentCost = cost)
                .ReturnsAsync(true);
            _mappingRepository
                .Setup(repository => repository.GetByFunctionConfigurationIdAsync(
                    FunctionConfigurationId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                [
                    new FunctionCostMapping
                    {
                        Id = 1,
                        FunctionConfigurationId = FunctionConfigurationId,
                        FunctionCostId = CostId,
                        IsActive = true
                    }
                ]);
        }

        public FunctionCostService CreateService(
            IMemoryCache memoryCache,
            IDistributedCache? distributedCache = null)
        {
            return new FunctionCostService(
                _costRepository.Object,
                _mappingRepository.Object,
                memoryCache,
                Mock.Of<ILogger<FunctionCostService>>(),
                distributedCache);
        }

        public FunctionCost CreateUpdatedCost() => CreateCost(0.02m);

        private static FunctionCost CreateCost(decimal baseCost)
        {
            var cost = new FunctionCost
            {
                Id = CostId,
                CostName = "Test cost",
                ProviderType = FunctionProviderType.Exa,
                PricingModel = FunctionPricingModel.FlatRate,
                BaseCost = baseCost,
                IsActive = true,
                EffectiveDate = DateTime.UtcNow.AddDays(-1)
            };
            cost.FunctionMappings.Add(new FunctionCostMapping
            {
                Id = 1,
                FunctionConfigurationId = FunctionConfigurationId,
                FunctionCostId = CostId,
                IsActive = true
            });
            return cost;
        }
    }
}
