namespace ConduitLLM.Core.Interfaces;

public sealed record WebhookAdmissionDecision(IWebhookAdmissionLease? Lease, DateTime DueAt, bool IsProbe = false);

/// <summary>Non-blocking receiver admission. A denial must be durably scheduled.</summary>
public interface IWebhookAdmission
{
    Task<WebhookAdmissionDecision> AcquireAsync(string destination, CancellationToken cancellationToken = default);
}

public interface IWebhookAdmissionLease : IAsyncDisposable
{
    /// <param name="success">True for acceptance, false for retryable receiver failure, null for release only.</param>
    Task RecordAsync(bool? success, CancellationToken cancellationToken = default);
}
