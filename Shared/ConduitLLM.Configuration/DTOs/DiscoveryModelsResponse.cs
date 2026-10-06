namespace ConduitLLM.Configuration.DTOs;

/// <summary>
/// Service-neutral capabilities projection for a discovered model. HTTP services
/// own the wire naming policy through their service-specific JSON contexts.
/// </summary>
public sealed record DiscoveryModelCapabilitiesDto(
    bool Chat,
    bool ChatStream,
    bool ImageInput,
    bool VideoInput,
    bool AudioInput,
    bool FileInput,
    bool Vision,
    bool VideoUnderstanding,
    bool ImageGeneration,
    bool VideoGeneration,
    bool Embeddings,
    bool FunctionCalling,
    bool SpeechToText,
    bool TextToSpeech,
    bool Rerank,
    bool? ToolUse = null,
    bool? JsonMode = null,
    int? MaxTokens = null,
    int? MaxOutputTokens = null,
    bool PdfInput = false);

/// <summary>Service-neutral operator-configured pricing projection.</summary>
public sealed record DiscoveryModelPricingDto(
    string PricingModel,
    decimal InputCostPerMillionTokens,
    decimal OutputCostPerMillionTokens,
    decimal? CachedInputCostPerMillionTokens,
    decimal? EmbeddingCostPerMillionTokens,
    string Currency);

/// <summary>Service-neutral model discovery projection.</summary>
public sealed record DiscoveredModelDto(
    string Id,
    string? Provider,
    string DisplayName,
    string Description,
    string ModelCardUrl,
    int MaxTokens,
    int MaxInputTokens,
    int MaxOutputTokens,
    string TokenizerType,
    IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> OutputModalities,
    string CapabilitySource,
    DateTime? CapabilitiesLastVerifiedAt,
    string Parameters,
    DiscoveryModelCapabilitiesDto Capabilities,
    DiscoveryModelPricingDto? Pricing = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? PricingRefreshAt { get; init; }
}

/// <summary>Service-neutral model discovery response projection.</summary>
public sealed record DiscoveryModelsResponse(
    IReadOnlyList<DiscoveredModelDto> Data,
    int Count);
