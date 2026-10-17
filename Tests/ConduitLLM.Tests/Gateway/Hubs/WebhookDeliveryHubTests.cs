using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

using Moq;

using Xunit.Abstractions;

namespace ConduitLLM.Tests.Gateway.Hubs;

public sealed class WebhookDeliveryHubTests : HubTestBase
{
    private readonly Mock<IWebhookConnectionTracker> _tracker = new();

    public WebhookDeliveryHubTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnConnectedAsync_JoinsStatisticsGroupOnlyForTrustedAdministrator(bool administrator)
    {
        SetupAuthServiceIsAdmin(administrator);
        var hub = CreateHub();

        await hub.OnConnectedAsync();

        VerifyAddedToGroup($"vkey-{DefaultVirtualKeyId}");
        MockGroups.Verify(groups => groups.AddToGroupAsync(DefaultConnectionId, "admin",
            It.IsAny<CancellationToken>()), administrator ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OnConnectedAsync_MissingOrNonpositiveOwnerCannotJoinStatisticsGroup(int? virtualKeyId)
    {
        SetupAuthServiceIsAdmin(true);
        var hub = CreateHub(virtualKeyId);

        await hub.OnConnectedAsync();

        MockGroups.Verify(groups => groups.AddToGroupAsync(It.IsAny<string>(), "admin",
            It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task UnsubscribeFromWebhooks_RemovesTrackerEntriesAndSignalRGroups()
    {
        var hub = CreateHub();
        var urls = new[] { "https://one.example/webhook", "https://two.example/webhook" };

        await hub.UnsubscribeFromWebhooks(urls);

        _tracker.Verify(service => service.RemoveWebhooksFromConnectionAsync(
            DefaultConnectionId,
            It.Is<IEnumerable<string>>(items => items.SequenceEqual(urls))), Times.Once);
        foreach (var url in urls)
        {
            VerifyRemovedFromGroup(SignalRConstants.Groups.Webhook(DefaultVirtualKeyId, url));
        }
    }

    [Theory]
    [InlineData(123)]
    [InlineData(456)]
    public async Task SubscribeToWebhooks_UsesOnlyAuthenticatedOwnerGroupsAndTracksUrls(int virtualKeyId)
    {
        var hub = CreateHub(virtualKeyId);
        var urls = new[] { "https://shared.example/webhook", "https://foreign.example/webhook" };

        await hub.SubscribeToWebhooks(urls);

        _tracker.Verify(service => service.AddWebhooksToConnectionAsync(DefaultConnectionId,
            It.Is<IEnumerable<string>>(items => items.SequenceEqual(urls))), Times.Once);
        foreach (var url in urls)
            VerifyAddedToGroup(SignalRConstants.Groups.Webhook(virtualKeyId, url));
        Assert.Equal(urls.Length, MockGroups.Invocations.Count);
        MockAuthService.Verify(service => service.IsAdminAsync(It.IsAny<HubCallerContext>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MissingOrNonpositiveOwner_CannotMutateSubscriptions(int? virtualKeyId)
    {
        var hub = CreateHub(virtualKeyId);
        var urls = new[] { "https://shared.example/webhook" };

        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToWebhooks(urls));
        await Assert.ThrowsAsync<HubException>(() => hub.UnsubscribeFromWebhooks(urls));

        _tracker.VerifyNoOtherCalls();
        Assert.Empty(MockGroups.Invocations);
    }

    private WebhookDeliveryHub CreateHub(int? virtualKeyId = DefaultVirtualKeyId)
    {
        MockAuthService.Setup(service => service.GetVirtualKeyId(It.IsAny<HubCallerContext>()))
            .Returns(virtualKeyId);
        return new WebhookDeliveryHub(
            Mock.Of<ILogger<WebhookDeliveryHub>>(), MockServiceProvider.Object, _tracker.Object)
        {
            Context = CreateHubCallerContext(virtualKeyId).Object,
            Groups = MockGroups.Object,
            Clients = MockClients.Object
        };
    }
}
