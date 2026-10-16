using System.Collections.Concurrent;
using System.Text.Json;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ConduitLLM.IntegrationTests.Tests;

[Trait("Category", "Integration")]
[Trait("Component", "SignalR")]
public sealed class TaskHubDispatchSecurityTests
{
    private static readonly (string Name, int ArgumentCount)[] LifecycleEvents =
    [
        ("TaskStarted", 3), ("TaskProgress", 3), ("TaskCompleted", 2),
        ("TaskFailed", 3), ("TaskCancelled", 2), ("TaskTimedOut", 2)
    ];

    [Theory]
    [InlineData("TaskStarted")]
    [InlineData("TaskProgress")]
    [InlineData("TaskCompleted")]
    [InlineData("TaskFailed")]
    [InlineData("TaskCancelled")]
    [InlineData("TaskTimedOut")]
    public async Task ClientCannotDiscoverAnyServerLifecycleBroadcaster(string method)
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var owner = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA);
        var foreign = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB);
        using var ownerEvents = new EventInbox(owner);
        using var foreignEvents = new EventInbox(foreign);
        await GatewaySignalRDispatchHost.InvokeAsync(owner, "SubscribeToTask", GatewaySignalRDispatchHost.TaskA);
        await GatewaySignalRDispatchHost.InvokeAsync(foreign, "SubscribeToTask", GatewaySignalRDispatchHost.TaskB);

        // TaskStarted deliberately supplies valid metadata naming the victim owner.
        // Rejection must happen during method lookup, before metadata binding or auth.
        var exception = await Assert.ThrowsAsync<HubException>(() => GatewaySignalRDispatchHost.InvokeAsync(
            foreign, method, ForgedArguments(method, GatewaySignalRDispatchHost.TaskA)));
        Assert.Equal($"Failed to invoke '{method}' due to an error on the server. HubException: Method does not exist.", exception.Message);

        await host.FlushAsync<TaskHub>(owner, foreign);
        Assert.Empty(ownerEvents.Messages);
        Assert.Empty(foreignEvents.Messages);

        // The failed invocation must not disconnect either authenticated client.
        await host.CreateTaskBroadcaster().TaskProgress(GatewaySignalRDispatchHost.TaskA, 17, "trusted");
        await host.FlushAsync<TaskHub>(owner, foreign);
        var notification = Assert.Single(ownerEvents.Messages);
        Assert.Equal("TaskProgress", notification.Name);
        Assert.Equal(17, notification.Arguments[1].GetInt32());
        Assert.Empty(foreignEvents.Messages);
    }

    [Fact]
    public async Task TaskSubscriptionsRejectOtherOwnersAndKeepTheirOriginalGroups()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var ownerA = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA);
        var ownerB = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB);
        using var eventsA = new EventInbox(ownerA);
        using var eventsB = new EventInbox(ownerB);
        await GatewaySignalRDispatchHost.InvokeAsync(ownerA, "SubscribeToTask", GatewaySignalRDispatchHost.TaskA);
        await GatewaySignalRDispatchHost.InvokeAsync(ownerB, "SubscribeToTask", GatewaySignalRDispatchHost.TaskB);

        await Assert.ThrowsAsync<HubException>(() => GatewaySignalRDispatchHost.InvokeAsync(ownerA, "SubscribeToTask", GatewaySignalRDispatchHost.TaskB));
        await Assert.ThrowsAsync<HubException>(() => GatewaySignalRDispatchHost.InvokeAsync(ownerB, "SubscribeToTask", GatewaySignalRDispatchHost.TaskA));
        await Assert.ThrowsAsync<HubException>(() => GatewaySignalRDispatchHost.InvokeAsync(ownerA, "SubscribeToTask", "missing-task"));
        host.Tasks["ownerless-task"] = new AsyncTaskStatus { TaskId = "ownerless-task" };
        await Assert.ThrowsAsync<HubException>(() => GatewaySignalRDispatchHost.InvokeAsync(ownerA, "SubscribeToTask", "ownerless-task"));

        var broadcaster = host.CreateTaskBroadcaster();
        await broadcaster.TaskProgress(GatewaySignalRDispatchHost.TaskA, 11, "owner A");
        await broadcaster.TaskProgress(GatewaySignalRDispatchHost.TaskB, 22, "owner B");
        await host.FlushAsync<TaskHub>(ownerA, ownerB);
        Assert.Equal(GatewaySignalRDispatchHost.TaskA, Assert.Single(eventsA.Messages).Arguments[0].GetString());
        Assert.Equal(GatewaySignalRDispatchHost.TaskB, Assert.Single(eventsB.Messages).Arguments[0].GetString());
    }

    [Fact]
    public async Task LegitimateServerLifecycleNotificationsPreservePayloadsAndOwnerIsolation()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var ownerA = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA);
        var ownerB = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB);
        using var eventsA = new EventInbox(ownerA);
        using var eventsB = new EventInbox(ownerB);
        await GatewaySignalRDispatchHost.InvokeAsync(ownerA, "SubscribeToTask", GatewaySignalRDispatchHost.TaskA);
        await GatewaySignalRDispatchHost.InvokeAsync(ownerB, "SubscribeToTask", GatewaySignalRDispatchHost.TaskB);
        var broadcaster = host.CreateTaskBroadcaster();

        await SendLifecycleAsync(broadcaster, GatewaySignalRDispatchHost.TaskA, new TaskMetadata(GatewaySignalRDispatchHost.OwnerA)
        {
            Model = "typed-metadata",
            ExtensionData = new() { ["custom_KEY"] = new Dictionary<string, object> { ["count"] = 0, ["flag"] = false } }
        });
        await SendLifecycleAsync(broadcaster, GatewaySignalRDispatchHost.TaskB, new Dictionary<string, object>
        {
            ["virtualKeyId"] = GatewaySignalRDispatchHost.OwnerB,
            ["model"] = "dictionary-metadata"
        });
        await host.FlushAsync<TaskHub>(ownerA, ownerB);

        AssertLifecycle(eventsA.Messages, GatewaySignalRDispatchHost.TaskA);
        AssertLifecycle(eventsB.Messages, GatewaySignalRDispatchHost.TaskB);
        var typed = eventsA.Messages[0].Arguments[2];
        Assert.Equal(GatewaySignalRDispatchHost.OwnerA, typed.GetProperty("virtualKeyId").GetInt32());
        Assert.Equal("typed-metadata", typed.GetProperty("model").GetString());
        Assert.Equal(0, typed.GetProperty("custom_KEY").GetProperty("count").GetInt32());
        Assert.False(typed.GetProperty("custom_KEY").GetProperty("flag").GetBoolean());
        Assert.Equal(GatewaySignalRDispatchHost.OwnerB, eventsB.Messages[0].Arguments[2].GetProperty("virtualKeyId").GetInt32());
    }

    [Fact]
    public async Task TaskTypeSubscribersReceiveOnlyTheirOwnersStartedMetadata()
    {
        await using var host = new GatewaySignalRDispatchHost();
        await host.StartAsync();
        var ownerA = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenA);
        var ownerB = await host.ConnectAsync(GatewaySignalRDispatchHost.TokenB);
        using var eventsA = new EventInbox(ownerA);
        using var eventsB = new EventInbox(ownerB);
        await GatewaySignalRDispatchHost.InvokeAsync(ownerA, "SubscribeToTaskType", "image_generation");
        await GatewaySignalRDispatchHost.InvokeAsync(ownerB, "SubscribeToTaskType", "image_generation");
        var broadcaster = host.CreateTaskBroadcaster();
        await broadcaster.TaskStarted(GatewaySignalRDispatchHost.TaskA, "image_generation", new TaskMetadata(GatewaySignalRDispatchHost.OwnerA));
        await broadcaster.TaskStarted(GatewaySignalRDispatchHost.TaskB, "image_generation", new Dictionary<string, object>
        {
            ["virtualKeyId"] = GatewaySignalRDispatchHost.OwnerB
        });
        await host.FlushAsync<TaskHub>(ownerA, ownerB);
        Assert.Equal(GatewaySignalRDispatchHost.TaskA, Assert.Single(eventsA.Messages).Arguments[0].GetString());
        Assert.Equal(GatewaySignalRDispatchHost.TaskB, Assert.Single(eventsB.Messages).Arguments[0].GetString());
    }

    private static object[] ForgedArguments(string method, string taskId) => method switch
    {
        "TaskStarted" => [taskId, "image_generation", new Dictionary<string, object>
        {
            ["virtualKeyId"] = GatewaySignalRDispatchHost.OwnerA,
            ["nested"] = new Dictionary<string, object> { ["forged"] = true }
        }],
        "TaskProgress" => [taskId, 99, "forged progress"],
        "TaskCompleted" => [taskId, new Dictionary<string, object> { ["forged"] = true }],
        "TaskFailed" => [taskId, "forged failure", false],
        "TaskCancelled" => [taskId, "forged cancellation"],
        "TaskTimedOut" => [taskId, 1],
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private static async Task SendLifecycleAsync(ITaskHub broadcaster, string taskId, object metadata)
    {
        await broadcaster.TaskStarted(taskId, "image_generation", metadata);
        await broadcaster.TaskProgress(taskId, 42, "working");
        using var result = JsonDocument.Parse("""{"custom_KEY":null,"count":0,"done":false}""");
        await broadcaster.TaskCompleted(taskId, result.RootElement);
        await broadcaster.TaskFailed(taskId, "provider failed", true);
        await broadcaster.TaskCancelled(taskId, "cancelled by owner");
        await broadcaster.TaskTimedOut(taskId, 120);
    }

    private static void AssertLifecycle(IReadOnlyList<CapturedEvent> messages, string taskId)
    {
        Assert.Equal(LifecycleEvents.Select(item => item.Name), messages.Select(item => item.Name));
        Assert.All(messages, message => Assert.Equal(taskId, message.Arguments[0].GetString()));
        Assert.Equal("image_generation", messages[0].Arguments[1].GetString());
        Assert.Equal(42, messages[1].Arguments[1].GetInt32());
        Assert.Equal("working", messages[1].Arguments[2].GetString());
        Assert.Equal("""{"custom_KEY":null,"count":0,"done":false}""", messages[2].Arguments[1].GetRawText());
        Assert.Equal("provider failed", messages[3].Arguments[1].GetString());
        Assert.True(messages[3].Arguments[2].GetBoolean());
        Assert.Equal("cancelled by owner", messages[4].Arguments[1].GetString());
        Assert.Equal(120, messages[5].Arguments[1].GetInt32());
    }

    private sealed record CapturedEvent(string Name, JsonElement[] Arguments);

    private sealed class EventInbox : IDisposable
    {
        private readonly ConcurrentQueue<CapturedEvent> _messages = new();
        private readonly List<IDisposable> _handlers = [];
        public IReadOnlyList<CapturedEvent> Messages => _messages.ToArray();

        public EventInbox(HubConnection connection)
        {
            foreach (var (name, count) in LifecycleEvents)
            {
                _handlers.Add(connection.On(name, Enumerable.Repeat(typeof(JsonElement), count).ToArray(), arguments =>
                {
                    _messages.Enqueue(new(name, arguments.Select(argument => ((JsonElement)argument!).Clone()).ToArray()));
                    return Task.CompletedTask;
                }));
            }
        }

        public void Dispose()
        {
            foreach (var handler in _handlers) handler.Dispose();
        }
    }
}
