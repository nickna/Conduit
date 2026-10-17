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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingManagementOwnerAndAdminAudiencesReceiveOneLogicalEvent(bool administratorsOwnKey)
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        using var ownerA = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenA);
        using var ownerB = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenB);
        using var generalAdmin = await ConnectAsync(host, GatewaySignalRDispatchHost.AdminToken);
        using var manager = await ConnectAsync(host, GatewaySignalRDispatchHost.AdminToken);
        var keyId = administratorsOwnKey ? GatewaySignalRDispatchHost.AdminKey : GatewaySignalRDispatchHost.OwnerA;
        await GatewaySignalRDispatchHost.InvokeAsync(manager.Connection, "SubscribeToKeyManagement", keyId);
        await host.FlushAsync<VirtualKeyManagementHub>(manager.Connection);
        Assert.Equal(new[] { "VirtualKeyStatus", "SubscribedToKeyManagement" },
            manager.Inbox.Messages.Select(message => message.Name));
        Assert.Equal(keyId, manager.Inbox.Messages[0].Payload.GetProperty("keyId").GetInt32());
        Assert.Equal(keyId, manager.Inbox.Messages[1].Payload.GetInt32());
        Clear(manager);

        var broadcaster = host.CreateKeyBroadcaster();
        await PublishLifecycleAsync(broadcaster, keyId);
        await broadcaster.NotifyKeyStatusChangedAsync(keyId, Status("enabled", keyId));
        await broadcaster.NotifyKeyStatusChangedAsync(keyId, Status("expired", keyId));
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, generalAdmin.Connection, manager.Connection);

        // Owner, management and general administration may overlap. Membership in
        // more than one authorized audience must still produce one logical event.
        var allNames = LifecycleEvents.Concat(new[] { "VirtualKeyStatusChanged", "VirtualKeyStatusChanged" }).ToArray();
        AssertMessages(manager, allNames);
        AssertStatuses(manager, "disabled", "enabled", "expired");
        AssertMessages(generalAdmin, administratorsOwnKey ? allNames : LifecycleEvents);
        AssertStatuses(generalAdmin, administratorsOwnKey ? ["disabled", "enabled", "expired"] : ["disabled"]);
        AssertMessages(ownerA, administratorsOwnKey ? [] : allNames.Skip(1).ToArray());
        AssertStatuses(ownerA, administratorsOwnKey ? [] : ["disabled", "enabled", "expired"]);
        Assert.Empty(ownerB.Inbox.Messages);

        await GatewaySignalRDispatchHost.InvokeAsync(manager.Connection, "UnsubscribeFromKeyManagement", keyId);
        await host.FlushAsync<VirtualKeyManagementHub>(manager.Connection);
        Assert.Equal(keyId, Assert.Single(manager.Inbox.Messages,
            message => message.Name == "UnsubscribedFromKeyManagement").Payload.GetInt32());
        Clear(ownerA, ownerB, generalAdmin, manager);

        await broadcaster.NotifyKeyStatusChangedAsync(keyId, Status("enabled", keyId));
        await broadcaster.NotifyKeyStatusChangedAsync(keyId, Status("expired", keyId));
        await broadcaster.NotifyKeyStatusChangedAsync(keyId, Status("suspended", keyId));
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, generalAdmin.Connection, manager.Connection);
        // Unsubscribing removes management visibility, while authenticated ownership
        // and the critical-status administrator audience remain effective.
        var threeStatuses = new[] { "VirtualKeyStatusChanged", "VirtualKeyStatusChanged", "VirtualKeyStatusChanged" };
        AssertMessages(manager, administratorsOwnKey ? threeStatuses : ["VirtualKeyStatusChanged"]);
        AssertStatuses(manager, administratorsOwnKey ? ["enabled", "expired", "suspended"] : ["suspended"]);
        AssertMessages(generalAdmin, administratorsOwnKey ? threeStatuses : ["VirtualKeyStatusChanged"]);
        AssertStatuses(generalAdmin, administratorsOwnKey ? ["enabled", "expired", "suspended"] : ["suspended"]);
        AssertMessages(ownerA, administratorsOwnKey ? [] : threeStatuses);
        AssertStatuses(ownerA, administratorsOwnKey ? [] : ["enabled", "expired", "suspended"]);
        Assert.Empty(ownerB.Inbox.Messages);

        // Keep its handlers installed while closing the actual transport. Subsequent
        // publications must reach the remaining audiences without adding stale events.
        await manager.Connection.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HubConnectionState.Disconnected, manager.Connection.State);
        Clear(ownerA, ownerB, generalAdmin, manager);
        await broadcaster.NotifyKeyUpdatedAsync(keyId, Updated(keyId));
        await broadcaster.NotifyKeyDeletedAsync(keyId, Deleted(keyId));
        await broadcaster.NotifyKeyStatusChangedAsync(keyId, Status("enabled", keyId));
        await broadcaster.NotifyKeyStatusChangedAsync(keyId, Status("disabled", keyId));
        await host.FlushAsync<VirtualKeyManagementHub>(ownerA.Connection, ownerB.Connection, generalAdmin.Connection);
        var remaining = new[] { "VirtualKeyUpdated", "VirtualKeyDeleted", "VirtualKeyStatusChanged", "VirtualKeyStatusChanged" };
        AssertMessages(generalAdmin, administratorsOwnKey ? remaining : ["VirtualKeyUpdated", "VirtualKeyDeleted", "VirtualKeyStatusChanged"]);
        AssertStatuses(generalAdmin, administratorsOwnKey ? ["enabled", "disabled"] : ["disabled"]);
        AssertMessages(ownerA, administratorsOwnKey ? [] : remaining);
        AssertStatuses(ownerA, administratorsOwnKey ? [] : ["enabled", "disabled"]);
        Assert.Empty(ownerB.Inbox.Messages);
        Assert.Empty(manager.Inbox.Messages);

        void AssertMessages(Client client, params string[] expected)
        {
            Assert.Equal(expected, client.Inbox.Messages.Select(message => message.Name));
            foreach (var message in client.Inbox.Messages)
                AssertPayload(message, message.Name, message.Name == "VirtualKeyStatusChanged"
                    ? message.Payload.GetProperty("newStatus").GetString()! : "disabled", keyId);
        }

        static void AssertStatuses(Client client, params string[] expected) =>
            Assert.Equal(expected, client.Inbox.Messages.Where(message => message.Name == "VirtualKeyStatusChanged")
                .Select(message => message.Payload.GetProperty("newStatus").GetString()));
    }

    [Fact]
    public async Task AdministratorPromotionAtSubscriptionPreservesForeignManagementAndDeduplicatesOwnLifecycle()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        using var promoted = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenA);
        using var ownerB = await ConnectAsync(host, GatewaySignalRDispatchHost.TokenB);
        using var generalAdmin = await ConnectAsync(host, GatewaySignalRDispatchHost.AdminToken);

        // Change the trusted persisted-key boundary after connection, rather than
        // forging a role on the client. Subscribe performs a fresh admin check.
        var key = await host.RuntimeKeys.Object.GetVirtualKeyInfoForValidationAsync(GatewaySignalRDispatchHost.OwnerA);
        Assert.NotNull(key);
        key.Metadata = """{"isAdmin":true}""";
        await GatewaySignalRDispatchHost.InvokeAsync(promoted.Connection, "SubscribeToKeyManagement", GatewaySignalRDispatchHost.OwnerB);
        await host.FlushAsync<VirtualKeyManagementHub>(promoted.Connection);
        Assert.Equal(new[] { "VirtualKeyStatus", "SubscribedToKeyManagement" },
            promoted.Inbox.Messages.Select(message => message.Name));
        Assert.Equal(GatewaySignalRDispatchHost.OwnerB, promoted.Inbox.Messages[1].Payload.GetInt32());
        Clear(promoted, ownerB, generalAdmin);

        var broadcaster = host.CreateKeyBroadcaster();
        await PublishLifecycleAsync(broadcaster, GatewaySignalRDispatchHost.OwnerB);
        await broadcaster.NotifyKeyStatusChangedAsync(GatewaySignalRDispatchHost.OwnerB,
            Status("enabled", GatewaySignalRDispatchHost.OwnerB));
        await host.FlushAsync<VirtualKeyManagementHub>(promoted.Connection, ownerB.Connection, generalAdmin.Connection);
        var withNoncriticalStatus = LifecycleEvents.Concat(new[] { "VirtualKeyStatusChanged" }).ToArray();
        AssertMessages(promoted, GatewaySignalRDispatchHost.OwnerB, withNoncriticalStatus);
        AssertMessages(ownerB, GatewaySignalRDispatchHost.OwnerB, withNoncriticalStatus.Skip(1).ToArray());
        AssertMessages(generalAdmin, GatewaySignalRDispatchHost.OwnerB, LifecycleEvents);
        Assert.Equal(new[] { "disabled", "enabled" }, promoted.Inbox.Messages
            .Where(message => message.Name == "VirtualKeyStatusChanged")
            .Select(message => message.Payload.GetProperty("newStatus").GetString()));

        Clear(promoted, ownerB, generalAdmin);
        await PublishLifecycleAsync(broadcaster);
        await broadcaster.NotifyKeyStatusChangedAsync(GatewaySignalRDispatchHost.OwnerA, Status("enabled"));
        await host.FlushAsync<VirtualKeyManagementHub>(promoted.Connection, ownerB.Connection, generalAdmin.Connection);
        // Role normalization must remove the old nonadmin-owner lifecycle route,
        // while retaining the connection's own noncritical status subscription.
        AssertMessages(promoted, GatewaySignalRDispatchHost.OwnerA, withNoncriticalStatus);
        AssertMessages(generalAdmin, GatewaySignalRDispatchHost.OwnerA, LifecycleEvents);
        Assert.Empty(ownerB.Inbox.Messages);
        Assert.Equal(new[] { "disabled", "enabled" }, promoted.Inbox.Messages
            .Where(message => message.Name == "VirtualKeyStatusChanged")
            .Select(message => message.Payload.GetProperty("newStatus").GetString()));

        static void AssertMessages(Client client, int keyId, params string[] expected)
        {
            Assert.Equal(expected, client.Inbox.Messages.Select(message => message.Name));
            foreach (var message in client.Inbox.Messages)
                AssertPayload(message, message.Name, message.Name == "VirtualKeyStatusChanged"
                    ? message.Payload.GetProperty("newStatus").GetString()! : "disabled", keyId);
        }
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

    private static async Task PublishLifecycleAsync(VirtualKeyManagementNotificationService broadcaster,
        int keyId = GatewaySignalRDispatchHost.OwnerA)
    {
        await broadcaster.NotifyKeyCreatedAsync(Created(keyId));
        await broadcaster.NotifyKeyUpdatedAsync(keyId, Updated(keyId));
        await broadcaster.NotifyKeyDeletedAsync(keyId, Deleted(keyId));
        await broadcaster.NotifyKeyStatusChangedAsync(keyId, Status("disabled", keyId));
    }

    private static object[] ForgedArguments(string method) => method switch
    {
        "BroadcastKeyCreated" => [Created()],
        "BroadcastKeyUpdated" => [GatewaySignalRDispatchHost.OwnerA, Updated()],
        "BroadcastKeyDeleted" => [GatewaySignalRDispatchHost.OwnerA, Deleted()],
        "BroadcastKeyStatusChanged" => [GatewaySignalRDispatchHost.OwnerA, Status("disabled")],
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private static VirtualKeyCreatedNotification Created(int keyId = GatewaySignalRDispatchHost.OwnerA) => new()
    {
        KeyId = keyId, KeyName = KeyName(keyId), IsEnabled = true,
        MaxBudget = 123.12345678m, AllowedModels = "model-a,model-b", RateLimitPerMinute = 12,
        CreatedAt = Instant, ExpiresAt = null, Timestamp = Instant
    };

    private static VirtualKeyUpdatedNotification Updated(int keyId = GatewaySignalRDispatchHost.OwnerA) => new()
    {
        KeyId = keyId, KeyName = KeyName(keyId), UpdatedProperties = ["AllowedModels", "MaxBudget"],
        IsEnabled = false, MaxBudget = 0.12345678m, AllowedModels = "model-b", RateLimitPerMinute = 0,
        ExpiresAt = null, UpdatedAt = Instant, Timestamp = Instant
    };

    private static VirtualKeyDeletedNotification Deleted(int keyId = GatewaySignalRDispatchHost.OwnerA) => new()
    {
        KeyId = keyId, KeyName = KeyName(keyId), DeletedAt = Instant,
        DeletionReason = "Retained reason 雪", Timestamp = Instant
    };

    private static VirtualKeyStatusChangedNotification Status(string status,
        int keyId = GatewaySignalRDispatchHost.OwnerA) => new()
    {
        KeyId = keyId, KeyName = KeyName(keyId), PreviousStatus = "pending",
        NewStatus = status, StatusChangeReason = "Retained reason 雪", ChangedAt = Instant, Timestamp = Instant
    };

    private static string KeyName(int keyId) => keyId switch
    {
        GatewaySignalRDispatchHost.OwnerA => "Owner A fixture",
        GatewaySignalRDispatchHost.OwnerB => "Owner B fixture",
        _ => "Administrator fixture"
    };

    private static void AssertPayload(CapturedEvent message, string name, string status = "disabled",
        int keyId = GatewaySignalRDispatchHost.OwnerA)
    {
        Assert.Equal(name, message.Name);
        var payload = message.Payload;
        Assert.Equal(keyId, payload.GetProperty("keyId").GetInt32());
        Assert.Equal(KeyName(keyId), payload.GetProperty("keyName").GetString());
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
