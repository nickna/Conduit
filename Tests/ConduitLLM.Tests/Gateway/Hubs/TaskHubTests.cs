using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit.Abstractions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Hubs;

namespace ConduitLLM.Tests.Gateway.Hubs
{
    /// <summary>
    /// Unit tests for the TaskHub SignalR hub.
    /// Tests task subscription, unsubscription, and ITaskHub notification methods.
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Component", "SignalR")]
    [Trait("Feature", "TaskHub")]
    public class TaskHubTests : HubTestBase
    {
        private readonly Mock<ILogger<TaskHub>> _mockLogger;
        private readonly Mock<IAsyncTaskService> _mockTaskService;
        private readonly Mock<IHubContext<TaskHub>> _mockHubContext;
        private readonly Mock<IHubClients> _mockHubContextClients;
        private readonly Mock<IClientProxy> _mockHubContextClientProxy;

        public TaskHubTests(ITestOutputHelper output) : base(output)
        {
            _mockLogger = CreateLogger<TaskHub>();
            _mockTaskService = new Mock<IAsyncTaskService>();
            _mockHubContext = new Mock<IHubContext<TaskHub>>();
            _mockHubContextClients = new Mock<IHubClients>();
            _mockHubContextClientProxy = new Mock<IClientProxy>();

            // Wire up hub context mocks
            SetupHubContextMocks();
        }

        private void SetupHubContextMocks()
        {
            _mockHubContext.Setup(x => x.Clients).Returns(_mockHubContextClients.Object);
            _mockHubContextClients.Setup(x => x.Group(It.IsAny<string>())).Returns(_mockHubContextClientProxy.Object);
            _mockHubContextClientProxy.Setup(x => x.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        /// <summary>
        /// Creates a TaskHub instance with mocked context.
        /// </summary>
        private TaskHub CreateHub(int? virtualKeyId = DefaultVirtualKeyId)
        {
            var hub = new TaskHub(
                _mockLogger.Object,
                _mockTaskService.Object,
                _mockHubContext.Object,
                MockServiceProvider.Object);

            // Create and configure context
            var context = CreateHubCallerContext(virtualKeyId);

            // Set up auth service
            if (virtualKeyId.HasValue)
            {
                SetupAuthServiceReturnsVirtualKey(virtualKeyId.Value);
            }
            else
            {
                SetupAuthServiceReturnsNoVirtualKey();
            }

            // Set Context, Groups, and Clients on hub using reflection
            typeof(Hub).GetProperty("Context")?.SetValue(hub, context.Object);
            typeof(Hub).GetProperty("Groups")?.SetValue(hub, MockGroups.Object);
            typeof(Hub).GetProperty("Clients")?.SetValue(hub, MockClients.Object);

            return hub;
        }

        /// <summary>
        /// Creates an AsyncTaskStatus with the specified virtual key ID.
        /// </summary>
        private AsyncTaskStatus CreateTaskStatus(string taskId, int virtualKeyId, string taskType = "image_generation")
        {
            return new AsyncTaskStatus
            {
                TaskId = taskId,
                TaskType = taskType,
                State = TaskState.Processing,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Metadata = new TaskMetadata(virtualKeyId)
                {
                    Model = "dall-e-3",
                    Prompt = "test prompt"
                }
            };
        }

        #region SubscribeToTask Tests

        [Fact]
        public async Task SubscribeToTask_WithValidTaskId_AddsToTaskGroup()
        {
            // Arrange
            var taskId = "task-123";
            var hub = CreateHub(virtualKeyId: DefaultVirtualKeyId);
            var taskStatus = CreateTaskStatus(taskId, DefaultVirtualKeyId);
            _mockTaskService.Setup(x => x.GetTaskStatusAsync(taskId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(taskStatus);

            // Act
            await hub.SubscribeToTask(taskId);

            // Assert
            VerifyAddedToGroup(TaskGroup(taskId));
        }

        [Fact]
        public async Task SubscribeToTask_WithEmptyTaskId_ThrowsHubException()
        {
            // Arrange
            var hub = CreateHub();

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToTask(""));
            Assert.Equal("Invalid task ID", exception.Message);
        }

        [Fact]
        public async Task SubscribeToTask_WithWhitespaceTaskId_ThrowsHubException()
        {
            // Arrange
            var hub = CreateHub();

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToTask("   "));
            Assert.Equal("Invalid task ID", exception.Message);
        }

        [Fact]
        public async Task SubscribeToTask_WithNonExistentTask_ThrowsHubException()
        {
            // Arrange
            var taskId = "non-existent-task";
            var hub = CreateHub();
            _mockTaskService.Setup(x => x.GetTaskStatusAsync(taskId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((AsyncTaskStatus?)null);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToTask(taskId));
            Assert.Equal("Task not found", exception.Message);
        }

        [Fact]
        public async Task SubscribeToTask_WithUnauthorizedTask_ThrowsHubException()
        {
            // Arrange
            var taskId = "task-other-user";
            var hub = CreateHub(virtualKeyId: DefaultVirtualKeyId);
            var taskStatus = CreateTaskStatus(taskId, virtualKeyId: 999); // Different virtual key
            _mockTaskService.Setup(x => x.GetTaskStatusAsync(taskId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(taskStatus);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToTask(taskId));
            Assert.Equal("Unauthorized access to task", exception.Message);
        }

        [Fact]
        public async Task SubscribeToTask_WithoutVirtualKey_ThrowsHubException()
        {
            // Arrange
            var hub = CreateHub(virtualKeyId: null);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToTask("task-123"));
            Assert.Equal("Unauthorized", exception.Message);
        }

        [Fact]
        public async Task SubscribeToTask_WhenServiceThrows_ThrowsHubException()
        {
            // Arrange
            var taskId = "task-error";
            var hub = CreateHub();
            _mockTaskService.Setup(x => x.GetTaskStatusAsync(taskId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Database error"));

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToTask(taskId));
            Assert.Contains("Failed to subscribe", exception.Message);
        }

        [Fact]
        public async Task SubscribeToTask_WithTaskWithNullMetadata_ThrowsUnauthorized()
        {
            // Arrange
            var taskId = "task-no-metadata";
            var hub = CreateHub();
            var taskStatus = new AsyncTaskStatus
            {
                TaskId = taskId,
                TaskType = "image_generation",
                State = TaskState.Processing,
                Metadata = null // No metadata
            };
            _mockTaskService.Setup(x => x.GetTaskStatusAsync(taskId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(taskStatus);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToTask(taskId));
            Assert.Equal("Unauthorized access to task", exception.Message);
        }

        #endregion

        #region UnsubscribeFromTask Tests

        [Fact]
        public async Task UnsubscribeFromTask_RemovesFromTaskGroup()
        {
            // Arrange
            var taskId = "task-456";
            var hub = CreateHub();

            // Act
            await hub.UnsubscribeFromTask(taskId);

            // Assert
            VerifyRemovedFromGroup(TaskGroup(taskId));
        }

        [Fact]
        public async Task UnsubscribeFromTask_LogsDebugMessage()
        {
            // Arrange
            var taskId = "task-log-test";
            var hub = CreateHub();

            // Act
            await hub.UnsubscribeFromTask(taskId);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("unsubscribed from task")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once());
        }

        #endregion

        #region SubscribeToTaskType Tests

        [Fact]
        public async Task SubscribeToTaskType_AddsToTypeSpecificGroup()
        {
            // Arrange
            var taskType = "image_generation";
            var hub = CreateHub(virtualKeyId: DefaultVirtualKeyId);

            // Act
            await hub.SubscribeToTaskType(taskType);

            // Assert
            VerifyAddedToGroup(TaskTypeGroup(DefaultVirtualKeyId, taskType));
        }

        [Fact]
        public async Task SubscribeToTaskType_WithoutVirtualKey_ThrowsHubException()
        {
            // Arrange
            var hub = CreateHub(virtualKeyId: null);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToTaskType("image_generation"));
            Assert.Equal("Unauthorized", exception.Message);
        }

        [Fact]
        public async Task SubscribeToTaskType_LogsDebugMessage()
        {
            // Arrange
            var taskType = "video_generation";
            var hub = CreateHub(virtualKeyId: 456);

            // Act
            await hub.SubscribeToTaskType(taskType);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("subscribed to task type")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once());
        }

        #endregion

        #region UnsubscribeFromTaskType Tests

        [Fact]
        public async Task UnsubscribeFromTaskType_RemovesFromTypeGroup()
        {
            // Arrange
            var taskType = "image_generation";
            var hub = CreateHub(virtualKeyId: DefaultVirtualKeyId);

            // Act
            await hub.UnsubscribeFromTaskType(taskType);

            // Assert
            VerifyRemovedFromGroup(TaskTypeGroup(DefaultVirtualKeyId, taskType));
        }

        [Fact]
        public async Task UnsubscribeFromTaskType_WithoutVirtualKey_ThrowsHubException()
        {
            // Arrange
            var hub = CreateHub(virtualKeyId: null);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HubException>(() => hub.UnsubscribeFromTaskType("image_generation"));
            Assert.Equal("Unauthorized", exception.Message);
        }

        #endregion

        #region ITaskHub - TaskStarted Tests

        [Fact]
        public async Task TaskStarted_WithTaskMetadata_NotifiesTaskAndTypeGroups()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-started-1";
            var taskType = "image_generation";
            var metadata = new TaskMetadata(DefaultVirtualKeyId) { Model = "dall-e-3" };

            // Act
            await ((ITaskHub)hub).TaskStarted(taskId, taskType, metadata);

            // Assert
            _mockHubContextClients.Verify(x => x.Group(TaskGroup(taskId)), Times.Once());
            _mockHubContextClients.Verify(x => x.Group(TaskTypeGroup(DefaultVirtualKeyId, taskType)), Times.Once());
            _mockHubContextClientProxy.Verify(
                x => x.SendCoreAsync("TaskStarted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }

        [Fact]
        public async Task TaskStarted_WithDictionaryMetadata_NotifiesTaskAndTypeGroups()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-started-dict";
            var taskType = "video_generation";
            var metadata = new Dictionary<string, object>
            {
                ["virtualKeyId"] = DefaultVirtualKeyId,
                ["model"] = "runway-gen3"
            };

            // Act
            await ((ITaskHub)hub).TaskStarted(taskId, taskType, metadata);

            // Assert
            _mockHubContextClients.Verify(x => x.Group(TaskGroup(taskId)), Times.Once());
            _mockHubContextClients.Verify(x => x.Group(TaskTypeGroup(DefaultVirtualKeyId, taskType)), Times.Once());
        }

        [Fact]
        public async Task TaskStarted_WithoutVirtualKeyId_DoesNotNotify()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-no-vk";
            var taskType = "image_generation";
            var metadata = new { model = "dall-e-3" }; // No virtualKeyId

            // Act
            await ((ITaskHub)hub).TaskStarted(taskId, taskType, metadata);

            // Assert
            _mockHubContextClientProxy.Verify(
                x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        #endregion

        #region ITaskHub - TaskProgress Tests

        [Fact]
        public async Task TaskProgress_SendsToTaskGroup()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-progress-1";
            var progress = 50;
            var message = "Processing...";

            // Act
            await ((ITaskHub)hub).TaskProgress(taskId, progress, message);

            // Assert
            _mockHubContextClients.Verify(x => x.Group(TaskGroup(taskId)), Times.Once());
            _mockHubContextClientProxy.Verify(
                x => x.SendCoreAsync(
                    "TaskProgress",
                    It.Is<object[]>(args => args.Length >= 2 && (int)args[1] == progress),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task TaskProgress_WithNullMessage_SendsToTaskGroup()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-progress-null-msg";
            var progress = 75;

            // Act
            await ((ITaskHub)hub).TaskProgress(taskId, progress);

            // Assert
            _mockHubContextClients.Verify(x => x.Group(TaskGroup(taskId)), Times.Once());
        }

        #endregion

        #region ITaskHub - TaskCompleted Tests

        [Fact]
        public async Task TaskCompleted_SendsToTaskGroup()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-completed-1";
            var result = new { ImageUrl = "https://example.com/image.png" };

            // Act
            await ((ITaskHub)hub).TaskCompleted(taskId, result);

            // Assert
            _mockHubContextClients.Verify(x => x.Group(TaskGroup(taskId)), Times.Once());
            _mockHubContextClientProxy.Verify(
                x => x.SendCoreAsync("TaskCompleted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion

        #region ITaskHub - TaskFailed Tests

        [Fact]
        public async Task TaskFailed_SendsToTaskGroup()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-failed-1";
            var error = "Generation failed due to content policy";
            var isRetryable = false;

            // Act
            await ((ITaskHub)hub).TaskFailed(taskId, error, isRetryable);

            // Assert
            _mockHubContextClients.Verify(x => x.Group(TaskGroup(taskId)), Times.Once());
            _mockHubContextClientProxy.Verify(
                x => x.SendCoreAsync(
                    "TaskFailed",
                    It.Is<object[]>(args => args.Length >= 2 && args[1].ToString() == error),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task TaskFailed_WithRetryableFlag_SendsCorrectFlag()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-failed-retry";
            var error = "Temporary error";
            var isRetryable = true;

            // Act
            await ((ITaskHub)hub).TaskFailed(taskId, error, isRetryable);

            // Assert
            _mockHubContextClientProxy.Verify(
                x => x.SendCoreAsync(
                    "TaskFailed",
                    It.Is<object[]>(args => args.Length >= 3 && (bool)args[2] == true),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion

        #region ITaskHub - TaskCancelled Tests

        [Fact]
        public async Task TaskCancelled_SendsToTaskGroup()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-cancelled-1";
            var reason = "User requested cancellation";

            // Act
            await ((ITaskHub)hub).TaskCancelled(taskId, reason);

            // Assert
            _mockHubContextClients.Verify(x => x.Group(TaskGroup(taskId)), Times.Once());
            _mockHubContextClientProxy.Verify(
                x => x.SendCoreAsync("TaskCancelled", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task TaskCancelled_WithNullReason_SendsToTaskGroup()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-cancelled-null";

            // Act
            await ((ITaskHub)hub).TaskCancelled(taskId, null);

            // Assert
            _mockHubContextClients.Verify(x => x.Group(TaskGroup(taskId)), Times.Once());
        }

        #endregion

        #region ITaskHub - TaskTimedOut Tests

        [Fact]
        public async Task TaskTimedOut_SendsToTaskGroup()
        {
            // Arrange
            var hub = CreateHub();
            var taskId = "task-timeout-1";
            var timeoutSeconds = 300;

            // Act
            await ((ITaskHub)hub).TaskTimedOut(taskId, timeoutSeconds);

            // Assert
            _mockHubContextClients.Verify(x => x.Group(TaskGroup(taskId)), Times.Once());
            _mockHubContextClientProxy.Verify(
                x => x.SendCoreAsync(
                    "TaskTimedOut",
                    It.Is<object[]>(args => args.Length >= 2 && (int)args[1] == timeoutSeconds),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion

        #region Constructor Tests

        [Fact]
        public void Constructor_WithNullTaskService_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new TaskHub(_mockLogger.Object, null!, _mockHubContext.Object, MockServiceProvider.Object));
        }

        [Fact]
        public void Constructor_WithNullHubContext_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new TaskHub(_mockLogger.Object, _mockTaskService.Object, null!, MockServiceProvider.Object));
        }

        #endregion
    }
}
