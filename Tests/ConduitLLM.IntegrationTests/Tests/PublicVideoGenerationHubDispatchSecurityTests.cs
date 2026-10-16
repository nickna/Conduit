using System.Collections.Concurrent;
using System.Text.Json;

using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Models;
using ConduitLLM.Gateway.Services;
using ConduitLLM.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

using Moq;

namespace ConduitLLM.IntegrationTests.Tests;

[Trait("Category", "Integration")]
[Trait("Component", "SignalR")]
public sealed class PublicVideoGenerationHubDispatchSecurityTests
{
    [Fact]
    public async Task AnonymousConnectionsWithOwnedTokensReceiveOnlyTheirOwnNotificationsAfterForeignSubscriptionFails()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var ownerA = await host.ConnectAsync(null, GatewaySignalRDispatchHost.PublicVideoPath);
        var ownerB = await host.ConnectAsync(null, GatewaySignalRDispatchHost.PublicVideoPath);
        using var eventsA = new EventInbox(ownerA);
        using var eventsB = new EventInbox(ownerB);
        await SubscribeAsync(ownerA, GatewaySignalRDispatchHost.VideoTaskA, GatewaySignalRDispatchHost.EphemeralTokenA);
        await SubscribeAsync(ownerB, GatewaySignalRDispatchHost.VideoTaskB, GatewaySignalRDispatchHost.EphemeralTokenB);

        var foreignA = await Assert.ThrowsAsync<HubException>(() => SubscribeAsync(
            ownerA, GatewaySignalRDispatchHost.VideoTaskB, GatewaySignalRDispatchHost.EphemeralTokenA));
        var foreignB = await Assert.ThrowsAsync<HubException>(() => SubscribeAsync(
            ownerB, GatewaySignalRDispatchHost.VideoTaskA, GatewaySignalRDispatchHost.EphemeralTokenB));
        Assert.Contains("Task is unavailable or access is denied", foreignA.Message);
        Assert.Equal(foreignA.Message, foreignB.Message);

        var broadcaster = host.CreateVideoBroadcaster();
        await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.VideoTaskA);
        await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.VideoTaskB);
        await host.FlushAsync<PublicVideoGenerationHub>(ownerA, ownerB);

        AssertNotifications(eventsA.Messages, GatewaySignalRDispatchHost.VideoTaskA);
        AssertNotifications(eventsB.Messages, GatewaySignalRDispatchHost.VideoTaskB);
        host.RuntimeKeys.Verify(service => service.ValidateVirtualKeyForAuthenticationAsync(
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        host.EphemeralKeys.Verify(service => service.DeleteKeyAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("missing-token", "Ephemeral key is required")]
    [InlineData("empty-token", "Ephemeral key is required")]
    [InlineData("invalid-token", "Invalid or expired ephemeral key")]
    [InlineData("expired-token", "Ephemeral key has expired")]
    [InlineData("unknown-task", "Task is unavailable or access is denied")]
    [InlineData("ownerless-task", "Task is unavailable or access is denied")]
    [InlineData("zero-owner-task", "Task is unavailable or access is denied")]
    [InlineData("zero-owner-token", "Task is unavailable or access is denied")]
    public async Task InvalidSubscriptionNeverJoinsAnyTaskGroup(string scenario, string expectedError)
    {
        await using var host = new GatewaySignalRDispatchHost();
        var taskId = GatewaySignalRDispatchHost.VideoTaskA;
        string? token = GatewaySignalRDispatchHost.EphemeralTokenA;
        switch (scenario)
        {
            case "missing-token": token = null; break;
            case "empty-token": token = ""; break;
            case "invalid-token": token = "ek_unknown"; break;
            case "expired-token": token = GatewaySignalRDispatchHost.ExpiredEphemeralToken; break;
            case "unknown-task": taskId = "missing-video-task"; break;
            case "ownerless-task": host.Tasks[taskId].Metadata = null; break;
            case "zero-owner-task": host.Tasks[taskId].Metadata = new TaskMetadata(0); break;
            case "zero-owner-token":
                host.EphemeralTokens[GatewaySignalRDispatchHost.EphemeralTokenA] = new EphemeralKeyData
                {
                    VirtualKeyId = 0, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
                };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        await host.StartAsync();
        var connection = await host.ConnectAsync(null, GatewaySignalRDispatchHost.PublicVideoPath);
        using var events = new EventInbox(connection);

        var exception = await Assert.ThrowsAsync<HubException>(() => SubscribeAsync(connection, taskId, token));
        Assert.Contains(expectedError, exception.Message);

        var broadcaster = host.CreateVideoBroadcaster();
        await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.VideoTaskA);
        await SendNotificationsAsync(broadcaster, GatewaySignalRDispatchHost.VideoTaskB);
        await host.FlushAsync<PublicVideoGenerationHub>(connection);
        Assert.Empty(events.Messages);
    }

    [Fact]
    public async Task EphemeralTokenCanBeReusedForResubscriptionAndAnotherConnectionUntilExpiry()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var first = await host.ConnectAsync(null, GatewaySignalRDispatchHost.PublicVideoPath);
        var second = await host.ConnectAsync(null, GatewaySignalRDispatchHost.PublicVideoPath);
        using var firstEvents = new EventInbox(first);
        using var secondEvents = new EventInbox(second);
        await SubscribeAsync(first, GatewaySignalRDispatchHost.VideoTaskA, GatewaySignalRDispatchHost.EphemeralTokenA);
        await GatewaySignalRDispatchHost.InvokeAsync(first, "UnsubscribeFromTask", GatewaySignalRDispatchHost.VideoTaskA);
        await SubscribeAsync(first, GatewaySignalRDispatchHost.VideoTaskA, GatewaySignalRDispatchHost.EphemeralTokenA);
        await SubscribeAsync(second, GatewaySignalRDispatchHost.VideoTaskA, GatewaySignalRDispatchHost.EphemeralTokenA);

        // Expiry blocks another authentication attempt without disturbing a prior valid subscription.
        host.EphemeralTokens[GatewaySignalRDispatchHost.EphemeralTokenA].ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var expired = await Assert.ThrowsAsync<HubException>(() => SubscribeAsync(
            second, GatewaySignalRDispatchHost.VideoTaskA, GatewaySignalRDispatchHost.EphemeralTokenA));
        Assert.Contains("Ephemeral key has expired", expired.Message);
        await host.CreateVideoBroadcaster().NotifyVideoGenerationProgressAsync(
            GatewaySignalRDispatchHost.VideoTaskA, 37, "processing", "retained subscription");
        await host.FlushAsync<PublicVideoGenerationHub>(first, second);

        Assert.Equal(2, firstEvents.Messages.Count(message => message.Name == "taskSubscribed"));
        Assert.Single(secondEvents.Messages, message => message.Name == "taskSubscribed");
        foreach (var inbox in new[] { firstEvents, secondEvents })
        {
            var progress = Assert.Single(inbox.Messages, message => message.Name == SignalRConstants.ClientMethods.TaskProgress);
            Assert.Equal(37, progress.Payload.GetProperty("progress").GetInt32());
            Assert.Equal(GatewaySignalRDispatchHost.VideoTaskA, progress.Payload.GetProperty("taskId").GetString());
        }
        host.EphemeralKeys.Verify(service => service.GetKeyDataAsync(GatewaySignalRDispatchHost.EphemeralTokenA), Times.Exactly(4));
        host.EphemeralKeys.Verify(service => service.DeleteKeyAsync(It.IsAny<string>()), Times.Never);
    }

    private static Task SubscribeAsync(HubConnection connection, string taskId, string? token) =>
        GatewaySignalRDispatchHost.InvokeAsync(connection, "SubscribeToTask", taskId, token);

    private static async Task SendNotificationsAsync(VideoGenerationNotificationService broadcaster, string taskId)
    {
        await broadcaster.NotifyVideoGenerationProgressAsync(taskId, 42, "processing", "Rendering", framesCompleted: 0, totalFrames: 12);
        await broadcaster.NotifyVideoGenerationCompletedAsync(taskId, $"https://video.example/{taskId}.mp4", TimeSpan.FromSeconds(10), 0.12345678m);
        await broadcaster.NotifyVideoGenerationFailedAsync(taskId, $"Provider failed for {taskId}", false);
    }

    private static void AssertNotifications(IReadOnlyList<CapturedEvent> messages, string taskId)
    {
        Assert.Equal(new[] { "taskSubscribed", SignalRConstants.ClientMethods.TaskProgress,
            SignalRConstants.ClientMethods.TaskCompleted, SignalRConstants.ClientMethods.TaskFailed }, messages.Select(message => message.Name));
        Assert.All(messages, message => Assert.Equal(taskId, message.Payload.GetProperty("taskId").GetString()));
        Assert.Equal("Successfully subscribed to task updates", messages[0].Payload.GetProperty("message").GetString());
        var progress = messages[1].Payload;
        Assert.Equal(new[] { "framesCompleted", "message", "progress", "status", "taskId", "timestamp", "totalFrames" },
            progress.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(42, progress.GetProperty("progress").GetInt32());
        Assert.Equal("processing", progress.GetProperty("status").GetString());
        Assert.Equal("Rendering", progress.GetProperty("message").GetString());
        Assert.Equal(0, progress.GetProperty("framesCompleted").GetInt32());
        Assert.Equal(12, progress.GetProperty("totalFrames").GetInt32());
        Assert.Equal(DateTimeKind.Utc, progress.GetProperty("timestamp").GetDateTime().Kind);
        var completed = messages[2].Payload;
        Assert.Equal(new[] { "taskId", "videoUrl" }, completed.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal($"https://video.example/{taskId}.mp4", completed.GetProperty("videoUrl").GetString());
        var failed = messages[3].Payload;
        Assert.Equal(new[] { "error", "taskId" }, failed.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal($"Provider failed for {taskId}", failed.GetProperty("error").GetString());
    }

    private sealed record CapturedEvent(string Name, JsonElement Payload);

    private sealed class EventInbox : IDisposable
    {
        private readonly ConcurrentQueue<CapturedEvent> _messages = new();
        private readonly List<IDisposable> _handlers = [];
        public IReadOnlyList<CapturedEvent> Messages => _messages.ToArray();

        public EventInbox(HubConnection connection)
        {
            foreach (var name in new[] { "taskSubscribed", SignalRConstants.ClientMethods.TaskProgress,
                SignalRConstants.ClientMethods.TaskCompleted, SignalRConstants.ClientMethods.TaskFailed })
                _handlers.Add(connection.On<JsonElement>(name, payload => _messages.Enqueue(new(name, payload.Clone()))));
        }

        public void Dispose()
        {
            foreach (var handler in _handlers) handler.Dispose();
        }
    }
}
