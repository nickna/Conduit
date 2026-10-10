namespace ConduitLLM.Core.Interfaces;

public sealed record WebhookDeliveryInspection(string Id, string EventId, string TaskId, int VirtualKeyId,
    string State, int Attempts, int Cycle, DateTime CreatedAt, DateTime UpdatedAt, DateTime Deadline,
    DateTime NextAttemptAt, DateTime RetainUntil, int? StatusCode, string? Reason);
public sealed record WebhookReplayRequest(Guid OperationId, int VirtualKeyId, int ExpectedCycle, Guid? DeadLetterId = null);
public sealed record WebhookReplayResult(string Outcome, int Cycle);
public sealed record WebhookBacklog(long Pending, long Delivered, long Exhausted, long ReservedAttempts, double OldestPendingSeconds, long ReplayCycles = 0);
public sealed record WebhookDeadLetterInspection(Guid EnvelopeId, string? DeliveryId, DateTimeOffset SentAt);

public interface IWebhookRecovery
{
    Task<List<WebhookDeliveryInspection>> InspectAsync(int? owner, string? taskId, string? eventId, int limit, CancellationToken ct);
    Task<WebhookReplayResult> ReplayAsync(string id, WebhookReplayRequest request, string actor, CancellationToken ct);
    Task<List<WebhookDeadLetterInspection>> DeadLettersAsync(int limit, CancellationToken ct);
    Task<WebhookBacklog> BacklogAsync(CancellationToken ct);
    Task<int> PurgeAsync(int limit, CancellationToken ct);
}
