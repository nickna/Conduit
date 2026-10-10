using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Authentication;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Security.Options;

namespace ConduitLLM.Tests.Gateway.Authentication;

[Trait("Category", "Unit")]
[Trait("Component", "VirtualKeyHubFilter")]
public sealed class VirtualKeyHubFilterPublicHubTests
{
    private readonly Mock<IVirtualKeyRuntimeService> _virtualKeys = new();
    private readonly PublicVideoGenerationHub _hub = new(
        Mock.Of<ILogger<PublicVideoGenerationHub>>(),
        Mock.Of<IEphemeralKeyService>());

    [Fact]
    public async Task OnConnectedAsync_PublicHub_DoesNotRequireConnectionVirtualKey()
    {
        var context = CreateCallerContext();
        var lifetime = new HubLifetimeContext(
            context,
            Mock.Of<IServiceProvider>(),
            _hub);
        var nextCalled = false;

        await CreateFilter().OnConnectedAsync(lifetime, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        _virtualKeys.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeMethodAsync_PublicHub_DefersToEphemeralKeyMethodAuthentication()
    {
        var method = typeof(PublicVideoGenerationHub)
            .GetMethod(nameof(PublicVideoGenerationHub.SubscribeToTask))!;
        var invocation = new HubInvocationContext(
            CreateCallerContext(),
            Mock.Of<IServiceProvider>(),
            _hub,
            method,
            ["task-id", "ephemeral-key"]);
        var nextCalled = false;

        await CreateFilter().InvokeMethodAsync(invocation, _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(nextCalled);
        _virtualKeys.VerifyNoOtherCalls();
    }

    private VirtualKeyHubFilter CreateFilter() => new(
        _virtualKeys.Object,
        Mock.Of<ILogger<VirtualKeyHubFilter>>(),
        Options.Create(new GatewaySecurityOptions()));

    private static HubCallerContext CreateCallerContext()
    {
        var context = new Mock<HubCallerContext>();
        context.Setup(value => value.Items).Returns(new Dictionary<object, object?>());
        return context.Object;
    }
}
