using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Consumers;
using ConduitLLM.Tests.Messaging;

using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Gateway.Consumers;

[Trait("Category", "Unit")]
public class ModelCostCacheInvalidationHandlerTests
{
    [Fact]
    public async Task HandleAsync_ClearsBillingCacheAndPricingRulesCache()
    {
        var modelCostService = new Mock<IModelCostService>();
        var pricingRulesCache = new Mock<ICachedPricingRulesService>();
        var discoveryCache = new Mock<IDiscoveryCacheService>();
        var handler = new ModelCostCacheInvalidationHandler(
            modelCostService.Object,
            pricingRulesCache.Object,
            discoveryCache.Object,
            Mock.Of<ILogger<ModelCostCacheInvalidationHandler>>());
        var context = new TestEventContext();

        await handler.HandleAsync(CreateEvent(), context);

        modelCostService.Verify(
            service => service.ClearCacheAsync(context.CancellationToken),
            Times.Once);
        pricingRulesCache.Verify(
            service => service.InvalidateCacheAsync(42, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_InvalidatesDiscoveryCacheHoldingPricedModelPayloads()
    {
        var discoveryCache = new Mock<IDiscoveryCacheService>();
        var handler = new ModelCostCacheInvalidationHandler(
            Mock.Of<IModelCostService>(),
            null,
            discoveryCache.Object,
            Mock.Of<ILogger<ModelCostCacheInvalidationHandler>>());
        var context = new TestEventContext();

        await handler.HandleAsync(CreateEvent(), context);

        discoveryCache.Verify(
            service => service.InvalidateAllDiscoveryAsync(context.CancellationToken),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_BillingCacheFailure_PropagatesForTransportRetry()
    {
        var modelCostService = new Mock<IModelCostService>();
        modelCostService
            .Setup(service => service.ClearCacheAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache unavailable"));
        var handler = new ModelCostCacheInvalidationHandler(
            modelCostService.Object,
            null,
            Mock.Of<IDiscoveryCacheService>(),
            Mock.Of<ILogger<ModelCostCacheInvalidationHandler>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(CreateEvent(), new TestEventContext()));
    }

    private static ModelCostChanged CreateEvent() => new()
    {
        ModelCostId = 42,
        CostName = "Billing model pricing",
        ChangeType = "Updated",
        ChangedProperties = ["InputCost"]
    };
}
