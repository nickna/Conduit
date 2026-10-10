namespace ConduitLLM.Persistence;

/// <summary>
/// Backend-neutral snapshot of a durable media record used by Gateway runtime flows.
/// </summary>
public sealed class MediaRuntimeRecord
{
    public Guid Id { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public int VirtualKeyId { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
    public string? ContentHash { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Prompt { get; set; }
    public string? StorageUrl { get; set; }
    public string? PublicUrl { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public int AccessCount { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public sealed class MediaRuntimeStorageAggregate
{
    public int TotalFiles { get; init; }
    public long TotalSizeBytes { get; init; }
    public IReadOnlyDictionary<string, long> ByProvider { get; init; } =
        new Dictionary<string, long>();
    public IReadOnlyList<MediaRuntimeTypeAggregate> ByMediaType { get; init; } =
        Array.Empty<MediaRuntimeTypeAggregate>();
    public IReadOnlyList<MediaRuntimeVirtualKeyAggregate> TopVirtualKeys { get; init; } =
        Array.Empty<MediaRuntimeVirtualKeyAggregate>();
}

public sealed record MediaRuntimeTypeAggregate(
    string MediaType,
    int FileCount,
    long SizeBytes);

public sealed record MediaRuntimeVirtualKeyAggregate(
    int VirtualKeyId,
    long SizeBytes);

/// <summary>
/// Effective quota policy and current usage for the group owning one virtual key.
/// </summary>
public sealed record MediaRuntimeQuotaSnapshot(
    int VirtualKeyGroupId,
    long TotalSizeBytes,
    int TotalFiles,
    long? MaxStorageSizeBytes,
    int? MaxFileCount,
    int QuotaExceededBehavior);

/// <summary>
/// Effective media quota and usage reported for one virtual-key group.
/// </summary>
public sealed record MediaRuntimeGroupQuotaUsage(
    int VirtualKeyGroupId,
    string VirtualKeyGroupName,
    int? MediaRetentionPolicyId,
    string? MediaRetentionPolicyName,
    long TotalSizeBytes,
    int TotalFiles,
    long? MaxStorageSizeBytes,
    int? MaxFileCount,
    int QuotaExceededBehavior,
    bool RespectRecentAccess,
    int RecentAccessWindowDays);
