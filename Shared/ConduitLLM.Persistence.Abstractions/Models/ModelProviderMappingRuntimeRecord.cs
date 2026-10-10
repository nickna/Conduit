using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Persistence;

/// <summary>
/// Backend-neutral request-time snapshot of a model-to-provider route.
/// </summary>
public sealed class ModelProviderMappingRuntimeRecord
{
    public int Id { get; init; }
    public string ModelAlias { get; init; } = string.Empty;
    public string ProviderModelId { get; init; } = string.Empty;
    public int ProviderId { get; init; }
    public bool IsEnabled { get; init; }
    public int RoutingPriority { get; init; }
    public decimal RoutingWeight { get; init; }
    public string? ProviderOptions { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public required Provider Provider { get; init; }
    public required ModelProviderTypeAssociationRuntimeRecord Association { get; init; }
}

/// <summary>
/// Provider-specific model metadata required by routing, capability checks, and billing.
/// Enum-valued fields remain integers so the persistence contract does not depend on the
/// EF-owned model metadata types.
/// </summary>
public sealed class ModelProviderTypeAssociationRuntimeRecord
{
    public int Id { get; init; }
    public int ModelId { get; init; }
    public bool IsEnabled { get; init; }
    public int? MaxInputTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public string? InputModalitiesJson { get; init; }
    public string? OutputModalitiesJson { get; init; }
    public string? OperationalCapabilitiesJson { get; init; }
    public int? CapabilitySource { get; init; }
    public DateTime? CapabilitiesLastVerifiedAt { get; init; }
    public string? ProviderVariation { get; init; }
    public decimal? QualityScore { get; init; }
    public decimal? SpeedScore { get; init; }
    public string Identifier { get; init; } = string.Empty;
    public int? ProviderType { get; init; }
    public int? ModelCostId { get; init; }
    public bool IsPrimary { get; init; }
    public string? Metadata { get; init; }
    public required ModelRuntimeRecord Model { get; init; }
    public ModelCostRuntimeRecord? ModelCost { get; init; }
}

/// <summary>Canonical model fields consumed by Gateway request paths.</summary>
public sealed class ModelRuntimeRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? ModelCardUrl { get; init; }
    public int ModelSeriesId { get; init; }
    public bool SupportsVision { get; init; }
    public bool SupportsImageGeneration { get; init; }
    public bool SupportsVideoGeneration { get; init; }
    public bool SupportsEmbeddings { get; init; }
    public bool SupportsSpeechToText { get; init; }
    public bool SupportsTextToSpeech { get; init; }
    public bool SupportsRerank { get; init; }
    public bool SupportsChat { get; init; }
    public bool SupportsFunctionCalling { get; init; }
    public bool SupportsStreaming { get; init; }
    public string? InputModalitiesJson { get; init; }
    public string? OutputModalitiesJson { get; init; }
    public int CapabilitySource { get; init; }
    public DateTime? CapabilitiesLastVerifiedAt { get; init; }
    public int TokenizerType { get; init; }
    public int? MaxInputTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool IsActive { get; init; }
    public string? ModelParameters { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public required ModelSeriesRuntimeRecord Series { get; init; }
}

/// <summary>Model-series fields needed for effective parameters and discovery metadata.</summary>
public sealed class ModelSeriesRuntimeRecord
{
    public int Id { get; init; }
    public int AuthorId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int TokenizerType { get; init; }
    public string Parameters { get; init; } = "{}";
}

/// <summary>Cost fields carried with a route for scoring and request billing metadata.</summary>
public sealed class ModelCostRuntimeRecord
{
    public int Id { get; init; }
    public string CostName { get; init; } = string.Empty;
    public int PricingModel { get; init; }
    public string? PricingConfiguration { get; init; }
    public decimal InputCostPerMillionTokens { get; init; }
    public decimal OutputCostPerMillionTokens { get; init; }
    public decimal? EmbeddingCostPerMillionTokens { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string ModelType { get; init; } = "chat";
    public bool IsActive { get; init; }
    public DateTime EffectiveDate { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public string? Description { get; init; }
    public int Priority { get; init; }
    public decimal? BatchProcessingMultiplier { get; init; }
    public bool SupportsBatchProcessing { get; init; }
    public decimal? CachedInputCostPerMillionTokens { get; init; }
    public decimal? CachedInputWriteCostPerMillionTokens { get; init; }
    public decimal? CostPerSearchUnit { get; init; }
    public decimal? AudioCostPerMinute { get; init; }
    public decimal? AudioCostPerThousandCharacters { get; init; }
    public decimal? ReasoningCostPerMillionTokens { get; init; }
}

/// <summary>Persisted balanced-routing policy for one model alias.</summary>
public sealed class ModelRoutePolicyRuntimeRecord
{
    public int Id { get; init; }
    public string ModelAlias { get; init; } = string.Empty;
    public string Strategy { get; init; } = "Balanced";
    public decimal CostWeight { get; init; }
    public decimal SpeedWeight { get; init; }
    public decimal QualityWeight { get; init; }
    public bool CacheAffinityEnabled { get; init; }
    public int AffinityTtlSeconds { get; init; }
    public decimal MaxAffinityScorePenalty { get; init; }
    public bool IsEnabled { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>A deterministic page of request-time model routes.</summary>
public sealed record ModelProviderMappingRuntimePage(
    IReadOnlyList<ModelProviderMappingRuntimeRecord> Items,
    int TotalCount);
