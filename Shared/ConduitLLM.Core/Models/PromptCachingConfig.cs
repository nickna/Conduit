using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Core.Validation;

namespace ConduitLLM.Core.Models;

public static class PromptCachingConstants
{
    public const int SchemaVersion = 3;
    public const string SettingsKey = "PromptCaching.Config";
    public const int MaxExplicitBreakpoints = 4;
}

public static class PromptCachingSerialization
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>Provider-aware managed prompt caching configuration.</summary>
public sealed class PromptCachingConfig
{
    public int SchemaVersion { get; set; }

    public bool Enabled { get; set; }

    public List<PromptCachingRule> Rules { get; set; } = new();
}

public sealed class PromptCachingRule
{
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    [Required]
    public string Provider { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string ModelPattern { get; set; } = string.Empty;

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter<PromptCachingStrategy>))]
    public PromptCachingStrategy Strategy { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ttl { get; set; }

    [MaxItems(PromptCachingConstants.MaxExplicitBreakpoints)]
    public List<CacheInjectionPoint> InjectionPoints { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<PromptCachingStrategy>))]
public enum PromptCachingStrategy
{
    Automatic,
    Explicit,
    [Obsolete("Migrated to Automatic in schema v3.")]
    OpenRouterAutomatic = Automatic,
    [Obsolete("Migrated to Explicit in schema v3.")]
    OpenRouterExplicit = Explicit
}

public sealed class CacheInjectionPoint
{
    [RegularExpression("^(system|developer|user|assistant)$")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [Range(-100, 100)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; set; }
}

/// <summary>Server-only resolved intent consumed by a provider adapter.</summary>
public sealed class PromptCachingIntent
{
    public required PromptCachingStrategy Strategy { get; init; }
    public string? Ttl { get; init; }
    public IReadOnlyList<CacheInjectionPoint> InjectionPoints { get; init; } = Array.Empty<CacheInjectionPoint>();
    public string? PromptCacheKey { get; init; }
}

public sealed record PromptCachingCapability(
    string Provider,
    string ModelPattern,
    IReadOnlyList<PromptCachingStrategy> Strategies,
    IReadOnlyList<string> Ttls,
    int? MinimumTokens,
    int MaxBreakpoints,
    bool ProviderManaged);
