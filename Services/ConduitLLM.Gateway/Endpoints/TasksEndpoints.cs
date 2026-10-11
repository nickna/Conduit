using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Gateway.Endpoints
{
    /// <summary>
    /// API controller for managing asynchronous tasks.
    /// </summary>
    public class TasksEndpoints : GatewayEndpointHandlerBase
    {
        private readonly IAsyncTaskService _taskService;

        /// <summary>
        /// Initializes the Tasks endpoint handler.
        /// </summary>
        /// <param name="taskService">The async task service.</param>
        /// <param name="httpContextAccessor">Accessor for the current request context.</param>
        /// <param name="logger">The logger.</param>
        public TasksEndpoints(IAsyncTaskService taskService, IHttpContextAccessor httpContextAccessor, ILogger<TasksEndpoints> logger)
            : base(null, httpContextAccessor, logger)
        {
            _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        }

        /// <summary>
        /// Gets the status of a specific task.
        /// </summary>
        /// <param name="taskId">The ID of the task to retrieve.</param>
        /// <returns>The task status.</returns>
        public async Task<IResult> GetTaskStatus(string taskId)
        {
            if (CurrentVirtualKeyId is not int virtualKeyId)
            {
                return OpenAIError(401, "Virtual key not found in request context", "unauthorized");
            }

            Logger.LogDebug("Getting status for task {TaskId}", taskId);
            try
            {
                var status = await _taskService.GetTaskStatusAsync(taskId);
                if (!AsyncTaskOwnership.IsOwnedBy(status, virtualKeyId))
                {
                    LogOwnershipFailure(taskId, virtualKeyId, status);
                    return TaskNotFound();
                }
                return Ok(CustomerStatus(status!));
            }
            catch (InvalidOperationException ex)
            {
                return OpenAIError(404, ex.Message, "not_found", "not_found_error");
            }
        }

        /// <summary>
        /// Cancels a running task.
        /// </summary>
        /// <param name="taskId">The ID of the task to cancel.</param>
        /// <returns>No content on success.</returns>
        public async Task<IResult> CancelTask(string taskId)
        {
            if (CurrentVirtualKeyId is not int virtualKeyId)
            {
                return OpenAIError(401, "Virtual key not found in request context", "unauthorized");
            }

            try
            {
                var status = await _taskService.GetTaskStatusAsync(taskId);
                if (!AsyncTaskOwnership.IsOwnedBy(status, virtualKeyId))
                {
                    LogOwnershipFailure(taskId, virtualKeyId, status);
                    return TaskNotFound();
                }

                await _taskService.CancelTaskAsync(taskId);
                Logger.LogInformation("Task {TaskId} cancelled successfully", taskId);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return OpenAIError(404, ex.Message, "not_found", "not_found_error");
            }
        }

        /// <summary>
        /// Polls a task until it completes or times out.
        /// </summary>
        /// <param name="taskId">The ID of the task to poll.</param>
        /// <param name="timeout">Maximum time to wait in seconds (default: 300, max: 600).</param>
        /// <param name="interval">Polling interval in seconds (default: 2, min: 1).</param>
        /// <returns>The final task status.</returns>
        public async Task<IResult> PollTask(string taskId, int timeout = 300, int interval = 2)
        {
            if (CurrentVirtualKeyId is not int virtualKeyId)
            {
                return OpenAIError(401, "Virtual key not found in request context", "unauthorized");
            }

            // Validate and clamp parameters
            timeout = Math.Clamp(timeout, 1, 600); // Max 10 minutes
            interval = Math.Max(interval, 1); // Min 1 second

            Logger.LogDebug("Polling task {TaskId} with timeout {TimeoutSeconds}s, interval {IntervalSeconds}s",
                taskId, timeout, interval);

            try
            {
                var initialStatus = await _taskService.GetTaskStatusAsync(taskId);
                if (!AsyncTaskOwnership.IsOwnedBy(initialStatus, virtualKeyId))
                {
                    LogOwnershipFailure(taskId, virtualKeyId, initialStatus);
                    return TaskNotFound();
                }

                var status = await _taskService.PollTaskUntilCompletedAsync(
                    taskId,
                    TimeSpan.FromSeconds(interval),
                    TimeSpan.FromSeconds(timeout));

                return Ok(CustomerStatus(status));
            }
            catch (InvalidOperationException ex)
            {
                return OpenAIError(404, ex.Message, "not_found", "not_found_error");
            }
            catch (OperationCanceledException)
            {
                return OpenAIError(408, "Task polling timed out", "timeout", "timeout");
            }
        }

        // Keep operator evidence, provider credential identity and signed recovery
        // references private without mutating the task service's cached status.
        private static AsyncTaskStatus CustomerStatus(AsyncTaskStatus status) => new()
        {
            TaskId = status.TaskId, TaskType = status.TaskType, State = status.State,
            CreatedAt = status.CreatedAt, UpdatedAt = status.UpdatedAt, CompletedAt = status.CompletedAt,
            Result = status.Result, Error = status.Error, Progress = status.Progress, ProgressMessage = status.ProgressMessage,
            RetryCount = status.RetryCount, MaxRetries = status.MaxRetries, IsRetryable = status.IsRetryable, NextRetryAt = status.NextRetryAt,
            Metadata = status.Metadata is { } metadata ? new TaskMetadata(metadata.VirtualKeyId)
            {
                Model = metadata.Model, Prompt = metadata.Prompt, CorrelationId = metadata.CorrelationId,
                Payload = metadata.Payload, VideoId = metadata.VideoId, WebhookUrl = metadata.WebhookUrl,
                WebhookHeaders = metadata.WebhookHeaders, Priority = metadata.Priority, ExtensionData = metadata.ExtensionData
            } : null
        };

        private IResult TaskNotFound() =>
            OpenAIError(404, "The requested task was not found", "not_found", "not_found_error");

        private void LogOwnershipFailure(
            string taskId,
            int virtualKeyId,
            AsyncTaskStatus? taskStatus)
        {
            if (taskStatus is not null)
            {
                Logger.LogWarning(
                    "Virtual key {VirtualKeyId} attempted to access task {TaskId} owned by {OwnerKeyId}",
                    virtualKeyId,
                    taskId,
                    taskStatus.Metadata?.VirtualKeyId);
            }
        }

    }
}
