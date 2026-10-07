namespace ConduitLLM.Core.Serialization;

/// <summary>Read compatibility for expiring pre-admission Redis snapshots; no runtime writer.</summary>
internal sealed class LegacyWebhookCircuitState
{
    public string State { get; set; } = "Closed";
    public DateTime OpenedAt { get; set; }
    public DateTime? HalfOpenTestAt { get; set; }
    public int FailureCount { get; set; }
    public string WebhookUrl { get; set; } = "";
}
