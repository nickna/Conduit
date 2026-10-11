namespace ConduitLLM.Core.Models;

/// <summary>Verified provider identity captured before polling an accepted media operation.</summary>
public sealed record MediaProviderOperation(int ProviderId, int CredentialId, string ProviderModelId,
    string ProviderName, string? OperationId = null);

/// <summary>Durable operator evidence and billing disposition for recovery of the original output.</summary>
public sealed record MediaTaskReconciliation
{
    public string DispatchId { get; init; } = "";
    public int VirtualKeyId { get; init; }
    public int ProviderId { get; init; }
    public string ProviderModelId { get; init; } = "";
    public string Model { get; init; } = "";
    public string ProviderOperationId { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Reason { get; init; } = "";
    public List<string> ResultUrls { get; init; } = [];
    public decimal? ProviderCost { get; init; }
    public string RequestedBy { get; init; } = "Admin";
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    public string Outcome { get; init; } = "accepted";
    public string BillingDisposition { get; init; } = "no_customer_charge";

    public bool SameRequest(MediaTaskReconciliation other) => DispatchId == other.DispatchId &&
        VirtualKeyId == other.VirtualKeyId && ProviderId == other.ProviderId &&
        ProviderModelId == other.ProviderModelId && Model == other.Model &&
        ProviderOperationId == other.ProviderOperationId && Evidence == other.Evidence &&
        Reason == other.Reason && ProviderCost == other.ProviderCost && ResultUrls.SequenceEqual(other.ResultUrls);

    public string? ValidationError()
    {
        if (!Guid.TryParseExact(DispatchId, "N", out var id) || id == Guid.Empty ||
            VirtualKeyId <= 0 || ProviderId <= 0 || ProviderCost is < 0 or > 1_000_000_000)
            return "A dispatch ID, owner, provider, and non-negative provider cost are required.";
        if (new[] { Model, ProviderModelId, ProviderOperationId }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200) ||
            new[] { Reason, Evidence }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 500))
            return "Model, provider model, operation ID, reason, and evidence are required.";
        if (ResultUrls.Count is < 1 or > 10 || ResultUrls.Any(x => !Utilities.MediaRecoveryDestination.IsValidUrl(x)) ||
            ResultUrls.Distinct(StringComparer.Ordinal).Count() != ResultUrls.Count)
            return "Provide 1-10 distinct public HTTPS media URLs, without credentials or redirects.";
        return null;
    }
}
