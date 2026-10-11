using ConduitLLM.Core.OpenApi;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

public static class TasksEndpoints
{
    public static IEndpointRouteBuilder MapAdminTasksEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/tasks")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Tasks");
        group.MapPost("/cleanup", Cleanup).WithName("Tasks_Cleanup")
            .WithSummary("Archive and delete old asynchronous tasks")
            .WithDescription("Archives completed tasks older than the requested threshold and deletes records according to task retention policy. The response retains the legacy cleaned_up and older_than_hours field names.")
            .WithParameterDescription("olderThanHours", "Task age threshold in hours; zero selects the 24-hour default and negative values are clamped to 1.")
            .Produces<TaskCleanupResponseDto>(StatusCodes.Status200OK);
        group.MapGet("/", List).WithName("Tasks_List")
            .WithDescription("Lists tasks in the indeterminate state for operator review. Other state filters are rejected.")
            .WithParameterDescription("state", "Execution or task state to filter by.")
            .WithParameterDescription("page", "Page number, starting at 1.")
            .WithParameterDescription("pageSize", "Number of records per page; values are normalized to the supported pagination range.")
            .WithSummary("List indeterminate async media tasks")
            .Produces<PagedResult<IndeterminateTaskDto>>(StatusCodes.Status200OK)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json");
        group.MapPost("/{taskId}/resolve", Resolve).WithName("Tasks_ResolveIndeterminate")
            .WithSummary("Resolve an indeterminate asynchronous task")
            .WithDescription("Records retry, failed_no_charge, or completed_no_charge. Recovery requires verified original provider output and a stable dispatch ID; it imports media without repeating generation or charging the customer. Returns 202 for durable dispatch, 204 for failure resolution, or 409 for a competing decision.")
            .WithParameterDescription("taskId", "Identifier returned when the asynchronous task was accepted.")
            .Produces<TaskResolutionAcceptedDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        return app;
    }

    private static async Task<IResult> List(
        [FromServices] IAsyncTaskService taskService,
        [FromQuery] string state = TaskStateConstants.Indeterminate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Pagination.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(state, TaskStateConstants.Indeterminate, StringComparison.OrdinalIgnoreCase))
        {
            return AdminResults.BadRequest("Only state=indeterminate is currently supported");
        }

        (page, pageSize) = Pagination.Normalize(page, pageSize);
        var result = await taskService.GetTasksByStateAsync(
            TaskState.Indeterminate, page, pageSize, cancellationToken);
        return Results.Ok(new PagedResult<IndeterminateTaskDto>
        {
            Data = result.Tasks.Select(task => new IndeterminateTaskDto(
                task.TaskId,
                task.TaskType,
                TaskStateConstants.FromTaskState(task.State),
                task.VirtualKeyId,
                task.Model,
                task.CreatedAt,
                task.UpdatedAt,
                task.CompletedAt,
                task.Error,
                task.RetryCount,
                task.MaxRetries,
                task.ProviderInvocationStartedAt,
                task.ProviderInvocationCompletedAt,
                task.ProviderOperationId, task.ProviderId, task.ProviderModelId,
                task.ReconciliationDispatchId, task.ReconciliationOutcome, task.BillingDisposition, task.ProviderCost)).ToList(),
            Pagination = PaginationMetadata.Create(page, pageSize, result.TotalCount)
        });
    }

    private static async Task<IResult> Cleanup(
        int olderThanHours,
        [FromServices] IAsyncTaskService taskService,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        olderThanHours = olderThanHours == 0 ? 24 : Math.Max(olderThanHours, 1);
        var result = await taskService.CleanupOldTasksAsync(new AsyncTaskRetentionPolicy(
            TimeSpan.FromHours(olderThanHours),
            TimeSpan.FromDays(30),
            TimeSpan.FromDays(7)));
        AdminAudit.Log(context, Logger(loggerFactory), "CleanedUp", "Tasks", detail:
            $"Archived {result.Archived} and deleted {result.Deleted} tasks");
        return Results.Ok(new TaskCleanupResponseDto
        {
            CleanedUp = result.Total,
            Archived = result.Archived,
            Deleted = result.Deleted,
            OlderThanHours = olderThanHours
        });
    }

    private static async Task<IResult> Resolve(
        string taskId,
        ResolveIndeterminateTaskDto request,
        [FromServices] IAsyncTaskService taskService,
        [FromServices] IEventBus eventBus,
        [FromServices] IMediaTaskReconciliationStore reconciliations,
        HttpContext context,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var resolution = (request.Resolution ?? "").Trim().ToLowerInvariant();
        if (resolution is not ("retry" or "failed_no_charge" or "completed_no_charge"))
        {
            return AdminResults.BadRequest("Resolution must be retry, failed_no_charge, or completed_no_charge");
        }
        if (string.IsNullOrWhiteSpace(request.Reason)) return AdminResults.BadRequest("A reason is required");
        var logger = Logger(loggerFactory);
        if (resolution == "completed_no_charge")
        {
            var decision = new MediaTaskReconciliation
            {
                DispatchId = request.DispatchId?.ToString("N") ?? "", VirtualKeyId = request.VirtualKeyId ?? 0,
                ProviderId = request.ProviderId ?? 0, Model = request.Model ?? "", ProviderModelId = request.ProviderModelId ?? "",
                ProviderOperationId = request.ProviderOperationId ?? "", Evidence = request.Evidence ?? "",
                Reason = request.Reason, ResultUrls = request.ResultUrls ?? [], ProviderCost = request.ProviderCost,
                RequestedBy = context.User.Identity?.Name ?? "Admin"
            };
            if (decision.ValidationError() is { } error) return AdminResults.BadRequest(error);
            var acceptance = await reconciliations.AcceptAsync(taskId, decision, cancellationToken);
            if (acceptance == MediaReconciliationAcceptance.Missing) return AdminResults.NotFound("Task was not found");
            if (acceptance == MediaReconciliationAcceptance.Invalid) return AdminResults.BadRequest("Recovery evidence does not match the original task or its requested output count");
            if (acceptance == MediaReconciliationAcceptance.Conflict) return AdminResults.Conflict("A competing resolution exists or the task is no longer indeterminate");
            AdminAudit.Log(context, logger, "RecoveryRequested", "AsyncTask", taskId,
                $"DispatchId: {decision.DispatchId}; BillingDisposition: no_customer_charge; Reason: {request.Reason}");
            return Results.Accepted(value: new TaskResolutionAcceptedDto(taskId, resolution, decision.DispatchId, decision.RequestedAt));
        }
        if (request.DispatchId != null || request.ResultUrls != null || request.Evidence != null || request.ProviderCost != null ||
            request.VirtualKeyId != null || request.ProviderId != null || request.ProviderModelId != null || request.Model != null)
            return AdminResults.BadRequest("Recovery evidence fields are only accepted with completed_no_charge");

        var current = await taskService.GetTaskStatusAsync(taskId, cancellationToken);
        if (current?.State != TaskState.Indeterminate)
        {
            return AdminResults.NotFound("Indeterminate task was not found", "not_found");
        }

        if (current.Metadata?.Reconciliation?.Outcome == "accepted")
            return AdminResults.Conflict("An accepted media recovery is already in progress");
        if (resolution == "failed_no_charge")
        {
            var updated = await taskService.FailIndeterminateTaskWithoutChargeAsync(
                taskId, request.Reason, request.ProviderOperationId, cancellationToken);
            if (!updated) return AdminResults.NotFound("Indeterminate task was not found", "not_found");
            AdminAudit.Log(context, logger, "ResolvedNoCharge", "AsyncTask", taskId,
                $"Resolution: failed_no_charge; Reason: {request.Reason}");
            return Results.NoContent();
        }

        if (current.TaskType is not ("image_generation" or "video_generation"))
        {
            return AdminResults.BadRequest("Only image and video generation tasks can be retried");
        }
        if (current.RetryCount >= current.MaxRetries)
        {
            return AdminResults.BadRequest("The task has reached its retry limit");
        }
        if (!string.IsNullOrWhiteSpace(request.ProviderOperationId))
        {
            return AdminResults.BadRequest(
                "providerOperationId is only accepted with failed_no_charge or completed_no_charge");
        }

        var dispatchId = Guid.NewGuid().ToString("N");
        var acceptedAt = DateTime.UtcNow;
        await eventBus.PublishAsync(new IndeterminateMediaTaskRetryRequested
        {
            TaskId = taskId,
            DispatchId = dispatchId,
            Reason = request.Reason,
            RequestedBy = context.User.Identity?.Name ?? "Admin",
            RequestedAt = acceptedAt,
            CorrelationId = context.TraceIdentifier
        }, cancellationToken);
        AdminAudit.Log(context, logger, "RetryRequested", "AsyncTask", taskId,
            $"DispatchId: {dispatchId}; Reason: {request.Reason}");
        return Results.Accepted(value: new TaskResolutionAcceptedDto(
            taskId, resolution, dispatchId, acceptedAt));
    }

    private static ILogger Logger(ILoggerFactory factory) =>
        factory.CreateLogger("ConduitLLM.Admin.Endpoints.Tasks");
}
