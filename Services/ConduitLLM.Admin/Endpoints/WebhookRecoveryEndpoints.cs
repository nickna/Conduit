using System.Security.Claims;
using ConduitLLM.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

public static class WebhookRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapWebhookRecoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/webhook-deliveries").RequireAuthorization("MasterKeyPolicy")
            .WithTags("Webhook recovery");
        group.MapGet("/", async ([FromServices] IWebhookRecovery recovery, int? owner, string? taskId, string? eventId,
            int? limit, CancellationToken ct) => Results.Ok(await recovery.InspectAsync(owner, taskId, eventId, limit ?? 50, ct)))
            .WithName("Webhooks_Inspect").WithSummary("Inspect retained webhook delivery receipts")
            .Produces<List<WebhookDeliveryInspection>>().Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status429TooManyRequests);
        group.MapGet("/backlog", async (IWebhookRecovery recovery, CancellationToken ct) => Results.Ok(await recovery.BacklogAsync(ct)))
            .WithName("Webhooks_Backlog").WithSummary("Read the retained webhook delivery backlog")
            .Produces<WebhookBacklog>().Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status429TooManyRequests);
        group.MapGet("/dead-letters", async (IWebhookRecovery recovery, int? limit, CancellationToken ct) =>
            Results.Ok(await recovery.DeadLettersAsync(limit ?? 50, ct))).WithName("Webhooks_DeadLetters")
            .WithSummary("List exhausted webhook error-envelope references").Produces<List<WebhookDeadLetterInspection>>()
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);
        group.MapPost("/{id}/replay", Replay).WithName("Webhooks_Replay")
            .WithSummary("Start one audited replay cycle for an exhausted webhook delivery")
            .Produces<WebhookReplayResult>().Produces<WebhookReplayResult>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);
        group.MapPost("/purge", async (IWebhookRecovery recovery, int? limit, CancellationToken ct) =>
            Results.Ok(await recovery.PurgeAsync(limit ?? 100, ct))).WithName("Webhooks_Purge")
            .WithSummary("Purge a bounded batch of expired webhook receipts and audit").Produces<int>()
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);
        return app;
    }

    private static async Task<IResult> Replay(string id, WebhookReplayRequest request, IWebhookRecovery recovery,
        HttpContext context, CancellationToken ct)
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.Identity?.Name ?? "master-key";
        var result = await recovery.ReplayAsync(id, request, actor, ct);
        return result.Outcome switch
        {
            "Accepted" or "AlreadyRequested" => Results.Ok(result),
            "NotFound" => AdminResults.NotFound("Delivery was not found for the specified owner", "not_found"),
            "Invalid" => AdminResults.BadRequest("A nonempty operation ID, owner, and expected cycle are required"),
            _ => Results.Conflict(result)
        };
    }
}
