using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Moq;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class StoreBackedModelCostServiceTests
{
    private readonly Mock<IModelProviderMappingRuntimeStore> _store = new();

    [Fact]
    public async Task IdLookupMapsTheCompleteRequestBillingShape()
    {
        var record = NewCost();
        _store.Setup(store => store.GetModelCostByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        var service = new StoreBackedModelCostService(_store.Object);

        var cost = await service.GetCostByIdAsync(42);

        Assert.NotNull(cost);
        Assert.Equal(PricingModel.Standard, cost.PricingModel);
        Assert.Equal(2.5m, cost.InputCostPerMillionTokens);
        Assert.Equal(10m, cost.OutputCostPerMillionTokens);
        Assert.Equal(0.25m, cost.CachedInputCostPerMillionTokens);
        Assert.Equal(12m, cost.ReasoningCostPerMillionTokens);
    }

    [Fact]
    public async Task IdentifierLookupDelegatesToTheFixedShapeStore()
    {
        var record = NewCost();
        _store.Setup(store => store.GetModelCostForIdentifierAsync(
                "provider/model",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        var service = new StoreBackedModelCostService(_store.Object);

        var cost = await service.GetCostForModelAsync("provider/model");

        Assert.Equal(record.Id, cost!.Id);
    }

    [Fact]
    public async Task InactiveOrExpiredCostIsNotUsedForBilling()
    {
        var record = NewCost(DateTime.UtcNow.AddMinutes(-1));
        _store.Setup(store => store.GetModelCostByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        var service = new StoreBackedModelCostService(_store.Object);

        Assert.Null(await service.GetCostByIdAsync(42));
    }

    [Fact]
    public async Task ManagementOperationsRemainOutsideTheNativeGateway()
    {
        var service = new StoreBackedModelCostService(_store.Object);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.ListModelCostsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => service.AddModelCostAsync(new ModelCost()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.UpdateModelCostAsync(new ModelCost()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.DeleteModelCostAsync(42));
        await service.ClearCacheAsync();
    }

    private static ModelCostRuntimeRecord NewCost(DateTime? expiryDate = null) => new()
    {
        Id = 42,
        CostName = "runtime cost",
        PricingModel = (int)PricingModel.Standard,
        InputCostPerMillionTokens = 2.5m,
        OutputCostPerMillionTokens = 10m,
        CachedInputCostPerMillionTokens = 0.25m,
        ReasoningCostPerMillionTokens = 12m,
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        UpdatedAt = DateTime.UtcNow,
        ModelType = "chat",
        IsActive = true,
        EffectiveDate = DateTime.UtcNow.AddDays(-1),
        ExpiryDate = expiryDate
    };
}
