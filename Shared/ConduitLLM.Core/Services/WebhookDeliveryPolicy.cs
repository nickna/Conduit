using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Core.Services;

/// <summary>One receiver policy, independent of transport delivery counts and deferrals.</summary>
public sealed class WebhookDeliveryPolicy
{
    public WebhookDeliveryOptions Options { get; }
    private readonly TimeProvider _clock;
    private readonly Func<double> _random;
    public WebhookDeliveryPolicy(IOptions<WebhookDeliveryOptions>? options = null,
        TimeProvider? clock = null, Func<double>? random = null)
    {
        Options = options?.Value ?? new();
        if (!Options.IsValid()) throw new ArgumentException("Invalid webhook delivery policy.", nameof(options));
        _clock = clock ?? TimeProvider.System;
        _random = random ?? Random.Shared.NextDouble;
    }
    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    public DateTime Deadline(WebhookDeliveryRequested request)
    {
        var start = request.DeliveryStartedAt ?? request.Timestamp;
        if (start.Kind == DateTimeKind.Unspecified) start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
        else start = start.ToUniversalTime();
        if (start > UtcNow) start = UtcNow;
        return start.AddSeconds(request.EventType is WebhookEventType.TaskProgress or WebhookEventType.TaskStarted
            ? Options.ProgressWindowSeconds : Options.TerminalWindowSeconds);
    }
    public static bool IsRetryable(WebhookSendResult result) => !result.Success &&
        (result.FailureKind is WebhookFailureKind.Network or WebhookFailureKind.Timeout ||
            result.StatusCode is 408 or 429 or 500 or 502 or 503 or 504);

    public DateTime RetryAt(int attempts, DateTime deadline, TimeSpan? retryAfter)
    {
        var seconds = Math.Min(Options.MaxDelaySeconds,
            Options.InitialDelaySeconds * Math.Pow(2, Math.Min(30, Math.Max(0, attempts - 1))));
        seconds = Math.Min(Options.MaxDelaySeconds, seconds * (1 - Options.JitterRatio + 2 * Options.JitterRatio * _random()));
        if (retryAfter is { } requested && requested > TimeSpan.Zero)
            seconds = Math.Max(seconds, Math.Min(Options.MaxRetryAfterSeconds, requested.TotalSeconds));
        return Cap(UtcNow.AddSeconds(Math.Max(1, seconds)), deadline);
    }
    public DateTime DeferUntil(DateTime deadline, DateTime? admissionDueAt = null) =>
        Cap(admissionDueAt > UtcNow ? admissionDueAt.Value : UtcNow.AddSeconds(Options.DeferralSeconds), deadline);
    private static DateTime Cap(DateTime proposed, DateTime deadline) => proposed < deadline ? proposed : deadline;
}
