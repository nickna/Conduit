using ConduitLLM.Gateway.Utilities;
using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Gateway.Hubs
{
    /// <summary>
    /// Unified SignalR hub for tracking all types of async operations in Conduit.
    /// Provides real-time updates for task lifecycle events with virtual key authentication.
    /// </summary>
    public class TaskHub : SecureHub, ITaskHub
    {
        private readonly IAsyncTaskService _taskService;
        private readonly IHubContext<TaskHub> _hubContext;

        public TaskHub(
            ILogger<TaskHub> logger,
            IAsyncTaskService taskService,
            IHubContext<TaskHub> hubContext,
            IServiceProvider serviceProvider)
            : base(logger, serviceProvider)
        {
            _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        }

        protected override string GetHubName() => "TaskHub";

        /// <summary>
        /// Subscribe to updates for a specific task
        /// </summary>
        public async Task SubscribeToTask(string taskId)
        {
            var virtualKeyId = RequireVirtualKeyId();
            
            // Validate task ID format
            if (string.IsNullOrWhiteSpace(taskId))
            {
                throw new HubException("Invalid task ID");
            }
            
            try
            {
                // Verify task ownership with error boundary
                var taskStatus = await _taskService.GetTaskStatusAsync(taskId);
                if (taskStatus == null)
                {
                    Logger.LogWarning("Virtual Key {KeyId} attempted to subscribe to non-existent task {TaskId}", 
                        virtualKeyId, taskId);
                    throw new HubException("Task not found");
                }
                
                // Extract virtual key ID from task metadata
                if (!VerifyTaskOwnership(taskStatus, virtualKeyId))
                {
                    Logger.LogWarning("Virtual Key {KeyId} attempted to subscribe to task {TaskId} owned by another key", 
                        virtualKeyId, taskId);
                    throw new HubException("Unauthorized access to task");
                }
                
                await Groups.AddToGroupAsync(Context.ConnectionId, $"task-{taskId}");
                Logger.LogDebug("Virtual Key {KeyId} subscribed to task {TaskId}", virtualKeyId, taskId);
            }
            catch (HubException)
            {
                // Re-throw HubExceptions as they're meant for the client
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error subscribing Virtual Key {KeyId} to task {TaskId}", 
                    virtualKeyId, taskId);
                throw new HubException("Failed to subscribe to task updates. Please try again.");
            }
        }

        /// <summary>
        /// Unsubscribe from updates for a specific task
        /// </summary>
        public async Task UnsubscribeFromTask(string taskId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"task-{taskId}");
            Logger.LogDebug("Client {ConnectionId} unsubscribed from task {TaskId}", 
                Context.ConnectionId, taskId);
        }

        /// <summary>
        /// Subscribe to all tasks of a specific type for the authenticated virtual key
        /// </summary>
        public async Task SubscribeToTaskType(string taskType)
        {
            var virtualKeyId = RequireVirtualKeyId();
            
            await Groups.AddToGroupAsync(Context.ConnectionId, $"vkey-{virtualKeyId}-{taskType}");
            Logger.LogDebug("Virtual Key {KeyId} subscribed to task type {TaskType}", virtualKeyId, taskType);
        }

        /// <summary>
        /// Unsubscribe from all tasks of a specific type
        /// </summary>
        public async Task UnsubscribeFromTaskType(string taskType)
        {
            var virtualKeyId = RequireVirtualKeyId();
            
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"vkey-{virtualKeyId}-{taskType}");
            Logger.LogDebug("Virtual Key {KeyId} unsubscribed from task type {TaskType}", virtualKeyId, taskType);
        }

        // Explicit implementations keep server notifications out of SignalR client method discovery.
        
        async Task ITaskHub.TaskStarted(string taskId, string taskType, object metadata)
        {
            int? virtualKeyId = null;

            // Handle both TaskMetadata and IDictionary formats
            if (metadata is TaskMetadata taskMetadata)
            {
                virtualKeyId = taskMetadata.VirtualKeyId;
            }
            else if (metadata is IDictionary<string, object> metadataDict &&
                metadataDict.TryGetValue("virtualKeyId", out var virtualKeyIdObj))
            {
                virtualKeyId = HubContextHelpers.ConvertToInt(virtualKeyIdObj);
            }

            if (virtualKeyId.HasValue)
            {
                Logger.LogInformation("Task {TaskId} started: type={TaskType}, virtualKey={VirtualKeyId}",
                    taskId, taskType, virtualKeyId);

                // Notify specific task subscribers
                await _hubContext.Clients.Group($"task-{taskId}")
                    .SendAsync("TaskStarted", taskId, taskType, metadata);

                // Notify task type subscribers for this virtual key
                await _hubContext.Clients.Group($"vkey-{virtualKeyId}-{taskType}")
                    .SendAsync("TaskStarted", taskId, taskType, metadata);
            }
            else
            {
                Logger.LogWarning("Task {TaskId} started but no virtual key ID could be extracted from metadata", taskId);
            }
        }

        async Task ITaskHub.TaskProgress(string taskId, int progress, string? message)
        {
            Logger.LogDebug("Task {TaskId} progress: {Progress}%{Message}",
                taskId, progress, message != null ? $" - {message}" : "");
            await _hubContext.Clients.Group($"task-{taskId}")
                .SendAsync("TaskProgress", taskId, progress, message);
        }

        async Task ITaskHub.TaskCompleted(string taskId, object result)
        {
            Logger.LogInformation("Task {TaskId} completed successfully", taskId);
            await _hubContext.Clients.Group($"task-{taskId}")
                .SendAsync("TaskCompleted", taskId, result);
        }

        async Task ITaskHub.TaskFailed(string taskId, string error, bool isRetryable)
        {
            Logger.LogWarning("Task {TaskId} failed (retryable: {IsRetryable}): {Error}",
                taskId, isRetryable, error);
            await _hubContext.Clients.Group($"task-{taskId}")
                .SendAsync("TaskFailed", taskId, error, isRetryable);
        }

        async Task ITaskHub.TaskCancelled(string taskId, string? reason)
        {
            Logger.LogInformation("Task {TaskId} cancelled{Reason}",
                taskId, reason != null ? $": {reason}" : "");
            await _hubContext.Clients.Group($"task-{taskId}")
                .SendAsync("TaskCancelled", taskId, reason);
        }

        async Task ITaskHub.TaskTimedOut(string taskId, int timeoutSeconds)
        {
            Logger.LogWarning("Task {TaskId} timed out after {TimeoutSeconds}s", taskId, timeoutSeconds);
            await _hubContext.Clients.Group($"task-{taskId}")
                .SendAsync("TaskTimedOut", taskId, timeoutSeconds);
        }

        // Helper methods
        
        private bool VerifyTaskOwnership(AsyncTaskStatus taskStatus, int virtualKeyId)
        {
            if (taskStatus.Metadata == null)
            {
                Logger.LogWarning("Task {TaskId} has no metadata", taskStatus.TaskId);
                return false;
            }

            // TaskMetadata is the expected type in AsyncTaskStatus
            return taskStatus.Metadata.VirtualKeyId == virtualKeyId;
        }
    }
}
