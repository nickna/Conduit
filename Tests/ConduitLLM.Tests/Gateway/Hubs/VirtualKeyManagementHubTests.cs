using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Metrics;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit.Abstractions;

namespace ConduitLLM.Tests.Gateway.Hubs;

public sealed class VirtualKeyManagementHubTests(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly Mock<IVirtualKeyRuntimeService> _keys = new();
    private readonly Mock<ISingleClientProxy> _caller = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnConnectedAsync_JoinsAdminGroupOnlyForTrustedAdministrator(bool administrator)
    {
        SetupAuthServiceIsAdmin(administrator);
        var hub = CreateHub();
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId))
            .ReturnsAsync(Key(DefaultVirtualKeyId));

        await hub.OnConnectedAsync();

        VerifyAddedToGroup($"vkey-{DefaultVirtualKeyId}");
        MockGroups.Verify(service => service.AddToGroupAsync(DefaultConnectionId, "admin",
            It.IsAny<CancellationToken>()), administrator ? Times.Once() : Times.Never());
        _caller.Verify(client => client.SendCoreAsync("VirtualKeyStatus",
            It.Is<object[]>(args => args.Length == 1 && args[0] is VirtualKeyStatusNotification &&
                ((VirtualKeyStatusNotification)args[0]).KeyId == DefaultVirtualKeyId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribeToKeyManagement_NonAdminCannotReadTargetOrJoinManagementGroup()
    {
        SetupAuthServiceIsAdmin(false);
        var hub = CreateHub();

        await hub.SubscribeToKeyManagement(456);

        _keys.VerifyNoOtherCalls();
        Assert.Empty(MockGroups.Invocations);
        _caller.Verify(client => client.SendCoreAsync("Error",
            It.Is<object[]>(args => args.Length == 1 && args[0] is HubErrorNotification &&
                ((HubErrorNotification)args[0]).Message == "Unauthorized: Admin privileges required"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribeToKeyManagement_AdminReadsTargetAndReceivesExistingStatus()
    {
        SetupAuthServiceIsAdmin(true);
        var hub = CreateHub();
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(456)).ReturnsAsync(Key(456));

        await hub.SubscribeToKeyManagement(456);

        VerifyAddedToGroup("vkey-mgmt-456");
        _keys.Verify(service => service.GetVirtualKeyInfoForValidationAsync(456), Times.Once);
        _caller.Verify(client => client.SendCoreAsync("VirtualKeyStatus",
            It.Is<object[]>(args => args.Length == 1 && args[0] is VirtualKeyStatusNotification &&
                ((VirtualKeyStatusNotification)args[0]).KeyId == 456), It.IsAny<CancellationToken>()), Times.Once);
        _caller.Verify(client => client.SendCoreAsync("SubscribedToKeyManagement",
            It.Is<object[]>(args => args.Length == 1 && (int)args[0] == 456),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCurrentKeyStatus_AlwaysLooksUpAuthenticatedOwner()
    {
        var hub = CreateHub();
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId))
            .ReturnsAsync(Key(DefaultVirtualKeyId));

        await hub.GetCurrentKeyStatus();

        _keys.Verify(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId), Times.Once);
        _keys.VerifyNoOtherCalls();
        _caller.Verify(client => client.SendCoreAsync("VirtualKeyStatus",
            It.Is<object[]>(args => args.Length == 1 && args[0] is VirtualKeyStatusNotification &&
                ((VirtualKeyStatusNotification)args[0]).KeyId == DefaultVirtualKeyId &&
                ((VirtualKeyStatusNotification)args[0]).CurrentSpend == 3m &&
                ((VirtualKeyStatusNotification)args[0]).MaxBudget == 25m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private VirtualKeyManagementHub CreateHub()
    {
        SetupAuthServiceReturnsVirtualKey(DefaultVirtualKeyId);
        MockClients.SetupGet(service => service.Caller).Returns(_caller.Object);
        return new VirtualKeyManagementHub((SignalRMetrics)RealMetrics,
            NullLogger<VirtualKeyManagementHub>.Instance, MockServiceProvider.Object, _keys.Object)
        {
            Context = CreateHubCallerContext().Object,
            Groups = MockGroups.Object,
            Clients = MockClients.Object
        };
    }

    private static VirtualKey Key(int id) => new()
    {
        Id = id, KeyName = $"key-{id}", IsEnabled = true,
        VirtualKeyGroup = new VirtualKeyGroup { Id = 1, Balance = 25m, LifetimeSpent = 3m }
    };
}
