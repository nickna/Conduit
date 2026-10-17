using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

namespace ConduitLLM.Tests.Gateway.Services;

public sealed class WebhookDeliveryNotificationServiceTests
{
    [Theory]
    [InlineData(0L, 0L, 0L)]
    [InlineData(3L, 7L, 2L)]
    public async Task RetainedStatisticsKeepTheirContractAndPeriodicPublicationTargetsOnlyAdministrators(
        long pending, long delivered, long exhausted)
    {
        var clock = new FakeTimeProvider();
        var recovery = new Mock<IWebhookRecovery>();
        recovery.Setup(value => value.BacklogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebhookBacklog(pending, delivered, exhausted, 4, 13));
        using var provider = new ServiceCollection().AddSingleton(recovery.Object).BuildServiceProvider();
        var sent = new TaskCompletionSource<WebhookStatistics>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        CancellationToken broadcastToken = default;
        proxy.Setup(value => value.SendCoreAsync("DeliveryStatisticsUpdated", It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, arguments, token) =>
            {
                broadcastToken = token;
                sent.TrySetResult(Assert.IsType<WebhookStatistics>(Assert.Single(arguments)));
            }).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>(MockBehavior.Strict);
        clients.Setup(value => value.Group("admin")).Returns(proxy.Object);
        var hub = new Mock<IHubContext<WebhookDeliveryHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        using var service = new WebhookDeliveryNotificationService(hub.Object, provider,
            NullLogger<WebhookDeliveryNotificationService>.Instance, clock);

        // Reading the service directly preserves the existing retained aggregate
        // contract and does not publish anything to hub clients.
        var direct = await service.GetStatisticsAsync("last_day");
        AssertStatistics(direct);
        clients.VerifyNoOtherCalls();

        try
        {
            await service.StartAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
            var published = await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AssertStatistics(published);
        }
        finally { await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }

        Assert.True(broadcastToken.IsCancellationRequested);
        recovery.Verify(value => value.BacklogAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        clients.Verify(value => value.Group("admin"), Times.Once);
        clients.VerifyGet(value => value.All, Times.Never);
        proxy.Verify(value => value.SendCoreAsync("DeliveryStatisticsUpdated", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        proxy.VerifyNoOtherCalls();
        clients.VerifyNoOtherCalls();

        void AssertStatistics(WebhookStatistics stats)
        {
            var total = pending + delivered + exhausted;
            Assert.Equal("retained", stats.Period);
            Assert.Equal((int)total, stats.TotalDeliveries);
            Assert.Equal((int)pending, stats.PendingDeliveries);
            Assert.Equal((int)delivered, stats.SuccessfulDeliveries);
            Assert.Equal((int)exhausted, stats.FailedDeliveries);
            Assert.Equal(total == 0 ? 0 : 100.0 * delivered / total, stats.SuccessRate);
            Assert.Equal(0, stats.AverageResponseTimeMs);
            Assert.Empty(stats.UrlStatistics);
            Assert.Equal(DateTimeKind.Utc, stats.Timestamp.Kind);
        }
    }

    [Fact]
    public async Task DisposeBeforeStop_CancelsAndJoinsHeldBroadcastWithoutAnotherGeneration()
    {
        var clock = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken broadcastToken = default;
        var recovery = new Mock<IWebhookRecovery>();
        recovery.Setup(value => value.BacklogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebhookBacklog(1, 2, 0, 0, 0));
        using var provider = new ServiceCollection().AddSingleton(recovery.Object).BuildServiceProvider();
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(value => value.SendCoreAsync("DeliveryStatisticsUpdated", It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, object?[] _, CancellationToken token) =>
            {
                broadcastToken = token;
                entered.TrySetResult();
                return release.Task;
            });
        var clients = new Mock<IHubClients>(MockBehavior.Strict);
        clients.Setup(value => value.Group("admin")).Returns(proxy.Object);
        var hub = new Mock<IHubContext<WebhookDeliveryHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        using var service = new WebhookDeliveryNotificationService(hub.Object, provider,
            NullLogger<WebhookDeliveryNotificationService>.Instance, clock);

        await service.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Dispose();
        Assert.True(broadcastToken.IsCancellationRequested);
        var stopping = service.StopAsync(CancellationToken.None);
        service.Dispose();
        Assert.False(stopping.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(3));
        release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync(CancellationToken.None));

        recovery.Verify(value => value.BacklogAsync(It.IsAny<CancellationToken>()), Times.Once);
        proxy.Verify(value => value.SendCoreAsync("DeliveryStatisticsUpdated", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        clients.Verify(value => value.Group("admin"), Times.Once);
        clients.VerifyGet(value => value.All, Times.Never);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StopAsync_JoinsHeldStatisticsAndAllowsANewGenerationWithoutLateBroadcasts()
    {
        var clock = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<WebhookBacklog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = 0;
        CancellationToken heldToken = default;
        var recovery = new Mock<IWebhookRecovery>();
        recovery.Setup(value => value.BacklogAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) =>
            {
                if (Interlocked.Increment(ref queries) != 1)
                    return Task.FromResult(new WebhookBacklog(1, 2, 0, 0, 0));
                heldToken = token;
                entered.TrySetResult();
                return held.Task;
            });
        using var provider = new ServiceCollection().AddSingleton(recovery.Object).BuildServiceProvider();
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(value => value.SendCoreAsync("DeliveryStatisticsUpdated", It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => sent.TrySetResult()).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>(MockBehavior.Strict);
        clients.Setup(value => value.Group("admin")).Returns(proxy.Object);
        var hub = new Mock<IHubContext<WebhookDeliveryHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        using var service = new WebhookDeliveryNotificationService(hub.Object, provider,
            NullLogger<WebhookDeliveryNotificationService>.Instance, clock);

        await service.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var stopping = service.StopAsync(canceled.Token);
        Assert.False(stopping.IsCompleted);
        Assert.True(heldToken.IsCancellationRequested);
        held.TrySetResult(new WebhookBacklog(1, 2, 0, 0, 0));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        proxy.Verify(value => value.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);

        await service.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);
        service.Dispose();
        service.Dispose();
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(2, queries);
        proxy.Verify(value => value.SendCoreAsync("DeliveryStatisticsUpdated", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        clients.Verify(value => value.Group("admin"), Times.Once);
        clients.VerifyGet(value => value.All, Times.Never);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NotifyDeliveryAttemptAsync_SendsAttemptToWebhookGroup()
    {
        const string webhookUrl = "https://example.test/webhook";
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(client => client.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hubClients = new Mock<IHubClients>();
        hubClients
            .Setup(clients => clients.Group(SignalRConstants.Groups.Webhook(42, webhookUrl)))
            .Returns(clientProxy.Object);
        var hubContext = new Mock<IHubContext<WebhookDeliveryHub>>();
        hubContext.SetupGet(context => context.Clients).Returns(hubClients.Object);
        var service = new WebhookDeliveryNotificationService(
            hubContext.Object,
            Mock.Of<IServiceProvider>(),
            Mock.Of<ILogger<WebhookDeliveryNotificationService>>());

        await service.NotifyDeliveryAttemptAsync(
            42, webhookUrl, "task-1", "video", "completed", attemptNumber: 2);

        clientProxy.Verify(client => client.SendCoreAsync(
            "DeliveryAttempted",
            It.Is<object?[]>(arguments => IsExpectedAttempt(arguments, webhookUrl)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AllDestinationNotifications_KeepPayloadsAndPublishOnlyToOwnerGroup()
    {
        const string url = "https://shared.example/webhook?recipient=one";
        var notifications = new List<(string Event, object Payload)>();
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(client => client.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((name, arguments, _) =>
                notifications.Add((name, Assert.Single(arguments))))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>(MockBehavior.Strict);
        clients.Setup(value => value.Group(SignalRConstants.Groups.Webhook(42, url))).Returns(proxy.Object);
        var hub = new Mock<IHubContext<WebhookDeliveryHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        using var service = new WebhookDeliveryNotificationService(
            hub.Object, Mock.Of<IServiceProvider>(), NullLogger<WebhookDeliveryNotificationService>.Instance);
        var retryTime = DateTime.UtcNow.AddMinutes(1);

        await service.NotifyDeliveryAttemptAsync(42, url, "task", "video", "completed", 2);
        await service.NotifyDeliverySuccessAsync(42, url, "task", 202, 123, 2);
        await service.NotifyDeliveryFailureAsync(42, url, "task", "receiver error", 503, 2, false);
        await service.NotifyRetryScheduledAsync(42, url, "task", retryTime, 3, 5);
        await service.NotifyCircuitBreakerStateChangeAsync(42, url, "open", "closed", "receiver error", 3);

        Assert.Collection(notifications,
            item =>
            {
                Assert.Equal("DeliveryAttempted", item.Event);
                var payload = Assert.IsType<WebhookDeliveryAttempt>(item.Payload);
                Assert.Equal(url, payload.Url);
                Assert.Equal("task", payload.TaskId);
                Assert.Equal("video", payload.TaskType);
                Assert.Equal("completed", payload.EventType);
                Assert.Equal(2, payload.AttemptNumber);
            },
            item =>
            {
                Assert.Equal("DeliverySucceeded", item.Event);
                var payload = Assert.IsType<WebhookDeliverySuccess>(item.Payload);
                Assert.Equal(url, payload.Url);
                Assert.Equal("task", payload.TaskId);
                Assert.Equal(202, payload.StatusCode);
                Assert.Equal(123, payload.ResponseTimeMs);
                Assert.Equal(2, payload.TotalAttempts);
            },
            item =>
            {
                Assert.Equal("DeliveryFailed", item.Event);
                var payload = Assert.IsType<WebhookDeliveryFailure>(item.Payload);
                Assert.Equal(url, payload.Url);
                Assert.Equal("task", payload.TaskId);
                Assert.Equal("receiver error", payload.ErrorMessage);
                Assert.Equal(503, payload.StatusCode);
                Assert.Equal(2, payload.AttemptNumber);
                Assert.False(payload.IsPermanentFailure);
            },
            item =>
            {
                Assert.Equal("RetryScheduled", item.Event);
                var payload = Assert.IsType<WebhookRetryInfo>(item.Payload);
                Assert.Equal(url, payload.Url);
                Assert.Equal("task-retry-3", payload.DeliveryId);
                Assert.Equal(retryTime, payload.ScheduledAt);
                Assert.Equal(3, payload.NextAttemptNumber);
            },
            item =>
            {
                Assert.Equal("CircuitBreakerStateChanged", item.Event);
                var payload = Assert.IsType<WebhookCircuitBreakerState>(item.Payload);
                Assert.Equal(url, payload.Url);
                Assert.Equal("open", payload.CurrentState);
                Assert.Equal("closed", payload.PreviousState);
                Assert.Equal("receiver error", payload.Reason);
                Assert.Equal(3, payload.FailureCount);
            });
        clients.Verify(value => value.Group(SignalRConstants.Groups.Webhook(42, url)), Times.Exactly(5));
        foreach (var eventName in new[]
                 { "DeliveryAttempted", "DeliverySucceeded", "DeliveryFailed", "RetryScheduled", "CircuitBreakerStateChanged" })
            proxy.Verify(value => value.SendCoreAsync(eventName,
                It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
        clients.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task LegacyOrInvalidOwner_PublishesNoDestinationNotifications(int owner)
    {
        var hub = new Mock<IHubContext<WebhookDeliveryHub>>(MockBehavior.Strict);
        using var service = new WebhookDeliveryNotificationService(
            hub.Object, Mock.Of<IServiceProvider>(), NullLogger<WebhookDeliveryNotificationService>.Instance);
        const string url = "https://shared.example/webhook";

        await service.NotifyDeliveryAttemptAsync(owner, url, "task", "video", "completed", 1);
        await service.NotifyDeliverySuccessAsync(owner, url, "task", 202, 123, 1);
        await service.NotifyDeliveryFailureAsync(owner, url, "task", "receiver error", 503, 1, false);
        await service.NotifyRetryScheduledAsync(owner, url, "task", DateTime.UtcNow, 2, 5);
        await service.NotifyCircuitBreakerStateChangeAsync(owner, url, "open", "closed", "receiver error", 3);

        hub.VerifyNoOtherCalls();
    }

    private static bool IsExpectedAttempt(object?[] arguments, string webhookUrl)
    {
        return arguments.Length == 1 &&
               arguments[0] is WebhookDeliveryAttempt attempt &&
               attempt.TaskId == "task-1" &&
               attempt.AttemptNumber == 2 &&
               attempt.Url == webhookUrl;
    }
}
