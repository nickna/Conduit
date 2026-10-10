namespace ConduitLLM.Persistence;

/// <summary>
/// Backend-neutral request-accounting row written by Gateway request paths.
/// </summary>
public sealed record RequestLogRuntimeRecord
{
    public int VirtualKeyId { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public int? ProviderId { get; init; }
    public string? ProviderType { get; init; }
    public int? ModelProviderMappingId { get; init; }
    public bool PromptCachingEligible { get; init; }
    public bool PromptCachingPolicyApplied { get; init; }
    public decimal CachedReadSavings { get; init; }
    public decimal CacheWritePremium { get; init; }
    public bool RoutingAffinityUsed { get; init; }
    public string? RoutingDecisionReason { get; init; }
    public int RoutingFailoverCount { get; init; }
    public string RequestType { get; init; } = string.Empty;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int? CachedInputTokens { get; init; }
    public int? CachedWriteTokens { get; init; }
    public decimal Cost { get; init; }
    public int? BillingMethod { get; init; }
    public decimal? ProviderReportedCostUsd { get; init; }
    public decimal? ProviderCostMarkupMultiplier { get; init; }
    public DateTime? BilledAtUtc { get; init; }
    public double ResponseTimeMs { get; init; }
    public DateTime Timestamp { get; init; }
    public string? UserId { get; init; }
    public string? ClientIp { get; init; }
    public string? RequestPath { get; init; }
    public int? StatusCode { get; init; }
    public string? MetadataJson { get; init; }
}
