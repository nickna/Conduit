using System.Collections.Concurrent;
using System.Text.Json;

using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services;
using ConduitLLM.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

using Moq;

namespace ConduitLLM.IntegrationTests.Tests;

[Trait("Category", "Integration")]
[Trait("Component", "SignalR")]
public sealed class VirtualKeyManagementHubDispatchSecurityTests
{
    private static readonly DateTime Instant = new(2026, 10, 15, 1, 2, 3, DateTimeKind.Utc);
    private static readonly string[] LifecycleEvents =
        ["VirtualKeyCreated", "VirtualKeyUpdated", "VirtualKeyDeleted", "VirtualKeyStatusChanged"];

    [Theory]
    [InlineData("BroadcastKeyCreated")]
    [InlineData("BroadcastKeyUpdated")]
    [InlineData("BroadcastKeyDeleted")]
    [InlineData("BroadcastKeyStatusChanged")]
    public async Task OrdinaryKeyCannotInvokeAnyLifecycleBroadcasterOrForgeOwnerAndAdminNotifications(string method)
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        using var ownerA = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenA);
        using var ownerB = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenB);
        using var admin = await ConnectAsync(host, GatewaySignalRDispatchHost.AdminToken);

        var exception = await Assert.ThrowsAsync<HubException>(() => GatewaySignalRDispatchHost.InvokeAsync(
            ownerB.Connection, method, ForgedArguments(method)));
        Assert.Equal($"Failed to invoke '{method}' due to an error on the server. HubException: Method does not exist.", exception.Message);
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, admin.Connection);
        Assert.Empty(ownerA.Inbox.Messages);
        Assert.Empty(ownerB.Inbox.Messages);
        Assert.Empty(admin.Inbox.Messages);

        // The admin audience is established by the production IsAdminAsync connection path.
        await host.CreateKeyBroadcaster().NotifyKeyCreatedAsync(Created());
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, admin.Connection);
        AssertPayload(Assert.Single(admin.Inbox.Messages), "VirtualKeyCreated");
        Assert.Empty(ownerA.Inbox.Messages);
        Assert.Empty(ownerB.Inbox.Messages);
    }

    [Fact]
    public async Task ManagementSubscriptionRequiresAdminAndUnsubscribeRestoresItsPriorAudience()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        using var ownerA = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenA);
        using var ownerB = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenB);
        using var admin = await ConnectAsync(host, GatewaySignalRDispatchHost.AdminToken);
        await GatewaySignalRDispatchHost.InvokeAsync(ownerA.Connection, "SubscribeToKeyManagement", GatewaySignalRDispatchHost.OwnerB);
        await GatewaySignalRDispatchHost.InvokeAsync(ownerB.Connection, "SubscribeToKeyManagement", GatewaySignalRDispatchHost.OwnerA);
        await GatewaySignalRDispatchHost.InvokeAsync(admin.Connection, "SubscribeToKeyManagement", GatewaySignalRDispatchHost.OwnerA);
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, admin.Connection);
        var deniedA = Assert.Single(ownerA.Inbox.Messages);
        var deniedB = Assert.Single(ownerB.Inbox.Messages);
        Assert.Equal("Error", deniedA.Name);
        Assert.Equal("Error", deniedB.Name);
        Assert.Equal("Unauthorized: Admin privileges required", deniedA.Payload.GetProperty("message").GetString());
        Assert.Equal("Unauthorized: Admin privileges required", deniedB.Payload.GetProperty("message").GetString());
        var adminMessages = admin.Inbox.Messages;
        Assert.Equal(new[] { "VirtualKeyStatus", "SubscribedToKeyManagement" }, adminMessages.Select(message => message.Name));
        Assert.Equal(GatewaySignalRDispatchHost.OwnerA, adminMessages[0].Payload.GetProperty("keyId").GetInt32());
        Assert.Equal(GatewaySignalRDispatchHost.OwnerA, adminMessages[1].Payload.GetInt32());
        Clear(ownerA, ownerB, admin);
        var broadcaster = host.CreateKeyBroadcaster();

        // A noncritical status is delivered to the management group, not the general admin group.
        await broadcaster.NotifyKeyStatusChangedAsync(GatewaySignalRDispatchHost.OwnerA, Status("enabled"));
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, admin.Connection);
        AssertPayload(Assert.Single(ownerA.Inbox.Messages), "VirtualKeyStatusChanged", "enabled");
        AssertPayload(Assert.Single(admin.Inbox.Messages), "VirtualKeyStatusChanged", "enabled");
        Assert.Empty(ownerB.Inbox.Messages);

        await GatewaySignalRDispatchHost.InvokeAsync(admin.Connection, "UnsubscribeFromKeyManagement", GatewaySignalRDispatchHost.OwnerA);
        await host.FlushAsync<VirtualKeyManagementHub>(admin.Connection);
        Assert.Equal(GatewaySignalRDispatchHost.OwnerA,
            Assert.Single(admin.Inbox.Messages, message => message.Name == "UnsubscribedFromKeyManagement").Payload.GetInt32());
        Clear(ownerA, ownerB, admin);
        await broadcaster.NotifyKeyStatusChangedAsync(GatewaySignalRDispatchHost.OwnerA, Status("enabled"));
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, admin.Connection);
        AssertPayload(Assert.Single(ownerA.Inbox.Messages), "VirtualKeyStatusChanged", "enabled");
        Assert.Empty(ownerB.Inbox.Messages);
        Assert.Empty(admin.Inbox.Messages);
    }

    [Fact]
    public async Task CurrentKeyStatusUsesOnlyTheAuthenticatedKeyAndRejectsTargetKeyArguments()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        using var ownerA = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenA);
        using var ownerB = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenB);
        using var admin = await ConnectAsync(host, GatewaySignalRDispatchHost.AdminToken);
        host.RuntimeKeys.Invocations.Clear();

        foreach (var client in new[] { ownerA, ownerB, admin })
            await GatewaySignalRDispatchHost.InvokeAsync(client.Connection, "GetCurrentKeyStatus");
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, admin.Connection);

        foreach (var (client, keyId) in new[] { (ownerA, GatewaySignalRDispatchHost.OwnerA),
            (ownerB, GatewaySignalRDispatchHost.OwnerB), (admin, GatewaySignalRDispatchHost.AdminKey) })
        {
            var message = Assert.Single(client.Inbox.Messages);
            Assert.Equal("VirtualKeyStatus", message.Name);
            Assert.Equal(keyId, message.Payload.GetProperty("keyId").GetInt32());
            AssertFields(message.Payload, "allowedModels", "budgetPercentage", "createdAt", "currentSpend", "expiresAt",
                "isEnabled", "keyId", "keyName", "lastUsedAt", "maxBudget", "rateLimitPerMinute", "timestamp");
            host.RuntimeKeys.Verify(service => service.GetVirtualKeyInfoForValidationAsync(keyId, It.IsAny<CancellationToken>()), Times.Once);
        }
        await Assert.ThrowsAsync<HubException>(() => GatewaySignalRDispatchHost.InvokeAsync(
            ownerA.Connection, "GetCurrentKeyStatus", GatewaySignalRDispatchHost.OwnerB));
        host.RuntimeKeys.Verify(service => service.GetVirtualKeyInfoForValidationAsync(
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task LegitimateLifecyclePublicationPreservesOwnerAndGeneralAdminPayloadsWithoutForeignDelivery()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        using var ownerA = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenA);
        using var ownerB = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenB);
        using var admin = await ConnectAsync(host, GatewaySignalRDispatchHost.AdminToken);

        await PublishLifecycleAsync(host.CreateKeyBroadcaster());
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, admin.Connection);

        Assert.Equal(LifecycleEvents.Skip(1), ownerA.Inbox.Messages.Select(message => message.Name));
        Assert.Equal(LifecycleEvents, admin.Inbox.Messages.Select(message => message.Name));
        Assert.Empty(ownerB.Inbox.Messages);
        foreach (var message in ownerA.Inbox.Messages.Concat(admin.Inbox.Messages)) AssertPayload(message, message.Name);
    }

    [Fact]
    public async Task AuthorizedManagementSubscriberReceivesEveryTargetedEventAndGeneralAdminGetsOnlyItsOwnAudience()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        using var ownerA = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenA);
        using var ownerB = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenB);
        using var generalAdmin = await ConnectAsync(host, GatewaySignalRDispatchHost.AdminToken);
        using var manager = await ConnectAsync(host, GatewaySignalRDispatchHost.AdminToken);
        await GatewaySignalRDispatchHost.InvokeAsync(manager.Connection, "SubscribeToKeyManagement", GatewaySignalRDispatchHost.OwnerA);
        await host.FlushAsync<VirtualKeyManagementHub>(manager.Connection);
        Clear(manager);

        var broadcaster = host.CreateKeyBroadcaster();
        await PublishLifecycleAsync(broadcaster);
        await broadcaster.NotifyKeyStatusChangedAsync(GatewaySignalRDispatchHost.OwnerA, Status("enabled"));
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, generalAdmin.Connection, manager.Connection);

        Assert.Equal(LifecycleEvents, generalAdmin.Inbox.Messages.Select(message => message.Name));
        Assert.Equal(new[] { "VirtualKeyUpdated", "VirtualKeyDeleted", "VirtualKeyStatusChanged", "VirtualKeyStatusChanged" },
            ownerA.Inbox.Messages.Select(message => message.Name));
        // Existing publication sends once per audience. A manager also belongs to admin,
        // so updates/deletions/critical status arrive through both authorized groups.
        Assert.Equal(new[] { "VirtualKeyCreated", "VirtualKeyUpdated", "VirtualKeyUpdated", "VirtualKeyDeleted",
            "VirtualKeyDeleted", "VirtualKeyStatusChanged", "VirtualKeyStatusChanged", "VirtualKeyStatusChanged" },
            manager.Inbox.Messages.Select(message => message.Name));
        Assert.Equal(new[] { "disabled", "disabled", "enabled" }, manager.Inbox.Messages
            .Where(message => message.Name == "VirtualKeyStatusChanged").Select(message => message.Payload.GetProperty("newStatus").GetString()));
        Assert.Empty(ownerB.Inbox.Messages);
        foreach (var message in manager.Inbox.Messages)
            AssertPayload(message, message.Name, message.Name == "VirtualKeyStatusChanged"
                ? message.Payload.GetProperty("newStatus").GetString()! : "disabled");
    }

    private static async Task<Client> ConnectAsync(GatewaySignalRDispatchHost host, string token)
    {
        var connection = await host.ConnectAsync(token, GatewaySignalRDispatchHost.KeyManagementPath);
        var inbox = new EventInbox(connection);
        // Complete a real method dispatch before draining connection-time status frames.
        await GatewaySignalRDispatchHost.InvokeAsync(connection, "GetCurrentKeyStatus");
        await host.FlushAsync<VirtualKeyManagementHub>(connection);
        inbox.Clear();
        return new(connection, inbox);
    }

    private static void Clear(params Client[] clients)
    {
        foreach (var client in clients) client.Inbox.Clear();
    }

    private static async Task PublishLifecycleAsync(VirtualKeyManagementNotificationService broadcaster)
    {
        await broadcaster.NotifyKeyCreatedAsync(Created());
        await broadcaster.NotifyKeyUpdatedAsync(GatewaySignalRDispatchHost.OwnerA, Updated());
        await broadcaster.NotifyKeyDeletedAsync(GatewaySignalRDispatchHost.OwnerA, Deleted());
        await broadcaster.NotifyKeyStatusChangedAsync(GatewaySignalRDispatchHost.OwnerA, Status("disabled"));
    }

    private static object[] ForgedArguments(string method) => method switch
    {
        "BroadcastKeyCreated" => [Created()],
        "BroadcastKeyUpdated" => [GatewaySignalRDispatchHost.OwnerA, Updated()],
        "BroadcastKeyDeleted" => [GatewaySignalRDispatchHost.OwnerA, Deleted()],
        "BroadcastKeyStatusChanged" => [GatewaySignalRDispatchHost.OwnerA, Status("disabled")],
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private static VirtualKeyCreatedNotification Created() => new()
    {
        KeyId = GatewaySignalRDispatchHost.OwnerA, KeyName = "Owner A fixture", IsEnabled = true,
        MaxBudget = 123.12345678m, AllowedModels = "model-a,model-b", RateLimitPerMinute = 12,
        CreatedAt = Instant, ExpiresAt = null, Timestamp = Instant
    };

    private static VirtualKeyUpdatedNotification Updated() => new()
    {
        KeyId = GatewaySignalRDispatchHost.OwnerA, KeyName = "Owner A fixture", UpdatedProperties = ["AllowedModels", "MaxBudget"],
        IsEnabled = false, MaxBudget = 0.12345678m, AllowedModels = "model-b", RateLimitPerMinute = 0,
        ExpiresAt = null, UpdatedAt = Instant, Timestamp = Instant
    };

    private static VirtualKeyDeletedNotification Deleted() => new()
    {
        KeyId = GatewaySignalRDispatchHost.OwnerA, KeyName = "Owner A fixture", DeletedAt = Instant,
        DeletionReason = "Retained reason 雪", Timestamp = Instant
    };

    private static VirtualKeyStatusChangedNotification Status(string status) => new()
    {
        KeyId = GatewaySignalRDispatchHost.OwnerA, KeyName = "Owner A fixture", PreviousStatus = "pending",
        NewStatus = status, StatusChangeReason = "Retained reason 雪", ChangedAt = Instant, Timestamp = Instant
    };

    private static void AssertPayload(CapturedEvent message, string name, string status = "disabled")
    {
        Assert.Equal(name, message.Name);
        var payload = message.Payload;
        Assert.Equal(GatewaySignalRDispatchHost.OwnerA, payload.GetProperty("keyId").GetInt32());
        Assert.Equal("Owner A fixture", payload.GetProperty("keyName").GetString());
        Assert.Equal(Instant, payload.GetProperty("timestamp").GetDateTime());
        switch (name)
        {
            case "VirtualKeyCreated":
                AssertFields(payload, "allowedModels", "createdAt", "expiresAt", "isEnabled", "keyId", "keyName", "maxBudget", "rateLimitPerMinute", "timestamp");
                Assert.True(payload.GetProperty("isEnabled").GetBoolean());
                Assert.Equal(123.12345678m, payload.GetProperty("maxBudget").GetDecimal());
                Assert.Equal("model-a,model-b", payload.GetProperty("allowedModels").GetString());
                Assert.Equal(12, payload.GetProperty("rateLimitPerMinute").GetInt32());
                Assert.Equal(Instant, payload.GetProperty("createdAt").GetDateTime());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("expiresAt").ValueKind);
                break;
            case "VirtualKeyUpdated":
                AssertFields(payload, "allowedModels", "expiresAt", "isEnabled", "keyId", "keyName", "maxBudget", "rateLimitPerMinute", "timestamp", "updatedAt", "updatedProperties");
                Assert.False(payload.GetProperty("isEnabled").GetBoolean());
                Assert.Equal(0.12345678m, payload.GetProperty("maxBudget").GetDecimal());
                Assert.Equal("model-b", payload.GetProperty("allowedModels").GetString());
                Assert.Equal(0, payload.GetProperty("rateLimitPerMinute").GetInt32());
                Assert.Equal(new[] { "AllowedModels", "MaxBudget" }, payload.GetProperty("updatedProperties").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal(Instant, payload.GetProperty("updatedAt").GetDateTime());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("expiresAt").ValueKind);
                break;
            case "VirtualKeyDeleted":
                AssertFields(payload, "deletedAt", "deletionReason", "keyId", "keyName", "timestamp");
                Assert.Equal("Retained reason 雪", payload.GetProperty("deletionReason").GetString());
                Assert.Equal(Instant, payload.GetProperty("deletedAt").GetDateTime());
                break;
            case "VirtualKeyStatusChanged":
                AssertFields(payload, "changedAt", "keyId", "keyName", "newStatus", "previousStatus", "statusChangeReason", "timestamp");
                Assert.Equal(status, payload.GetProperty("newStatus").GetString());
                Assert.Equal("pending", payload.GetProperty("previousStatus").GetString());
                Assert.Equal("Retained reason 雪", payload.GetProperty("statusChangeReason").GetString());
                Assert.Equal(Instant, payload.GetProperty("changedAt").GetDateTime());
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private static void AssertFields(JsonElement payload, params string[] names) =>
        Assert.Equal(names, payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private sealed record CapturedEvent(string Name, JsonElement Payload);
    private sealed record Client(HubConnection Connection, EventInbox Inbox) : IDisposable
    {
        public void Dispose() => Inbox.Dispose();
    }

    private sealed class EventInbox : IDisposable
    {
        private readonly ConcurrentQueue<CapturedEvent> _messages = new();
        private readonly List<IDisposable> _handlers = [];
        public IReadOnlyList<CapturedEvent> Messages => _messages.ToArray();
        public void Clear() => _messages.Clear();

        public EventInbox(HubConnection connection)
        {
            foreach (var name in LifecycleEvents.Concat(new[] { "VirtualKeyStatus", "Error", "SubscribedToKeyManagement", "UnsubscribedFromKeyManagement" }))
                _handlers.Add(connection.On<JsonElement>(name, payload => _messages.Enqueue(new(name, payload.Clone()))));
        }

        public void Dispose()
        {
            foreach (var handler in _handlers) handler.Dispose();
        }
    }
}
