namespace ConduitLLM.Persistence;

/// <summary>
/// Backend-neutral snapshot of the virtual-key fields used by Gateway requests.
/// </summary>
public sealed class VirtualKeyRuntimeRecord
{
    public int Id { get; init; }
    public string KeyName { get; init; } = string.Empty;
    public string KeyHash { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsEnabled { get; init; }
    public int VirtualKeyGroupId { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? Metadata { get; init; }
    public string? AllowedModels { get; init; }
    public int? RateLimitRpm { get; init; }
    public int? RateLimitRpd { get; init; }
    public int? RateLimitTpm { get; init; }
    public int? MaxParallelRequests { get; init; }
    public int? RateLimitPriority { get; init; }
    public string? ModelRateLimits { get; init; }
    public byte[]? RowVersion { get; init; }
    public required VirtualKeyGroupRuntimeRecord Group { get; init; }
}

/// <summary>
/// Backend-neutral snapshot of the group fields used by validation and limits.
/// </summary>
public sealed class VirtualKeyGroupRuntimeRecord
{
    public int Id { get; init; }
    public string? ExternalGroupId { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public decimal Balance { get; init; }
    public decimal LifetimeCreditsAdded { get; init; }
    public decimal LifetimeSpent { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public int? MediaRetentionPolicyId { get; init; }
    public int? RateLimitRpm { get; init; }
    public int? RateLimitRpd { get; init; }
    public int? RateLimitTpm { get; init; }
    public int? MaxParallelRequests { get; init; }
    public byte[]? RowVersion { get; init; }
}
