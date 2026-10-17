using System.Collections.Concurrent;
using System.Text.Json;

using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services;
using ConduitLLM.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using Moq;

namespace ConduitLLM.IntegrationTests.Tests;

[Trait("Category", "Integration")]
[Trait("Component", "SignalR")]
public sealed class WebhookDeliveryHubDispatchSecurityTests
{
    private const string SharedUrl = "https://shared.example/callback?channel=shared";
    private const string OwnerAUrl = "https://owner-a.example/callback";
    private const string ForeignUrl = "https://owner-b.example/private-callback?secret=tenant-b";
    private static readonly string[] DeliveryEvents =
        ["DeliveryAttempted", "DeliverySucceeded", "DeliveryFailed", "RetryScheduled", "CircuitBreakerStateChanged"];

    [Fact]
    public async Task HostedInstallationStatisticsReachOnlyTheAuthenticatedAdministrator()
    {
        var deliveries = new[]
        {
            Delivery(GatewaySignalRDispatchHost.OwnerA, "a-pending", "Pending"),
            Delivery(GatewaySignalRDispatchHost.OwnerA, "a-success-1", "Delivered"),
            Delivery(GatewaySignalRDispatchHost.OwnerA, "a-success-2", "Delivered"),
            Delivery(GatewaySignalRDispatchHost.OwnerB, "b-pending-1", "Pending"),
            Delivery(GatewaySignalRDispatchHost.OwnerB, "b-pending-2", "Pending"),
            Delivery(GatewaySignalRDispatchHost.OwnerB, "b-pending-3", "Pending"),
            Delivery(GatewaySignalRDispatchHost.OwnerB, "b-pending-4", "Pending"),
            Delivery(GatewaySignalRDispatchHost.OwnerB, "b-success", "Delivered"),
            Delivery(GatewaySignalRDispatchHost.OwnerB, "b-failure-1", "Exhausted"),
            Delivery(GatewaySignalRDispatchHost.OwnerB, "b-failure-2", "Exhausted")
        };
        var recovery = new Mock<IWebhookRecovery>(MockBehavior.Strict);
        recovery.Setup(service => service.BacklogAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new WebhookBacklog(
                    deliveries.LongCount(row => row.State == "Pending"),
                    deliveries.LongCount(row => row.State == "Delivered"),
                    deliveries.LongCount(row => row.State == "Exhausted"), 0, 0));
            });
        Action? tick = null;
        var timer = new Mock<ITimer>();
        var clock = new Mock<TimeProvider> { CallBase = true };
        clock.Setup(provider => provider.CreateTimer(It.IsAny<TimerCallback>(), It.IsAny<object?>(),
                It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
            .Returns((TimerCallback callback, object? state, TimeSpan due, TimeSpan period) =>
            {
                Assert.Equal(TimeSpan.FromMinutes(1), due);
                Assert.Equal(TimeSpan.FromMinutes(1), period);
                tick = () => callback(state);
                return timer.Object;
            });
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync(services =>
        {
            services.AddSingleton(recovery.Object);
            services.AddSingleton(clock.Object);
            services.AddSingleton<WebhookDeliveryNotificationService>();
            services.AddSingleton<IWebhookDeliveryNotificationService>(provider =>
                provider.GetRequiredService<WebhookDeliveryNotificationService>());
            services.AddHostedService(provider => provider.GetRequiredService<WebhookDeliveryNotificationService>());
        });
        var ownerA = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA, GatewaySignalRDispatchHost.WebhookPath);
        var ownerB = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB, GatewaySignalRDispatchHost.WebhookPath);
        var unsubscribed = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA, GatewaySignalRDispatchHost.WebhookPath);
        var administrator = await host.ConnectAsync(GatewaySignalRDispatchHost.AdminToken, GatewaySignalRDispatchHost.WebhookPath);
        using var eventsA = new StatisticsInbox(ownerA);
        using var eventsB = new StatisticsInbox(ownerB);
        using var unsubscribedEvents = new StatisticsInbox(unsubscribed);
        using var adminEvents = new StatisticsInbox(administrator);
        await SubscribeAsync(ownerA, OwnerAUrl);
        await SubscribeAsync(ownerB, ForeignUrl);
        // Invocation round trips complete OnConnected and its trusted group join.
        // This placeholder acknowledgement does not add any URL subscription.
        await GatewaySignalRDispatchHost.InvokeAsync(unsubscribed, "RequestStatistics");
        await GatewaySignalRDispatchHost.InvokeAsync(administrator, "RequestStatistics");
        Assert.Empty(await host.WebhookTracker.GetConnectionWebhooksAsync(unsubscribed.ConnectionId!));
        Assert.Empty(await host.WebhookTracker.GetConnectionWebhooksAsync(administrator.ConnectionId!));

        var began = DateTime.UtcNow;
        Assert.NotNull(tick);
        tick(); // Wake the actual PeriodicTimer/hosted worker, not a broadcast method.
        var statistics = await adminEvents.First.WaitAsync(TimeSpan.FromSeconds(15));
        await host.Services.GetRequiredService<WebhookDeliveryNotificationService>()
            .StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        await host.FlushAsync<WebhookDeliveryHub>(ownerA, ownerB, unsubscribed, administrator);

        Assert.Empty(eventsA.Messages);
        Assert.Empty(eventsB.Messages);
        Assert.Empty(unsubscribedEvents.Messages);
        Assert.Single(adminEvents.Messages);
        AssertFields(statistics, "averageResponseTimeMs", "failedDeliveries", "pendingDeliveries", "period",
            "successRate", "successfulDeliveries", "timestamp", "totalDeliveries", "urlStatistics");
        Assert.Equal("retained", statistics.GetProperty("period").GetString());
        Assert.Equal(10, statistics.GetProperty("totalDeliveries").GetInt32());
        Assert.Equal(5, statistics.GetProperty("pendingDeliveries").GetInt32());
        Assert.Equal(3, statistics.GetProperty("successfulDeliveries").GetInt32());
        Assert.Equal(2, statistics.GetProperty("failedDeliveries").GetInt32());
        Assert.Equal(30, statistics.GetProperty("successRate").GetDouble());
        Assert.Equal(0, statistics.GetProperty("averageResponseTimeMs").GetDouble());
        Assert.Empty(statistics.GetProperty("urlStatistics").EnumerateArray());
        Assert.Equal(DateTimeKind.Utc, statistics.GetProperty("timestamp").GetDateTime().Kind);
        Assert.InRange(statistics.GetProperty("timestamp").GetDateTime(), began, DateTime.UtcNow);
        recovery.Verify(service => service.BacklogAsync(It.IsAny<CancellationToken>()), Times.Once);

        static WebhookDeliveryRecord Delivery(int owner, string id, string state) =>
            new() { Id = id, VirtualKeyId = owner, State = state };
    }

    [Fact]
    public async Task SharedDestinationKeepsEveryDeliveryNotificationWithinItsAuthenticatedOwner()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var ownerA = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA, GatewaySignalRDispatchHost.WebhookPath);
        var ownerB = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB, GatewaySignalRDispatchHost.WebhookPath);
        using var eventsA = new EventInbox(ownerA);
        using var eventsB = new EventInbox(ownerB);
        await SubscribeAsync(ownerA, SharedUrl);
        await SubscribeAsync(ownerB, SharedUrl);
        using var broadcaster = host.CreateWebhookBroadcaster();

        var retryA = await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerA, SharedUrl, "task-a");
        var retryB = await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerB, SharedUrl, "task-b");
        await host.FlushAsync<WebhookDeliveryHub>(ownerA, ownerB);

        AssertNotifications(eventsA.Messages, SharedUrl, "task-a", retryA);
        AssertNotifications(eventsB.Messages, SharedUrl, "task-b", retryB);
    }

    [Theory]
    [InlineData("http://shared.example/callback?channel=shared")]
    [InlineData("https://shared.example:8443/callback?channel=shared")]
    [InlineData("https://shared.example/callback?channel=other")]
    public async Task SchemePortAndQueryKeepDistinctDestinationsSeparateForTheSameOwner(string otherUrl)
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var original = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA, GatewaySignalRDispatchHost.WebhookPath);
        var other = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA, GatewaySignalRDispatchHost.WebhookPath);
        using var originalEvents = new EventInbox(original);
        using var otherEvents = new EventInbox(other);
        await SubscribeAsync(original, SharedUrl);
        await SubscribeAsync(other, otherUrl);
        using var broadcaster = host.CreateWebhookBroadcaster();

        var originalRetry = await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerA, SharedUrl, "original-destination");
        var otherRetry = await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerA, otherUrl, "other-destination");
        await host.FlushAsync<WebhookDeliveryHub>(original, other);

        AssertNotifications(originalEvents.Messages, SharedUrl, "original-destination", originalRetry);
        AssertNotifications(otherEvents.Messages, otherUrl, "other-destination", otherRetry);
    }

    [Fact]
    public async Task KnownForeignUrlAndExtraOwnerArgumentCannotSelectAnotherOwnersDeliveries()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var ownerA = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA, GatewaySignalRDispatchHost.WebhookPath);
        var ownerB = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB, GatewaySignalRDispatchHost.WebhookPath);
        using var eventsA = new EventInbox(ownerA);
        using var eventsB = new EventInbox(ownerB);
        // A URL selects a destination only within the authenticated caller's owner partition.
        await SubscribeAsync(ownerA, ForeignUrl);
        await SubscribeAsync(ownerB, ForeignUrl);
        await Assert.ThrowsAsync<HubException>(() => GatewaySignalRDispatchHost.InvokeAsync(
            ownerA, "SubscribeToWebhooks", new[] { ForeignUrl }, GatewaySignalRDispatchHost.OwnerB));
        using var broadcaster = host.CreateWebhookBroadcaster();

        var retry = await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerB, ForeignUrl, "task-b-private");
        await host.FlushAsync<WebhookDeliveryHub>(ownerA, ownerB);

        Assert.Empty(eventsA.Messages);
        AssertNotifications(eventsB.Messages, ForeignUrl, "task-b-private", retry);
    }

    [Fact]
    public async Task CircuitDetailsAreNotSentToAuthenticatedConnectionsWithoutTheOwnerSubscription()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var owner = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA, GatewaySignalRDispatchHost.WebhookPath);
        var observer = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB, GatewaySignalRDispatchHost.WebhookPath);
        using var ownerEvents = new EventInbox(owner);
        using var observerEvents = new EventInbox(observer);
        await SubscribeAsync(owner, ForeignUrl);
        using var broadcaster = host.CreateWebhookBroadcaster();

        await broadcaster.NotifyCircuitBreakerStateChangeAsync(GatewaySignalRDispatchHost.OwnerA,
            ForeignUrl, "Open", "Closed", "Owner A private destination failure", 3);
        await host.FlushAsync<WebhookDeliveryHub>(owner, observer);

        var notification = Assert.Single(ownerEvents.Messages);
        Assert.Equal("CircuitBreakerStateChanged", notification.Name);
        Assert.Equal(ForeignUrl, notification.Payload.GetProperty("url").GetString());
        Assert.Empty(observerEvents.Messages);
    }

    [Theory]
    [InlineData("BroadcastDeliveryAttempt")]
    [InlineData("BroadcastDeliverySuccess")]
    [InlineData("BroadcastDeliveryFailure")]
    [InlineData("BroadcastRetryScheduled")]
    public async Task ClientsCannotDiscoverServerBroadcastersEvenWithValidForgedDtoArguments(string method)
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var ownerA = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA, GatewaySignalRDispatchHost.WebhookPath);
        var ownerB = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB, GatewaySignalRDispatchHost.WebhookPath);
        using var eventsA = new EventInbox(ownerA);
        using var eventsB = new EventInbox(ownerB);
        await SubscribeAsync(ownerA, SharedUrl);
        await SubscribeAsync(ownerB, SharedUrl);

        var exception = await Assert.ThrowsAsync<HubException>(() => GatewaySignalRDispatchHost.InvokeAsync(
            ownerB, method, SharedUrl, ForgedPayload(method)));
        Assert.Equal($"Failed to invoke '{method}' due to an error on the server. HubException: Method does not exist.", exception.Message);
        await host.FlushAsync<WebhookDeliveryHub>(ownerA, ownerB);
        Assert.Empty(eventsA.Messages);
        Assert.Empty(eventsB.Messages);

        using var broadcaster = host.CreateWebhookBroadcaster();
        var retry = await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerA, SharedUrl, "trusted-task-a");
        await host.FlushAsync<WebhookDeliveryHub>(ownerA, ownerB);
        AssertNotifications(eventsA.Messages, SharedUrl, "trusted-task-a", retry);
        Assert.Empty(eventsB.Messages);
    }

    [Fact]
    public async Task UnsubscribeAndDisconnectCleanTrackingWithoutRemovingAnotherOwnersSubscriptions()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var ownerA = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA, GatewaySignalRDispatchHost.WebhookPath);
        var ownerB = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB, GatewaySignalRDispatchHost.WebhookPath);
        var ownerBConnectionId = ownerB.ConnectionId!;
        using var eventsA = new EventInbox(ownerA);
        using var eventsB = new EventInbox(ownerB);
        await SubscribeAsync(ownerA, SharedUrl, OwnerAUrl);
        await SubscribeAsync(ownerB, SharedUrl, ForeignUrl);
        Assert.Equal(2, await host.WebhookTracker.GetWebhookConnectionCountAsync(SharedUrl));
        Assert.Equal(new[] { OwnerAUrl, SharedUrl }.Order(StringComparer.Ordinal),
            (await host.WebhookTracker.GetConnectionWebhooksAsync(ownerA.ConnectionId!)).Order(StringComparer.Ordinal));

        await GatewaySignalRDispatchHost.InvokeAsync(ownerA, "UnsubscribeFromWebhooks",
            new object[] { new[] { SharedUrl } });
        Assert.Equal(1, await host.WebhookTracker.GetWebhookConnectionCountAsync(SharedUrl));
        Assert.Equal(OwnerAUrl, Assert.Single(await host.WebhookTracker.GetConnectionWebhooksAsync(ownerA.ConnectionId!)));
        Assert.Contains(ownerBConnectionId, await host.WebhookTracker.GetWebhookConnectionsAsync(SharedUrl));
        using var broadcaster = host.CreateWebhookBroadcaster();
        await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerA, SharedUrl, "unsubscribed-task");
        var retryA = await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerA, OwnerAUrl, "retained-task-a");
        var retryB = await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerB, SharedUrl, "retained-task-b");
        await host.FlushAsync<WebhookDeliveryHub>(ownerA, ownerB);
        AssertNotifications(eventsA.Messages, OwnerAUrl, "retained-task-a", retryA);
        AssertNotifications(eventsB.Messages, SharedUrl, "retained-task-b", retryB);

        await ownerB.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while ((await host.WebhookTracker.GetConnectionWebhooksAsync(ownerBConnectionId)).Count != 0)
                await Task.Delay(10, deadline.Token);
        }
        Assert.Equal(0, await host.WebhookTracker.GetWebhookConnectionCountAsync(SharedUrl));
        Assert.Equal(0, await host.WebhookTracker.GetWebhookConnectionCountAsync(ForeignUrl));
        Assert.Equal(1, await host.WebhookTracker.GetWebhookConnectionCountAsync(OwnerAUrl));
        await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.OwnerB, ForeignUrl, "disconnected-task-b");
        await host.FlushAsync<WebhookDeliveryHub>(ownerA);
        AssertNotifications(eventsA.Messages, OwnerAUrl, "retained-task-a", retryA);
    }

    // The hub takes one array argument, so wrap it in the invocation argument list.
    private static Task SubscribeAsync(HubConnection connection, params string[] urls) =>
        GatewaySignalRDispatchHost.InvokeAsync(connection, "SubscribeToWebhooks", new object[] { urls });

    private static async Task<DateTime> SendNotificationsAsync(WebhookDeliveryNotificationService broadcaster,
        int owner, string url, string taskId)
    {
        var retryAt = DateTime.UtcNow.AddMinutes(5);
        await broadcaster.NotifyDeliveryAttemptAsync(owner, url, taskId, "video", "task.completed", 2);
        await broadcaster.NotifyDeliverySuccessAsync(owner, url, taskId, 202, 37, 2);
        await broadcaster.NotifyDeliveryFailureAsync(owner, url, taskId, $"Failure for {taskId}", 503, 2, true);
        await broadcaster.NotifyRetryScheduledAsync(owner, url, taskId, retryAt, 2, 5);
        await broadcaster.NotifyCircuitBreakerStateChangeAsync(owner, url, "Open", "Closed", $"Circuit for {taskId}", 3);
        return retryAt;
    }

    private static object ForgedPayload(string method) => method switch
    {
        "BroadcastDeliveryAttempt" => new WebhookDeliveryAttempt
        {
            WebhookId = "forged-webhook", DeliveryId = "forged-delivery", Url = SharedUrl,
            TaskId = "victim-task-a", TaskType = "video", EventType = "task.completed", AttemptNumber = 99
        },
        "BroadcastDeliverySuccess" => new WebhookDeliverySuccess
        {
            WebhookId = "forged-webhook", DeliveryId = "forged-delivery", Url = SharedUrl,
            TaskId = "victim-task-a", StatusCode = 200, TotalAttempts = 1, ResponseTimeMs = 1
        },
        "BroadcastDeliveryFailure" => new WebhookDeliveryFailure
        {
            WebhookId = "forged-webhook", DeliveryId = "forged-delivery", Url = SharedUrl,
            TaskId = "victim-task-a", ErrorMessage = "forged failure", StatusCode = 500, IsPermanentFailure = true
        },
        "BroadcastRetryScheduled" => new WebhookRetryInfo
        {
            WebhookId = "forged-webhook", DeliveryId = "victim-task-a-retry-1", Url = SharedUrl,
            EventType = "task.completed", NextAttemptNumber = 9, ScheduledAt = DateTime.UtcNow.AddMinutes(1), Reason = "forged retry"
        },
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private static void AssertNotifications(IReadOnlyList<CapturedEvent> messages, string url, string taskId, DateTime retryAt)
    {
        Assert.Equal(DeliveryEvents, messages.Select(message => message.Name));
        Assert.All(messages, message => Assert.Equal(url, message.Payload.GetProperty("url").GetString()));
        foreach (var message in messages.Take(3))
        {
            Assert.Equal(taskId, message.Payload.GetProperty("taskId").GetString());
            Assert.StartsWith(taskId + "_", message.Payload.GetProperty("webhookId").GetString());
            Assert.Equal(DateTimeKind.Utc, message.Payload.GetProperty("timestamp").GetDateTime().Kind);
        }
        var attempt = messages[0].Payload;
        AssertFields(attempt, "attemptNumber", "deliveryId", "eventType", "taskId", "taskType", "timestamp", "url", "webhookId");
        Assert.Equal(2, attempt.GetProperty("attemptNumber").GetInt32());
        Assert.Equal("video", attempt.GetProperty("taskType").GetString());
        Assert.Equal("task.completed", attempt.GetProperty("eventType").GetString());
        var success = messages[1].Payload;
        AssertFields(success, "attemptCount", "deliveryId", "eventType", "responseTimeMs", "statusCode", "taskId", "timestamp", "totalAttempts", "url", "webhookId");
        Assert.Equal(202, success.GetProperty("statusCode").GetInt32());
        Assert.Equal(37, success.GetProperty("responseTimeMs").GetDouble());
        Assert.Equal(2, success.GetProperty("totalAttempts").GetInt32());
        var failure = messages[2].Payload;
        AssertFields(failure, "attemptCount", "attemptNumber", "deliveryId", "error", "errorMessage", "eventType", "isFinal", "isPermanentFailure", "statusCode", "taskId", "timestamp", "url", "webhookId");
        Assert.Equal($"Failure for {taskId}", failure.GetProperty("errorMessage").GetString());
        Assert.Equal(503, failure.GetProperty("statusCode").GetInt32());
        Assert.Equal(2, failure.GetProperty("attemptNumber").GetInt32());
        Assert.True(failure.GetProperty("isPermanentFailure").GetBoolean());
        var retry = messages[3].Payload;
        AssertFields(retry, "delaySeconds", "deliveryId", "eventType", "nextAttemptNumber", "reason", "scheduledAt", "url", "webhookId");
        Assert.Equal($"{taskId}-retry-2", retry.GetProperty("deliveryId").GetString());
        Assert.Equal("webhook.delivery", retry.GetProperty("eventType").GetString());
        Assert.Equal(2, retry.GetProperty("nextAttemptNumber").GetInt32());
        Assert.Equal(retryAt, retry.GetProperty("scheduledAt").GetDateTime());
        Assert.Equal("Retry 2 of 5", retry.GetProperty("reason").GetString());
        Assert.InRange(retry.GetProperty("delaySeconds").GetDouble(), 0, 300);
        var circuit = messages[4].Payload;
        AssertFields(circuit, "currentState", "failureCount", "nextTestAt", "previousState", "reason", "successCount", "timestamp", "url");
        Assert.Equal("Open", circuit.GetProperty("currentState").GetString());
        Assert.Equal("Closed", circuit.GetProperty("previousState").GetString());
        Assert.Equal($"Circuit for {taskId}", circuit.GetProperty("reason").GetString());
        Assert.Equal(3, circuit.GetProperty("failureCount").GetInt32());
        Assert.Equal(0, circuit.GetProperty("successCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, circuit.GetProperty("nextTestAt").ValueKind);
    }

    private static void AssertFields(JsonElement payload, params string[] names) =>
        Assert.Equal(names, payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private sealed record CapturedEvent(string Name, JsonElement Payload);

    private sealed class StatisticsInbox : IDisposable
    {
        private readonly ConcurrentQueue<JsonElement> _messages = new();
        private readonly TaskCompletionSource<JsonElement> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _handler;
        public IReadOnlyList<JsonElement> Messages => _messages.ToArray();
        public Task<JsonElement> First => _first.Task;

        public StatisticsInbox(HubConnection connection) =>
            _handler = connection.On<JsonElement>("DeliveryStatisticsUpdated", payload =>
            {
                var copy = payload.Clone();
                _messages.Enqueue(copy);
                _first.TrySetResult(copy);
            });

        public void Dispose() => _handler.Dispose();
    }

    private sealed class EventInbox : IDisposable
    {
        private readonly ConcurrentQueue<CapturedEvent> _messages = new();
        private readonly List<IDisposable> _handlers = [];
        public IReadOnlyList<CapturedEvent> Messages => _messages.ToArray();

        public EventInbox(HubConnection connection)
        {
            foreach (var name in DeliveryEvents)
                _handlers.Add(connection.On<JsonElement>(name, payload => _messages.Enqueue(new(name, payload.Clone()))));
        }

        public void Dispose()
        {
            foreach (var handler in _handlers) handler.Dispose();
        }
    }
}
