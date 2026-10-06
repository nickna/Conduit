using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models;

/// <summary>Persisted OpenAI-compatible result for an asynchronous media task.</summary>
public sealed class MediaGenerationTaskResult
{
    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("data")]
    public List<MediaGenerationTaskResultItem> Data { get; init; } = [];

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("usage")]
    public MediaGenerationTaskUsage Usage { get; init; } = new();

    [JsonPropertyName("_metadata")]
    public MediaGenerationTaskInternalMetadata InternalMetadata { get; init; } = new();
}

public sealed class MediaGenerationTaskResultItem
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; init; }
}

public sealed class MediaGenerationTaskUsage
{
    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; init; }
}

public sealed class MediaGenerationTaskInternalMetadata
{
    [JsonPropertyName("cost")]
    public decimal Cost { get; init; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("generation_duration_seconds")]
    public double GenerationDurationSeconds { get; init; }
}
