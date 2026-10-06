using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
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
            .Produces<TaskCleanupResponseDto>(StatusCodes.Status200OK);
        group.MapGet("/", List).WithName("Tasks_List")
            .WithSummary("List indeterminate async media tasks")
            .Produces<PagedResult<IndeterminateTaskDto>>(StatusCodes.Status200OK)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json");
        group.MapPost("/{taskId}/resolve", Resolve).WithName("Tasks_ResolveIndeterminate")
            .Produces<TaskResolutionAcceptedDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
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
                task.ProviderOperationId)).ToList(),
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
        HttpContext context,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var resolution = request.Resolution.Trim().ToLowerInvariant();
        if (resolution is not ("retry" or "failed_no_charge"))
        {
            return AdminResults.BadRequest("Resolution must be retry or failed_no_charge");
        }

        var current = await taskService.GetTaskStatusAsync(taskId, cancellationToken);
        if (current?.State != TaskState.Indeterminate)
        {
            return AdminResults.NotFound("Indeterminate task was not found", "not_found");
        }

        var logger = Logger(loggerFactory);
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
                "providerOperationId is only accepted with failed_no_charge");
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
