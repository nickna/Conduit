using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Consumers;
using ConduitLLM.Tests.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Http.Consumers;

public sealed class ModelMappingCacheInvalidationHandlerTests
{
    [Theory]
    [InlineData("Created")] [InlineData("Updated")] [InlineData("Deleted")]
    public async Task EveryMutationInvalidatesTheCompleteRoutingDomainThenDiscovery(string change)
    {
        var mappings = new Mock<IModelMappingCacheInvalidator>();
        var discovery = new Mock<IDiscoveryCacheService>();
        var order = new List<string>();
        using var cancellation = new CancellationTokenSource();
        mappings.Setup(cache => cache.InvalidateAsync(cancellation.Token)).Callback(() => order.Add("routing")).Returns(Task.CompletedTask);
        discovery.Setup(cache => cache.InvalidateAllDiscoveryAsync(cancellation.Token)).Callback(() => order.Add("discovery")).Returns(Task.CompletedTask);
        var handler = new ModelMappingCacheInvalidationHandler(mappings.Object, discovery.Object, NullLogger<ModelMappingCacheInvalidationHandler>.Instance);
        await handler.HandleAsync(new ModelMappingChanged { ChangeType = change, ModelAlias = null }, new TestEventContext { CancellationToken = cancellation.Token });
        Assert.Equal(new[] { "routing", "discovery" }, order);
    }
    [Fact]
    public async Task RoutingFailurePropagatesAndDoesNotRunDiscovery()
    {
        var mappings = new Mock<IModelMappingCacheInvalidator>();
        mappings.Setup(cache => cache.InvalidateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("store unavailable"));
        var discovery = new Mock<IDiscoveryCacheService>();
        var handler = new ModelMappingCacheInvalidationHandler(mappings.Object, discovery.Object, NullLogger<ModelMappingCacheInvalidationHandler>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new ModelMappingChanged(), new TestEventContext()));
        discovery.Verify(cache => cache.InvalidateAllDiscoveryAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task DiscoveryFailurePropagatesAfterRoutingExpiration()
    {
        var mappings = new Mock<IModelMappingCacheInvalidator>();
        var discovery = new Mock<IDiscoveryCacheService>();
        discovery.Setup(cache => cache.InvalidateAllDiscoveryAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("store unavailable"));
        var handler = new ModelMappingCacheInvalidationHandler(mappings.Object, discovery.Object, NullLogger<ModelMappingCacheInvalidationHandler>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new ModelMappingChanged(), new TestEventContext()));
        mappings.Verify(cache => cache.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
