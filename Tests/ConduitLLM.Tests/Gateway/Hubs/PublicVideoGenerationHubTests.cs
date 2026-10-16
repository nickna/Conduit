using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Models;
using ConduitLLM.Gateway.Services;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Gateway.Hubs;

[Trait("Category", "Unit")]
[Trait("Component", "PublicVideoGenerationHub")]
public sealed class PublicVideoGenerationHubTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingToken_RejectsWithoutReadingOwnershipOrChangingSubscriptions(string token)
    {
        using var fixture = new SubscriptionFixture();

        await Assert.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeToTask("task-a", token));

        fixture.Keys.VerifyNoOtherCalls();
        fixture.Tasks.VerifyNoOtherCalls();
        fixture.AssertUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrExpiredToken_RejectsBeforeReadingTaskOwnership(bool expired)
    {
        using var fixture = new SubscriptionFixture();
        fixture.Keys.Setup(service => service.GetKeyDataAsync("token-a"))
            .ReturnsAsync(expired
                ? new EphemeralKeyData { VirtualKeyId = 11, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }
                : null);

        await Assert.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeToTask("task-a", "token-a"));

        fixture.Tasks.VerifyNoOtherCalls();
        fixture.AssertUnchanged();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveTokenOwner_RejectsWithoutReadingTaskOwnership(int owner)
    {
        using var fixture = new SubscriptionFixture();
        fixture.SetToken(owner);

        var error = await Assert.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeToTask("task-a", "token-a"));

        Assert.Equal("Task is unavailable or access is denied", error.Message);
        fixture.Tasks.VerifyNoOtherCalls();
        fixture.AssertUnchanged();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing-metadata")]
    [InlineData("zero-owner")]
    [InlineData("negative-owner")]
    [InlineData("foreign-owner")]
    public async Task UnverifiableOrForeignTask_UsesSameFailureAndPreservesExistingSubscription(string scenario)
    {
        using var fixture = new SubscriptionFixture();
        fixture.SetToken(11);
        var status = scenario switch
        {
            "unknown" => null,
            "missing-metadata" => new AsyncTaskStatus { TaskId = "task-b" },
            "zero-owner" => new AsyncTaskStatus { TaskId = "task-b", Metadata = new TaskMetadata(0) },
            "negative-owner" => new AsyncTaskStatus { TaskId = "task-b", Metadata = new TaskMetadata(-1) },
            _ => new AsyncTaskStatus { TaskId = "task-b", Metadata = new TaskMetadata(22) }
        };
        fixture.Tasks.Setup(service => service.GetTaskStatusAsync("task-b", fixture.ConnectionAborted))
            .Callback(() => fixture.AssertUnchanged())
            .ReturnsAsync(status);

        var error = await Assert.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeToTask("task-b", "token-a"));

        Assert.Equal("Task is unavailable or access is denied", error.Message);
        fixture.AssertUnchanged();
        fixture.Tasks.Verify(service => service.GetTaskStatusAsync("task-b", fixture.ConnectionAborted), Times.Once);
    }

    [Fact]
    public async Task OwnedTask_ValidatesBeforeJoiningAndSendsExistingSubscriptionPayload()
    {
        using var fixture = new SubscriptionFixture();
        fixture.SetToken(11);
        var ownerResolved = false;
        fixture.Tasks.Setup(service => service.GetTaskStatusAsync("task-a", fixture.ConnectionAborted))
            .Callback(() =>
            {
                fixture.AssertUnchanged();
                ownerResolved = true;
            })
            .ReturnsAsync(new AsyncTaskStatus { TaskId = "task-a", Metadata = new TaskMetadata(11) });
        fixture.Groups.Setup(service => service.AddToGroupAsync(
                "connection", SignalRConstants.Groups.VideoTask("task-a"), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Assert.True(ownerResolved);
                Assert.Equal("already-owned", fixture.Items["TaskId"]);
                Assert.Equal(11, fixture.Items["VirtualKeyId"]);
            })
            .Returns(Task.CompletedTask);

        await fixture.Hub.SubscribeToTask("task-a", "token-a");

        Assert.Equal("task-a", fixture.Items["TaskId"]);
        Assert.Equal(11, fixture.Items["VirtualKeyId"]);
        fixture.Groups.Verify(service => service.AddToGroupAsync(
            "connection", SignalRConstants.Groups.VideoTask("task-a"), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Caller.Verify(client => client.SendCoreAsync("taskSubscribed",
            It.Is<object[]>(arguments => arguments.Length == 1 &&
                arguments[0] is TaskSubscriptionNotification &&
                ((TaskSubscriptionNotification)arguments[0]).TaskId == "task-a" &&
                ((TaskSubscriptionNotification)arguments[0]).Message == "Successfully subscribed to task updates"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReusedToken_AllowsMultipleOwnedSubscriptionsAndRejectsForeignAttemptWithoutRemovingEither()
    {
        using var fixture = new SubscriptionFixture();
        fixture.SetToken(11);
        fixture.Tasks.Setup(service => service.GetTaskStatusAsync(It.IsAny<string>(), fixture.ConnectionAborted))
            .ReturnsAsync((string taskId, CancellationToken _) => new AsyncTaskStatus
            {
                TaskId = taskId,
                Metadata = new TaskMetadata(taskId == "foreign" ? 22 : 11)
            });

        await fixture.Hub.SubscribeToTask("task-a", "token-a");
        await fixture.Hub.SubscribeToTask("task-b", "token-a");
        await Assert.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeToTask("foreign", "token-a"));

        Assert.Equal("task-b", fixture.Items["TaskId"]);
        Assert.Equal(11, fixture.Items["VirtualKeyId"]);
        fixture.Keys.Verify(service => service.GetKeyDataAsync("token-a"), Times.Exactly(3));
        fixture.Keys.VerifyNoOtherCalls();
        fixture.Groups.Verify(service => service.AddToGroupAsync(
            "connection", SignalRConstants.Groups.VideoTask("task-a"), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Groups.Verify(service => service.AddToGroupAsync(
            "connection", SignalRConstants.Groups.VideoTask("task-b"), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Groups.VerifyNoOtherCalls();
        fixture.Caller.Verify(client => client.SendCoreAsync(
            "taskSubscribed", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.Caller.VerifyNoOtherCalls();
    }

    private sealed class SubscriptionFixture : IDisposable
    {
        private readonly CancellationTokenSource _connection = new();
        public Mock<IEphemeralKeyService> Keys { get; } = new();
        public Mock<IAsyncTaskService> Tasks { get; } = new();
        public Mock<IGroupManager> Groups { get; } = new();
        public Mock<ISingleClientProxy> Caller { get; } = new();
        public Dictionary<object, object?> Items { get; } = new()
        {
            ["TaskId"] = "already-owned", ["VirtualKeyId"] = 11
        };
        public CancellationToken ConnectionAborted => _connection.Token;
        public PublicVideoGenerationHub Hub { get; }

        public SubscriptionFixture()
        {
            var context = new Mock<HubCallerContext>();
            context.SetupGet(value => value.ConnectionId).Returns("connection");
            context.SetupGet(value => value.Items).Returns(Items);
            context.SetupGet(value => value.ConnectionAborted).Returns(ConnectionAborted);
            var clients = new Mock<IHubCallerClients>();
            clients.SetupGet(value => value.Caller).Returns(Caller.Object);
            Hub = new PublicVideoGenerationHub(
                NullLogger<PublicVideoGenerationHub>.Instance, Keys.Object, Tasks.Object)
            {
                Context = context.Object, Groups = Groups.Object, Clients = clients.Object
            };
        }

        public void SetToken(int owner) => Keys.Setup(service => service.GetKeyDataAsync("token-a"))
            .ReturnsAsync(new EphemeralKeyData { VirtualKeyId = owner, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });

        public void AssertUnchanged()
        {
            Assert.Equal(2, Items.Count);
            Assert.Equal("already-owned", Items["TaskId"]);
            Assert.Equal(11, Items["VirtualKeyId"]);
            Assert.Empty(Groups.Invocations);
            Assert.Empty(Caller.Invocations);
        }

        public void Dispose() => _connection.Dispose();
    }
}
