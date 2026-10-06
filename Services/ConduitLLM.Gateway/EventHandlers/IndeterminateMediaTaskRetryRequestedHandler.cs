using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;

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
        var retryEvent = ConduitLLM.Core.Utilities.MediaGenerationCommandReconstruction.Build(task.TaskId, task.TaskType,
            task.Metadata ?? throw new InvalidOperationException("Media task has no persisted metadata."));

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

}
