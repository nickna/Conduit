using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Gateway.Services;

public sealed class VirtualKeyManagementNotificationServiceTests
{
    [Theory]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("deleted")]
    [InlineData("enabled")]
    [InlineData("disabled")]
    [InlineData("suspended")]
    [InlineData("Disabled")]
    [InlineData("Suspended")]
    [InlineData("expired")]
    public async Task ServerPublication_PreservesLifecyclePayloadAndIntendedRecipientGroups(string notificationType)
    {
        var delivered = new List<(string Group, string Event, object Payload)>();
        var proxies = new List<Mock<IClientProxy>>();
        var clients = new Mock<IHubClients>(MockBehavior.Strict);
        clients.Setup(service => service.Group(It.IsAny<string>())).Returns((string group) =>
        {
            var proxy = new Mock<IClientProxy>();
            proxies.Add(proxy);
            proxy.Setup(client => client.SendCoreAsync(
                    It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object[], CancellationToken>((name, args, _) =>
                    delivered.Add((group, name, Assert.Single(args))))
                .Returns(Task.CompletedTask);
            return proxy.Object;
        });
        var context = new Mock<IHubContext<VirtualKeyManagementHub>>();
        context.SetupGet(service => service.Clients).Returns(clients.Object);
        var notifications = new VirtualKeyManagementNotificationService(
            context.Object, NullLogger<VirtualKeyManagementNotificationService>.Instance);
        object payload;
        string eventName;
        string[] groups;
        switch (notificationType)
        {
            case "created":
                var created = new VirtualKeyCreatedNotification { KeyId = 42, KeyName = "server-created" };
                payload = created;
                eventName = "VirtualKeyCreated";
                groups = ["admin"];
                await notifications.NotifyKeyCreatedAsync(created);
                break;
            case "updated":
                var updated = new VirtualKeyUpdatedNotification { KeyId = 42, UpdatedProperties = ["keyName"] };
                payload = updated;
                eventName = "VirtualKeyUpdated";
                groups = ["vkey-lifecycle-owner-42", "admin"];
                await notifications.NotifyKeyUpdatedAsync(42, updated);
                break;
            case "deleted":
                var deleted = new VirtualKeyDeletedNotification { KeyId = 42, KeyName = "server-deleted" };
                payload = deleted;
                eventName = "VirtualKeyDeleted";
                groups = ["vkey-lifecycle-owner-42", "admin"];
                await notifications.NotifyKeyDeletedAsync(42, deleted);
                break;
            default:
                var status = new VirtualKeyStatusChangedNotification
                {
                    KeyId = 42, PreviousStatus = "enabled", NewStatus = notificationType
                };
                payload = status;
                eventName = "VirtualKeyStatusChanged";
                groups = notificationType is "disabled" or "suspended"
                    ? ["vkey-lifecycle-owner-42", "admin"] : ["vkey-status-42"];
                await notifications.NotifyKeyStatusChangedAsync(42, status);
                break;
        }

        Assert.Equal(groups, delivered.Select(item => item.Group));
        Assert.All(delivered, item =>
        {
            Assert.Equal(eventName, item.Event);
            Assert.Same(payload, item.Payload);
        });
        clients.VerifyGet(service => service.All, Times.Never);
        clients.Verify(service => service.Groups(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        foreach (var group in groups)
            clients.Verify(service => service.Group(group), Times.Once);
        foreach (var proxy in proxies)
        {
            proxy.Verify(client => client.SendCoreAsync(eventName,
                It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], payload)),
                It.IsAny<CancellationToken>()), Times.Once);
            proxy.VerifyNoOtherCalls();
        }
        clients.VerifyNoOtherCalls();
    }
}
