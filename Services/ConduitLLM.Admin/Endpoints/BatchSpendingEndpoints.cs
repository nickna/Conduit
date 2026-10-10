using ConduitLLM.Core.OpenApi;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Configuration.Events;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Extensions;

using Microsoft.AspNetCore.Mvc;
using ConduitLLM.Admin.DTOs;

namespace ConduitLLM.Admin.Endpoints;

public static class BatchSpendingEndpoints
{
    public static IEndpointRouteBuilder MapBatchSpendingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/batch-spending-jobs")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("BatchSpending");
        group.MapPost("/flush", Flush).WithName("BatchSpending_Flush")
            .WithParameterDescription("priority", "Flush priority; Normal is the default and High requests urgent processing.")
            .WithParameterDescription("timeoutSeconds", "Optional flush timeout in seconds, from 1 to 300.")
            .WithParameterDescription("includeStatistics", "Include processing statistics in the flush request.")
            .WithSummary("Request a flush of pending spending updates")
            .WithDescription("Publishes a flush request to the event bus. Acceptance does not mean all pending spending updates have completed.")
            .WithParameterDescription("reason", "Optional reason recorded for the requested operation.")
            .Produces<BatchSpendingFlushResponse>(StatusCodes.Status202Accepted)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        group.MapGet("/status", GetStatus).WithName("BatchSpending_GetStatus")
            .WithSummary("Get batch spending system status").Produces<BatchSpendingStatusResponse>();
        group.MapGet("/info", GetInformation).WithName("BatchSpending_GetInformation")
            .WithSummary("Get batch spending administration information").Produces<BatchSpendingInformationResponse>();
        return app;
    }

    private static async Task<IResult> Flush(
        [FromServices] IEventBus eventBus,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        [FromQuery] string? reason = null,
        [FromQuery] FlushPriority priority = FlushPriority.Normal,
        [FromQuery] int? timeoutSeconds = null,
        [FromQuery] bool includeStatistics = true)
    {
        var requestId = Guid.NewGuid().ToString();
        var logger = Logger(loggerFactory);
        logger.LogInformation(
            "Admin requesting batch spend flush - RequestId: {RequestId}, Reason: {Reason}, Priority: {Priority}",
            requestId, reason ?? "Administrative operation", priority);
        if (timeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentException("Timeout must be between 1 and 300 seconds");
        }

        var flushEvent = new BatchSpendFlushRequestedEvent
        {
            RequestId = requestId,
            RequestedBy = "Admin",
            RequestedAt = DateTime.UtcNow,
            Reason = reason ?? "Administrative flush operation",
            Source = "Admin API",
            Priority = priority,
            TimeoutSeconds = timeoutSeconds,
            IncludeStatistics = includeStatistics
        };
        await eventBus.PublishAsync(flushEvent);
        AdminAudit.Log(httpContext, logger, "Flushed", "BatchSpending",
            detail: $"RequestId: {requestId}, Priority: {priority}, Reason: {LoggingSanitizer.S(flushEvent.Reason)}");
        return Results.Accepted(value: new BatchSpendingFlushResponse(
            true,
            "Batch spend flush request submitted successfully",
            requestId,
            flushEvent.RequestedAt,
            priority.ToString(),
            timeoutSeconds.HasValue
                ? $"Up to {timeoutSeconds} seconds"
                : "Based on service configuration",
            "This is an asynchronous operation. Monitor logs for completion status."));
    }

    private static IResult GetStatus([FromServices] IEventBus eventBus) => Results.Ok(
        new BatchSpendingStatusResponse(
            true,
            "healthy",
            eventBus is not null,
            eventBus is not null,
            [
                "flush - Trigger immediate batch spend processing",
                "status - Get system status information"
            ],
            new BatchSpendingArchitectureResponse(
                "Event-driven with Wolverine",
                "Publishes BatchSpendFlushRequestedEvent",
                "Consumes events and performs actual flush operations",
                "Admin and Gateway APIs communicate via events only"),
            DateTime.UtcNow));

    private static IResult GetInformation() => Results.Ok(
        new BatchSpendingInformationResponse(
            "Batch Spending Administration",
            "Administrative interface for managing batch spend update operations",
            new BatchSpendingEndpointsInfo(
                new BatchSpendingEndpointInfo(
                    "POST",
                    "/api/batch-spending/flush",
                    "Triggers immediate processing of pending spend updates",
                    new BatchSpendingParameterInfo(
                        "Optional reason for audit trail",
                        "Normal (default) or High for urgent operations",
                        "Optional timeout (1-300 seconds)",
                        "Include detailed stats in logs (default: true)"),
                    [
                        "Integration testing - Deterministic billing verification",
                        "Administrative reconciliation - Manual financial updates",
                        "Maintenance operations - Pre-deployment charge processing",
                        "Emergency scenarios - Immediate spending updates"
                    ]),
                new BatchSpendingEndpointInfo(
                    "GET",
                    "/api/batch-spending/status",
                    "Gets system status and event publishing capability")),
            new BatchSpendingInformationArchitecture(
                "Event-driven architecture with Wolverine",
                "Master key authentication required",
                "Asynchronous processing with error handling and retry policies",
                "Full audit trail via structured logging"),
            [
                "All operations are asynchronous and event-driven",
                "Flush requests are processed by the Gateway API batch spending service",
                "Monitor application logs for detailed operation results",
                "High priority requests are processed with elevated logging",
                "Failed operations include detailed error information in logs"
            ],
            DateTime.UtcNow));

    private static ILogger Logger(ILoggerFactory factory) =>
        factory.CreateLogger("ConduitLLM.Admin.Endpoints.BatchSpending");
}
