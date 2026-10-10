using ConduitLLM.Core.Events;

namespace ConduitLLM.Core.Interfaces;

public enum WebhookClaimStatus { Acquired, Busy, Delivered, Exhausted, Obsolete }
public sealed record WebhookClaim(WebhookClaimStatus Status, string Id, Guid Token,
    WebhookDeliveryRequested Request, int Attempts, DateTime Deadline, DateTime? DueAt = null);

/// <summary>Fenced PostgreSQL delivery state. Scheduling commits in the same transaction as state.</summary>
public interface IWebhookDeliveryStore
{
    Task<WebhookClaim> TryClaimAsync(WebhookDeliveryRequested request, DateTime deadline, TimeSpan lease,
        CancellationToken cancellationToken = default);
    Task<int?> BeginAttemptAsync(WebhookClaim claim, CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(WebhookClaim claim, WebhookSendResult result, bool exhausted,
        CancellationToken cancellationToken = default);
    Task<bool> ScheduleAsync(WebhookClaim claim, DateTime dueAt, WebhookSendResult? result = null,
        CancellationToken cancellationToken = default);
}
