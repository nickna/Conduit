using System.Text.Json;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Gateway.Serialization;

using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Gateway.EventHandlers;

/// <summary>
/// Converts an operator-approved reconciliation command into the original media
/// request. The dispatch ID makes command redelivery safe if the process stops
/// after updating the task but before Wolverine flushes the follow-on event.
/// </summary>
public sealed class IndeterminateMediaTaskRetryRequestedHandler
    : IEventHandler<IndeterminateMediaTaskRetryRequested>
{
    private readonly IAsyncTaskService _taskService;
    private readonly ILogger<IndeterminateMediaTaskRetryRequestedHandler> _logger;

    public IndeterminateMediaTaskRetryRequestedHandler(
        IAsyncTaskService taskService,
        ILogger<IndeterminateMediaTaskRetryRequestedHandler> logger)
    {
        _taskService = taskService;
        _logger = logger;
    }

    public async Task HandleAsync(
        IndeterminateMediaTaskRetryRequested request,
        IEventContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DispatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        // Validate and reconstruct before changing state. A malformed historical
        // payload must remain visible as indeterminate instead of becoming a dead
        // pending task.
        var task = await _taskService.GetTaskStatusAsync(request.TaskId, context.CancellationToken);
        if (task == null)
        {
            _logger.LogWarning(
                "Ignoring media retry command {DispatchId}; task {TaskId} no longer exists",
                request.DispatchId, request.TaskId);
            return;
        }
        var retryEvent = BuildRetryEvent(task);

        var preparation = await _taskService.PrepareIndeterminateTaskRetryAsync(
            request.TaskId,
            request.DispatchId,
            request.Reason,
            context.CancellationToken);
        if (preparation.Status is not (
            MediaTaskRetryPreparationStatus.Prepared or
            MediaTaskRetryPreparationStatus.AlreadyPrepared))
        {
            _logger.LogWarning(
                "Ignoring media retry command {DispatchId} for task {TaskId}; preparation result was {Status}",
                request.DispatchId, request.TaskId, preparation.Status);
            return;
        }

        if (retryEvent.Image != null)
        {
            await context.PublishAsync(retryEvent.Image, context.CancellationToken);
        }
        else
        {
            await context.PublishAsync(retryEvent.Video!, context.CancellationToken);
        }

        if (preparation.Status == MediaTaskRetryPreparationStatus.Prepared)
        {
            MediaTaskIdempotencyMetrics.RecordOperatorRetry(task.TaskType);
        }

        _logger.LogWarning(
            "Dispatched operator-approved retry {DispatchId} for indeterminate {TaskType} task {TaskId}",
            request.DispatchId, task.TaskType, request.TaskId);
    }

    private static RetryEvent BuildRetryEvent(AsyncTaskStatus task)
    {
        var metadata = task.Metadata ?? throw new InvalidOperationException(
            $"Indeterminate media task {task.TaskId} has no persisted request metadata.");

        return task.TaskType switch
        {
            "image_generation" => new RetryEvent(BuildImageEvent(task.TaskId, metadata), null),
            "video_generation" => new RetryEvent(null, BuildVideoEvent(task.TaskId, metadata)),
            _ => throw new InvalidOperationException(
                $"Task {task.TaskId} has unsupported media task type '{task.TaskType}'.")
        };
    }

    private static ImageGenerationRequested BuildImageEvent(string taskId, TaskMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.Payload))
        {
            throw new InvalidOperationException(
                $"Indeterminate image task {taskId} has no persisted request payload.");
        }
        var request = JsonSerializer.Deserialize(
                metadata.Payload,
                CoreMessagingJsonContext.Default.ImageGenerationRequested)
            ?? throw new InvalidOperationException(
                $"Indeterminate image task {taskId} has an invalid request payload.");
        return request with
        {
            TaskId = taskId,
            RequestedAt = DateTime.UtcNow,
            CorrelationId = metadata.CorrelationId ?? taskId
        };
    }

    private static VideoGenerationRequested BuildVideoEvent(string taskId, TaskMetadata metadata)
    {
        VideoGenerationRequest? videoRequest = null;
        if (!string.IsNullOrWhiteSpace(metadata.Payload))
        {
            videoRequest = JsonSerializer.Deserialize(
                metadata.Payload,
                CoreHttpJsonContext.Default.VideoGenerationRequest);
        }
        if (videoRequest == null &&
            metadata.ExtensionData?.TryGetValue("Request", out var storedRequest) == true)
        {
            videoRequest = storedRequest is JsonElement element
                ? element.Deserialize(CoreHttpJsonContext.Default.VideoGenerationRequest)
                : JsonSerializer.Deserialize(
                    JsonSerializer.Serialize(storedRequest, CoreHttpJsonContext.Default.Object),
                    CoreHttpJsonContext.Default.VideoGenerationRequest);
        }
        if (videoRequest == null)
        {
            throw new InvalidOperationException(
                $"Indeterminate video task {taskId} has no valid persisted request payload.");
        }

        return new VideoGenerationRequested
        {
            RequestId = taskId,
            Request = videoRequest,
            VirtualKeyId = metadata.VirtualKeyId.ToString(),
            IsAsync = true,
            RequestedAt = DateTime.UtcNow,
            CorrelationId = metadata.CorrelationId ?? taskId,
            WebhookUrl = metadata.WebhookUrl ?? videoRequest.WebhookUrl,
            WebhookHeaders = metadata.WebhookHeaders ?? videoRequest.WebhookHeaders
        };
    }

    private sealed record RetryEvent(
        ImageGenerationRequested? Image,
        VideoGenerationRequested? Video);
}
