using ConduitLLM.Core.Events;

namespace ConduitLLM.Core.Interfaces;

public sealed record MediaTaskTerminalTransition(string TaskId, TaskState State,
    string? ExpectedWorkerId = null, int? Progress = null, string? ResultJson = null,
    string? Error = null, WebhookDeliveryRequested? Webhook = null);

/// <summary>Commits terminal state and callback intent in the existing Wolverine outbox transaction.</summary>
public interface IMediaTaskTerminalWriter
{
    Task<bool> CommitAsync(MediaTaskTerminalTransition transition, CancellationToken cancellationToken = default);
}
