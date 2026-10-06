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
            .WithName("Webhooks_Inspect");
        group.MapGet("/backlog", async (IWebhookRecovery recovery, CancellationToken ct) => Results.Ok(await recovery.BacklogAsync(ct)))
            .WithName("Webhooks_Backlog");
        group.MapGet("/dead-letters", async (IWebhookRecovery recovery, int? limit, CancellationToken ct) =>
            Results.Ok(await recovery.DeadLettersAsync(limit ?? 50, ct))).WithName("Webhooks_DeadLetters");
        group.MapPost("/{id}/replay", Replay).WithName("Webhooks_Replay");
        group.MapPost("/purge", async (IWebhookRecovery recovery, int? limit, CancellationToken ct) =>
            Results.Ok(await recovery.PurgeAsync(limit ?? 100, ct))).WithName("Webhooks_Purge");
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
