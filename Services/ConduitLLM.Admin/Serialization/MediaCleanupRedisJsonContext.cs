using System.Text.Json.Serialization;

namespace ConduitLLM.Admin.Serialization;

/// <summary>
/// Source-generated metadata for media-cleanup status values persisted in Redis.
/// These contracts intentionally retain the legacy PascalCase property names.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LastRunInfo))]
[JsonSerializable(typeof(ReconciliationDriftInfo))]
internal partial class MediaCleanupRedisJsonContext : JsonSerializerContext;

internal sealed class LastRunInfo
{
    public DateTime? LastRunTimeUtc { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public double DurationSeconds { get; set; }
    public string? Status { get; set; }
    public string? LeaderInstanceId { get; set; }
    public string? TriggeredBy { get; set; }
}

internal sealed class ReconciliationDriftInfo
{
    public int UntrackedObjectCount { get; set; }
    public long UntrackedBytes { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
}
