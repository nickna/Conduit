namespace ConduitLLM.Persistence;

/// <summary>A backend-neutral scalar billing audit event without navigation graphs.</summary>
public sealed record BillingAuditRuntimeRecord
{
    public Guid Id { get; init; }
    public DateTime Timestamp { get; init; }
    public int EventType { get; init; }
    public int? VirtualKeyId { get; init; }
    public int? VirtualKeyGroupId { get; init; }
    public string? Model { get; init; }
    public string? RequestId { get; init; }
    public string? UsageJson { get; init; }
    public decimal? CalculatedCost { get; init; }
    public string? FailureReason { get; init; }
    public string? ProviderType { get; init; }
    public int? HttpStatusCode { get; init; }
    public string? MetadataJson { get; init; }
    public string? RequestPath { get; init; }
    public bool IsEstimated { get; init; }
    public string? ToolUsageJson { get; init; }
    public decimal? ToolUsageCost { get; init; }
}
