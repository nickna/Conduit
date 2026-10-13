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
        var clients = new Mock<IHubClients>();
        clients.SetupGet(value => value.All).Returns(proxy.Object);
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
        var clients = new Mock<IHubClients>();
        clients.SetupGet(value => value.All).Returns(proxy.Object);
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
            .Setup(clients => clients.Group(SignalRConstants.Groups.Webhook(webhookUrl)))
            .Returns(clientProxy.Object);
        var hubContext = new Mock<IHubContext<WebhookDeliveryHub>>();
        hubContext.SetupGet(context => context.Clients).Returns(hubClients.Object);
        var service = new WebhookDeliveryNotificationService(
            hubContext.Object,
            Mock.Of<IServiceProvider>(),
            Mock.Of<ILogger<WebhookDeliveryNotificationService>>());

        await service.NotifyDeliveryAttemptAsync(
            webhookUrl, "task-1", "video", "completed", attemptNumber: 2);

        clientProxy.Verify(client => client.SendCoreAsync(
            "DeliveryAttempted",
            It.Is<object?[]>(arguments => IsExpectedAttempt(arguments, webhookUrl)),
            It.IsAny<CancellationToken>()), Times.Once);
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
