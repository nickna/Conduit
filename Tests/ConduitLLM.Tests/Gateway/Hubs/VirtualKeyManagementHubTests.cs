using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Services;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit.Abstractions;

namespace ConduitLLM.Tests.Gateway.Hubs;

public sealed class VirtualKeyManagementHubTests(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly Mock<IVirtualKeyRuntimeService> _keys = new();
    private readonly Mock<ISingleClientProxy> _caller = new();
    private readonly List<ServiceProvider> _providers = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnConnectedAsync_JoinsAdminGroupOnlyForTrustedAdministrator(bool administrator)
    {
        var hub = CreateHub(administrator);
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Key(DefaultVirtualKeyId, administrator));

        await hub.OnConnectedAsync();

        VerifyAddedToGroup($"vkey-{DefaultVirtualKeyId}");
        MockGroups.Verify(service => service.AddToGroupAsync(DefaultConnectionId, "admin",
            It.IsAny<CancellationToken>()), administrator ? Times.Once() : Times.Never());
        MockGroups.Verify(service => service.AddToGroupAsync(DefaultConnectionId, $"vkey-lifecycle-owner-{DefaultVirtualKeyId}",
            It.IsAny<CancellationToken>()), administrator ? Times.Never() : Times.Once());
        VerifyAddedToGroup($"vkey-status-{DefaultVirtualKeyId}");
        _keys.Verify(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId,
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        _caller.Verify(client => client.SendCoreAsync("VirtualKeyStatus",
            It.Is<object[]>(args => args.Length == 1 && args[0] is VirtualKeyStatusNotification &&
                ((VirtualKeyStatusNotification)args[0]).KeyId == DefaultVirtualKeyId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribeToKeyManagement_NonAdminCannotReadTargetOrJoinManagementGroup()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        MockGroups.Invocations.Clear();
        _keys.Invocations.Clear();
        _caller.Invocations.Clear();

        await hub.SubscribeToKeyManagement(456);

        _keys.Verify(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId,
            It.IsAny<CancellationToken>()), Times.Once);
        _keys.VerifyNoOtherCalls();
        VerifyRemovedFromGroup("admin");
        VerifyAddedToGroup($"vkey-lifecycle-owner-{DefaultVirtualKeyId}");
        _caller.Verify(client => client.SendCoreAsync("Error",
            It.Is<object[]>(args => args.Length == 1 && args[0] is HubErrorNotification &&
                ((HubErrorNotification)args[0]).Message == "Unauthorized: Admin privileges required"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribeToKeyManagement_AdminReadsTargetAndReceivesExistingStatus()
    {
        var hub = CreateHub(administrator: true);
        await hub.OnConnectedAsync();
        MockGroups.Invocations.Clear();
        _keys.Invocations.Clear();
        _caller.Invocations.Clear();
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(456, It.IsAny<CancellationToken>())).ReturnsAsync(Key(456));

        await hub.SubscribeToKeyManagement(456);

        VerifyAddedToGroup("vkey-status-456");
        VerifyAddedToGroup("admin");
        VerifyRemovedFromGroup($"vkey-lifecycle-owner-{DefaultVirtualKeyId}");
        _keys.Verify(service => service.GetVirtualKeyInfoForValidationAsync(456, It.IsAny<CancellationToken>()), Times.Once);
        _caller.Verify(client => client.SendCoreAsync("VirtualKeyStatus",
            It.Is<object[]>(args => args.Length == 1 && args[0] is VirtualKeyStatusNotification &&
                ((VirtualKeyStatusNotification)args[0]).KeyId == 456), It.IsAny<CancellationToken>()), Times.Once);
        _caller.Verify(client => client.SendCoreAsync("SubscribedToKeyManagement",
            It.Is<object[]>(args => args.Length == 1 && (int)args[0] == 456),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribeToKeyManagement_ReconcilesAConnectionPromotedAfterItConnected()
    {
        var hub = CreateHub();
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Key(DefaultVirtualKeyId));
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(456, It.IsAny<CancellationToken>())).ReturnsAsync(Key(456));
        await hub.OnConnectedAsync();
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId,
            It.IsAny<CancellationToken>())).ReturnsAsync(Key(DefaultVirtualKeyId, administrator: true));

        await hub.SubscribeToKeyManagement(456);

        VerifyAddedToGroup($"vkey-lifecycle-owner-{DefaultVirtualKeyId}");
        VerifyRemovedFromGroup($"vkey-lifecycle-owner-{DefaultVirtualKeyId}");
        VerifyAddedToGroup("admin");
        VerifyAddedToGroup($"vkey-status-{DefaultVirtualKeyId}");
        VerifyAddedToGroup("vkey-status-456");
        MockGroups.Verify(service => service.RemoveFromGroupAsync(DefaultConnectionId,
            $"vkey-status-{DefaultVirtualKeyId}", It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsubscribeFromKeyManagement_RetainsOwnKeyStatusMembership(bool ownKey)
    {
        var hub = CreateHub(administrator: true);
        var target = ownKey ? DefaultVirtualKeyId : 456;
        await hub.OnConnectedAsync();
        if (!ownKey)
        {
            _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(456,
                It.IsAny<CancellationToken>())).ReturnsAsync(Key(456));
            await hub.SubscribeToKeyManagement(target);
        }
        MockGroups.Invocations.Clear();
        _caller.Invocations.Clear();

        await hub.UnsubscribeFromKeyManagement(target);

        MockGroups.Verify(service => service.RemoveFromGroupAsync(DefaultConnectionId, $"vkey-status-{target}",
            It.IsAny<CancellationToken>()), ownKey ? Times.Never() : Times.Once());
        _caller.Verify(client => client.SendCoreAsync("UnsubscribedFromKeyManagement",
            It.Is<object[]>(args => args.Length == 1 && (int)args[0] == target),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCurrentKeyStatus_AlwaysLooksUpAuthenticatedOwner()
    {
        var hub = CreateHub();
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Key(DefaultVirtualKeyId));

        await hub.GetCurrentKeyStatus();

        _keys.Verify(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId, It.IsAny<CancellationToken>()), Times.Once);
        _keys.VerifyNoOtherCalls();
        _caller.Verify(client => client.SendCoreAsync("VirtualKeyStatus",
            It.Is<object[]>(args => args.Length == 1 && args[0] is VirtualKeyStatusNotification &&
                ((VirtualKeyStatusNotification)args[0]).KeyId == DefaultVirtualKeyId &&
                ((VirtualKeyStatusNotification)args[0]).CurrentSpend == 3m &&
                ((VirtualKeyStatusNotification)args[0]).MaxBudget == 25m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private VirtualKeyManagementHub CreateHub(bool administrator = false)
    {
        SetupAuthServiceReturnsVirtualKey(DefaultVirtualKeyId);
        MockClients.SetupGet(service => service.Caller).Returns(_caller.Object);
        _keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(DefaultVirtualKeyId,
            It.IsAny<CancellationToken>())).ReturnsAsync(Key(DefaultVirtualKeyId, administrator));
        var context = new Mock<IHubContext<VirtualKeyManagementHub>>();
        context.SetupGet(value => value.Groups).Returns(MockGroups.Object);
        var provider = new ServiceCollection()
            .AddSingleton(_keys.Object)
            .AddSingleton(context.Object)
            .AddSingleton<ILogger<VirtualKeyManagementAudienceService>>(
                NullLogger<VirtualKeyManagementAudienceService>.Instance)
            .AddSingleton<VirtualKeyManagementAudienceService>()
            .BuildServiceProvider();
        _providers.Add(provider);
        MockServiceProvider.Setup(value => value.GetService(typeof(IVirtualKeyManagementAudienceService)))
            .Returns(provider.GetRequiredService<VirtualKeyManagementAudienceService>());
        return new VirtualKeyManagementHub((SignalRMetrics)RealMetrics,
            NullLogger<VirtualKeyManagementHub>.Instance, MockServiceProvider.Object, _keys.Object)
        {
            Context = CreateHubCallerContext().Object,
            Groups = MockGroups.Object,
            Clients = MockClients.Object
        };
    }

    private static VirtualKey Key(int id, bool administrator = false) => new()
    {
        Id = id, KeyName = $"key-{id}", IsEnabled = true,
        Metadata = administrator ? "{\"isAdmin\":true}" : null,
        VirtualKeyGroup = new VirtualKeyGroup { Id = 1, Balance = 25m, LifetimeSpent = 3m }
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var provider in _providers) provider.Dispose();
        base.Dispose(disposing);
    }
}
