using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Services;

namespace ConduitLLM.Gateway.Consumers;

/// <summary>One bounded receiver attempt; PostgreSQL commits every receipt and future delivery.</summary>
public sealed class WebhookDeliveryConsumer(IWebhookNotificationService webhookService,
    IWebhookDeliveryStore store, IWebhookAdmission admission,
    IWebhookDeliveryNotificationService notifications, ILogger<WebhookDeliveryConsumer> logger,
    WebhookDeliveryPolicy? policy = null) : IEventHandler<WebhookDeliveryRequested>
{
    private readonly WebhookDeliveryPolicy _policy = policy ?? new();

    public async Task HandleAsync(WebhookDeliveryRequested request, IEventContext context)
    {
        var cancellation = context.CancellationToken;
        var claim = await store.TryClaimAsync(request, _policy.Deadline(request),
            TimeSpan.FromSeconds(_policy.Options.AttemptTimeoutSeconds + 30), cancellation);
        if (claim.Status != WebhookClaimStatus.Acquired) return;
        request = claim.Request;
        if (claim.Deadline <= _policy.UtcNow || claim.Attempts >= _policy.Options.MaxAttempts)
        {
            await ExhaustAsync(claim, WebhookSendResult.Failed(null, "Delivery window or attempt budget exhausted."),
                claim.Attempts, cancellation);
            return;
        }

        JsonElement payload;
        try
        {
            if (Encoding.UTF8.GetByteCount(request.PayloadJson) > 1024 * 1024 ||
                !Uri.TryCreate(request.WebhookUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("Invalid callback request.");
            using var document = JsonDocument.Parse(request.PayloadJson);
            if (document.RootElement.ValueKind == JsonValueKind.Null)
            {
                using var fallback = JsonDocument.Parse("""{"error":"Failed to deserialize payload"}""");
                payload = fallback.RootElement.Clone();
            }
            else payload = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            await ExhaustAsync(claim, WebhookSendResult.Failed(null, "Invalid callback URL or payload.",
                WebhookFailureKind.InvalidRequest), claim.Attempts, cancellation);
            return;
        }

        var decision = await admission.AcquireAsync(request.WebhookUrl, cancellation);
        await using var admissionLease = decision.Lease;
        if (admissionLease == null)
        {
            var due = decision.DueAt < claim.Deadline ? decision.DueAt : claim.Deadline;
            if (await store.ScheduleAsync(claim, due, cancellationToken: cancellation))
            {
                WebhookDeliveryTelemetry.Schedule(deferral: true);
                await ReportAsync(() => notifications.NotifyRetryScheduledAsync(request.WebhookUrl, request.TaskId,
                    due, claim.Attempts, _policy.Options.MaxAttempts));
            }
            return;
        }

        var attempt = await store.BeginAttemptAsync(claim, cancellation);
        if (!attempt.HasValue) return;
        await ReportAsync(() => notifications.NotifyDeliveryAttemptAsync(request.WebhookUrl, request.TaskId,
            request.TaskType, request.EventType.ToString(), attempt.Value));
        var stopwatch = Stopwatch.StartNew();
        WebhookSendResult result;
        try
        {
            var headers = WebhookIdentity.Headers(request);
            result = request.EventType == WebhookEventType.TaskProgress
                ? await webhookService.SendTaskProgressWebhookAsync(request.WebhookUrl, payload, headers, cancellation)
                : await webhookService.SendTaskCompletionWebhookAsync(request.WebhookUrl, payload, headers, cancellation);
        }
        catch (HttpRequestException)
        {
            result = WebhookSendResult.Failed(null, "Receiver connection failed.", WebhookFailureKind.Network);
        }
        // Shutdown/infrastructure failures propagate. The committed lease recovery
        // envelope preserves work and its already-reserved logical attempt budget.

        if (result.Success)
        {
            if (!await store.CompleteAsync(claim, result, exhausted: false, cancellation)) return;
            await ReportAsync(() => admissionLease.RecordAsync(true));
            await ReportAsync(() => notifications.NotifyDeliverySuccessAsync(request.WebhookUrl, request.TaskId,
                result.StatusCode ?? 0, stopwatch.ElapsedMilliseconds, attempt.Value));
            return;
        }

        var retryable = WebhookDeliveryPolicy.IsRetryable(result);
        await ReportAsync(() => admissionLease.RecordAsync(retryable ? false : null));
        if (!retryable || attempt.Value >= _policy.Options.MaxAttempts || _policy.UtcNow >= claim.Deadline)
        {
            await ExhaustAsync(claim, result, attempt.Value, cancellation);
            return;
        }

        var retryAt = _policy.RetryAt(attempt.Value, claim.Deadline, result.RetryAfter);
        if (!await store.ScheduleAsync(claim, retryAt, result, cancellation)) return;
        WebhookDeliveryTelemetry.Schedule(deferral: false);
        await ReportAsync(() => notifications.NotifyDeliveryFailureAsync(request.WebhookUrl, request.TaskId,
            result.Error ?? "Receiver attempt failed.", result.StatusCode, attempt.Value, false));
        await ReportAsync(() => notifications.NotifyRetryScheduledAsync(request.WebhookUrl, request.TaskId,
            retryAt, attempt.Value, _policy.Options.MaxAttempts));
    }

    private async Task ExhaustAsync(WebhookClaim claim, WebhookSendResult result, int attempts, CancellationToken cancellation)
    {
        if (!await store.CompleteAsync(claim, result, exhausted: true, cancellation)) return;
        await ReportAsync(() => notifications.NotifyDeliveryFailureAsync(claim.Request.WebhookUrl, claim.Request.TaskId,
            result.Error ?? "Delivery exhausted.", result.StatusCode, attempts, true));
        throw new NonRetryableMessageException($"Webhook delivery {claim.Id} exhausted after {attempts} reserved attempts.");
    }

    private async Task ReportAsync(Func<Task> report)
    {
        try { await report(); }
        catch (Exception) { logger.LogWarning("Optional webhook reporting failed; authoritative delivery state is preserved"); }
    }
}
