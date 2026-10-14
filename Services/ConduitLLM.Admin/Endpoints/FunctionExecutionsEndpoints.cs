using ConduitLLM.Core.OpenApi;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Functions.DTOs;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Core.Extensions;

using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

public static class FunctionExecutionsEndpoints
{
    public static IEndpointRouteBuilder MapFunctionExecutionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/function-executions")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("FunctionExecutions");

        group.MapGet("/{id:guid}", GetById).WithName("FunctionExecutions_GetById")
            .WithSummary("Get a function execution")
            .WithParameterDescription("id", "Identifier of the FunctionExecutions resource.")
            .Produces<AdminFunctionExecutionDto>().Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapGet("/virtual-keys/{virtualKeyId:int}", GetByVirtualKey)
            .WithName("FunctionExecutions_GetByVirtualKey")
            .WithSummary("List function executions for a virtual key")
            .WithParameterDescription("virtualKeyId", "Identifier of the virtual key.").Produces<List<AdminFunctionExecutionDto>>();
        group.MapGet("/configuration/{functionConfigurationId:int}", GetByConfiguration)
            .WithName("FunctionExecutions_GetByConfiguration")
            .WithSummary("List function executions for a configuration")
            .WithParameterDescription("functionConfigurationId", "Identifier of the function configuration.").Produces<List<AdminFunctionExecutionDto>>();
        group.MapGet("/state/{state}", GetByState).WithName("FunctionExecutions_GetByState")
            .WithSummary("List function executions in a given state")
            .WithParameterDescription("state", "Execution or task state to filter by.")
            .Produces<List<AdminFunctionExecutionDto>>().Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        group.MapGet("/expired-leases", GetExpiredLeases).WithName("FunctionExecutions_GetExpiredLeases")
            .WithSummary("List function executions with expired leases")
            .Produces<List<AdminFunctionExecutionDto>>();
        group.MapGet("/ready-for-retry", GetReadyForRetry).WithName("FunctionExecutions_GetReadyForRetry")
            .WithSummary("List function executions ready for retry")
            .Produces<List<AdminFunctionExecutionDto>>();
        group.MapDelete("/cleanup", Cleanup).WithName("FunctionExecutions_Cleanup")
            .WithSummary("Delete old function execution records")
            .WithParameterDescription("olderThanDays", "Minimum record age in days for cleanup.")
            .Produces<FunctionExecutionCleanupResultDto>().Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        return app;
    }

    private static async Task<IResult> GetById(Guid id, [FromServices] IFunctionExecutionRepository repository)
    {
        var execution = await repository.GetByIdAsync(id);
        return execution is null
            ? AdminResults.NotFoundEntity("Function execution", id)
            : Results.Ok(execution.ToDto());
    }

    private static async Task<IResult> GetByVirtualKey(int virtualKeyId, [FromServices] IFunctionExecutionRepository repository)
    {
        var executions = await repository.GetByVirtualKeyIdAsync(virtualKeyId);
        return Results.Ok(executions.Select(execution => execution.ToDto()).ToList());
    }

    private static async Task<IResult> GetByConfiguration(
        int functionConfigurationId,
        [FromServices] IFunctionExecutionRepository repository)
    {
        var executions = await repository.GetByFunctionConfigurationIdAsync(functionConfigurationId);
        return Results.Ok(executions.Select(execution => execution.ToDto()).ToList());
    }

    private static async Task<IResult> GetByState(string state, [FromServices] IFunctionExecutionRepository repository)
    {
        if (!Enum.TryParse<ExecutionState>(state, true, out var parsedState))
        {
            return AdminResults.BadRequest($"Invalid execution state: {state}");
        }

        var executions = await repository.GetByStateAsync(parsedState);
        return Results.Ok(executions.Select(execution => execution.ToDto()).ToList());
    }

    private static async Task<IResult> GetExpiredLeases([FromServices] IFunctionExecutionRepository repository)
    {
        var executions = await repository.GetExpiredLeasesAsync();
        return Results.Ok(executions.Select(execution => execution.ToDto()).ToList());
    }

    private static async Task<IResult> GetReadyForRetry([FromServices] IFunctionExecutionRepository repository)
    {
        var executions = await repository.GetReadyForRetryAsync();
        return Results.Ok(executions.Select(execution => execution.ToDto()).ToList());
    }

    private static async Task<IResult> Cleanup(
        [FromServices] IFunctionExecutionRepository repository,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        [FromQuery] int olderThanDays = 30)
    {
        if (olderThanDays < 1)
        {
            return AdminResults.BadRequest("olderThanDays must be at least 1");
        }

        var deletedCount = await repository.DeleteOldExecutionsAsync(DateTime.UtcNow.AddDays(-olderThanDays));
        AdminAudit.Log(
            httpContext,
            loggerFactory.CreateLogger("ConduitLLM.Admin.Endpoints.FunctionExecutions"),
            "Cleanup",
            "FunctionExecution",
            detail: $"OlderThanDays: {olderThanDays}, DeletedCount: {deletedCount}");
        return Results.Ok(new FunctionExecutionCleanupResultDto
        {
            DeletedCount = deletedCount,
            Message = $"Deleted {deletedCount} executions older than {olderThanDays} days"
        });
    }
}
