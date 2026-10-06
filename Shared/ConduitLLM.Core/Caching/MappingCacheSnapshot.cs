using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Models;

namespace ConduitLLM.Core.Caching;

/// <summary>Versioned detached routing graph. No credentials, EF tracking or cyclic navigation collections.</summary>
public sealed record MappingCacheSnapshot(int Version, MappingRow Mapping, ProviderRow Provider,
    AssociationRow Association, ModelRow Model, SeriesRow? Series, CostRow? Cost)
{
    public static MappingCacheSnapshot From(ModelProviderMapping value)
    {
        if (value.Provider is null || value.ModelProviderTypeAssociation?.Model is null)
            throw new InvalidOperationException("A mapping loader must supply its provider, association and canonical model.");
        var association = value.ModelProviderTypeAssociation;
        return new(1, MappingRow.From(value), ProviderRow.From(value.Provider), AssociationRow.From(association),
            ModelRow.From(association.Model), association.Model.Series is null ? null : SeriesRow.From(association.Model.Series),
            association.ModelCost is null ? null : CostRow.From(association.ModelCost));
    }

    public ModelProviderMapping ToDomain()
    {
        if (Version != 1 || Mapping is null || Provider is null || Association is null || Model is null)
            throw new System.Text.Json.JsonException("Incomplete or obsolete mapping cache snapshot.");
        var value = Mapping.ToDomain();
        value.Provider = Provider.ToDomain();
        value.ModelProviderTypeAssociation = Association.ToDomain();
        value.ModelProviderTypeAssociation.Model = Model.ToDomain();
        value.ModelProviderTypeAssociation.Model.Series = Series?.ToDomain()!;
        value.ModelProviderTypeAssociation.ModelCost = Cost?.ToDomain();
        return value;
    }
}

public sealed record MappingRow
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
    public int ModelProviderTypeAssociationId { get; init; }

    internal static MappingRow From(ModelProviderMapping source) => new()
    {
        Id = source.Id,
        ModelAlias = source.ModelAlias,
        ProviderModelId = source.ProviderModelId,
        ProviderId = source.ProviderId,
        IsEnabled = source.IsEnabled,
        RoutingPriority = source.RoutingPriority,
        RoutingWeight = source.RoutingWeight,
        ProviderOptions = source.ProviderOptions,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        ModelProviderTypeAssociationId = source.ModelProviderTypeAssociationId,
    };
    internal ModelProviderMapping ToDomain() => new()
    {
        Id = Id,
        ModelAlias = ModelAlias,
        ProviderModelId = ProviderModelId,
        ProviderId = ProviderId,
        IsEnabled = IsEnabled,
        RoutingPriority = RoutingPriority,
        RoutingWeight = RoutingWeight,
        ProviderOptions = ProviderOptions,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        ModelProviderTypeAssociationId = ModelProviderTypeAssociationId,
    };
}

public sealed record ProviderRow
{
    public int Id { get; init; }
    public ProviderType ProviderType { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public string? BaseUrl { get; init; }
    public Dictionary<string, string>? Settings { get; init; }
    public bool IsEnabled { get; init; }
    public bool TrustProviderReportedCosts { get; init; }
    public decimal ProviderCostMarkupMultiplier { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    internal static ProviderRow From(Provider source) => new()
    {
        Id = source.Id,
        ProviderType = source.ProviderType,
        ProviderName = source.ProviderName,
        BaseUrl = source.BaseUrl,
        Settings = source.Settings is null ? null : new(source.Settings),
        IsEnabled = source.IsEnabled,
        TrustProviderReportedCosts = source.TrustProviderReportedCosts,
        ProviderCostMarkupMultiplier = source.ProviderCostMarkupMultiplier,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };
    internal Provider ToDomain() => new()
    {
        Id = Id,
        ProviderType = ProviderType,
        ProviderName = ProviderName,
        BaseUrl = BaseUrl,
        Settings = Settings is null ? null : new(Settings),
        IsEnabled = IsEnabled,
        TrustProviderReportedCosts = TrustProviderReportedCosts,
        ProviderCostMarkupMultiplier = ProviderCostMarkupMultiplier,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}

public sealed record AssociationRow
{
    public int Id { get; init; }
    public int ModelId { get; init; }
    public bool IsEnabled { get; init; }
    public int? MaxInputTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public string? InputModalitiesJson { get; init; }
    public string? OutputModalitiesJson { get; init; }
    public string? OperationalCapabilitiesJson { get; init; }
    public ModelCapabilitySource? CapabilitySource { get; init; }
    public DateTime? CapabilitiesLastVerifiedAt { get; init; }
    public string? ProviderVariation { get; init; }
    public decimal? QualityScore { get; init; }
    public decimal? SpeedScore { get; init; }
    public string Identifier { get; init; } = string.Empty;
    public ProviderType? Provider { get; init; }
    public int? ModelCostId { get; init; }
    public bool IsPrimary { get; init; }
    public string? Metadata { get; init; }

    internal static AssociationRow From(ModelProviderTypeAssociation source) => new()
    {
        Id = source.Id,
        ModelId = source.ModelId,
        IsEnabled = source.IsEnabled,
        MaxInputTokens = source.MaxInputTokens,
        MaxOutputTokens = source.MaxOutputTokens,
        InputModalitiesJson = source.InputModalitiesJson,
        OutputModalitiesJson = source.OutputModalitiesJson,
        OperationalCapabilitiesJson = source.OperationalCapabilitiesJson,
        CapabilitySource = source.CapabilitySource,
        CapabilitiesLastVerifiedAt = source.CapabilitiesLastVerifiedAt,
        ProviderVariation = source.ProviderVariation,
        QualityScore = source.QualityScore,
        SpeedScore = source.SpeedScore,
        Identifier = source.Identifier,
        Provider = source.Provider,
        ModelCostId = source.ModelCostId,
        IsPrimary = source.IsPrimary,
        Metadata = source.Metadata,
    };
    internal ModelProviderTypeAssociation ToDomain() => new()
    {
        Id = Id,
        ModelId = ModelId,
        IsEnabled = IsEnabled,
        MaxInputTokens = MaxInputTokens,
        MaxOutputTokens = MaxOutputTokens,
        InputModalitiesJson = InputModalitiesJson,
        OutputModalitiesJson = OutputModalitiesJson,
        OperationalCapabilitiesJson = OperationalCapabilitiesJson,
        CapabilitySource = CapabilitySource,
        CapabilitiesLastVerifiedAt = CapabilitiesLastVerifiedAt,
        ProviderVariation = ProviderVariation,
        QualityScore = QualityScore,
        SpeedScore = SpeedScore,
        Identifier = Identifier,
        Provider = Provider,
        ModelCostId = ModelCostId,
        IsPrimary = IsPrimary,
        Metadata = Metadata,
    };
}

public sealed record ModelRow
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
    public ModelCapabilitySource CapabilitySource { get; init; }
    public DateTime? CapabilitiesLastVerifiedAt { get; init; }
    public TokenizerType TokenizerType { get; init; }
    public int? MaxInputTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool IsActive { get; init; }
    public string? ModelParameters { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    internal static ModelRow From(Model source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Version = source.Version,
        Description = source.Description,
        ModelCardUrl = source.ModelCardUrl,
        ModelSeriesId = source.ModelSeriesId,
        SupportsVision = source.SupportsVision,
        SupportsImageGeneration = source.SupportsImageGeneration,
        SupportsVideoGeneration = source.SupportsVideoGeneration,
        SupportsEmbeddings = source.SupportsEmbeddings,
        SupportsSpeechToText = source.SupportsSpeechToText,
        SupportsTextToSpeech = source.SupportsTextToSpeech,
        SupportsRerank = source.SupportsRerank,
        SupportsChat = source.SupportsChat,
        SupportsFunctionCalling = source.SupportsFunctionCalling,
        SupportsStreaming = source.SupportsStreaming,
        InputModalitiesJson = source.InputModalitiesJson,
        OutputModalitiesJson = source.OutputModalitiesJson,
        CapabilitySource = source.CapabilitySource,
        CapabilitiesLastVerifiedAt = source.CapabilitiesLastVerifiedAt,
        TokenizerType = source.TokenizerType,
        MaxInputTokens = source.MaxInputTokens,
        MaxOutputTokens = source.MaxOutputTokens,
        IsActive = source.IsActive,
        ModelParameters = source.ModelParameters,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };
    internal Model ToDomain() => new()
    {
        Id = Id,
        Name = Name,
        Version = Version,
        Description = Description,
        ModelCardUrl = ModelCardUrl,
        ModelSeriesId = ModelSeriesId,
        SupportsVision = SupportsVision,
        SupportsImageGeneration = SupportsImageGeneration,
        SupportsVideoGeneration = SupportsVideoGeneration,
        SupportsEmbeddings = SupportsEmbeddings,
        SupportsSpeechToText = SupportsSpeechToText,
        SupportsTextToSpeech = SupportsTextToSpeech,
        SupportsRerank = SupportsRerank,
        SupportsChat = SupportsChat,
        SupportsFunctionCalling = SupportsFunctionCalling,
        SupportsStreaming = SupportsStreaming,
        InputModalitiesJson = InputModalitiesJson,
        OutputModalitiesJson = OutputModalitiesJson,
        CapabilitySource = CapabilitySource,
        CapabilitiesLastVerifiedAt = CapabilitiesLastVerifiedAt,
        TokenizerType = TokenizerType,
        MaxInputTokens = MaxInputTokens,
        MaxOutputTokens = MaxOutputTokens,
        IsActive = IsActive,
        ModelParameters = ModelParameters,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}

public sealed record SeriesRow
{
    public int Id { get; init; }
    public int AuthorId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public TokenizerType TokenizerType { get; init; }
    public string Parameters { get; init; } = string.Empty;

    internal static SeriesRow From(ModelSeries source) => new()
    {
        Id = source.Id,
        AuthorId = source.AuthorId,
        Name = source.Name,
        Description = source.Description,
        TokenizerType = source.TokenizerType,
        Parameters = source.Parameters,
    };
    internal ModelSeries ToDomain() => new()
    {
        Id = Id,
        AuthorId = AuthorId,
        Name = Name,
        Description = Description,
        TokenizerType = TokenizerType,
        Parameters = Parameters,
    };
}

public sealed record CostRow
{
    public int Id { get; init; }
    public string CostName { get; init; } = string.Empty;
    public PricingModel PricingModel { get; init; }
    public string? PricingConfiguration { get; init; }
    public decimal InputCostPerMillionTokens { get; init; }
    public decimal OutputCostPerMillionTokens { get; init; }
    public decimal? EmbeddingCostPerMillionTokens { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string ModelType { get; init; } = string.Empty;
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

    internal static CostRow From(ModelCost source) => new()
    {
        Id = source.Id,
        CostName = source.CostName,
        PricingModel = source.PricingModel,
        PricingConfiguration = source.PricingConfiguration,
        InputCostPerMillionTokens = source.InputCostPerMillionTokens,
        OutputCostPerMillionTokens = source.OutputCostPerMillionTokens,
        EmbeddingCostPerMillionTokens = source.EmbeddingCostPerMillionTokens,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        ModelType = source.ModelType,
        IsActive = source.IsActive,
        EffectiveDate = source.EffectiveDate,
        ExpiryDate = source.ExpiryDate,
        Description = source.Description,
        Priority = source.Priority,
        BatchProcessingMultiplier = source.BatchProcessingMultiplier,
        SupportsBatchProcessing = source.SupportsBatchProcessing,
        CachedInputCostPerMillionTokens = source.CachedInputCostPerMillionTokens,
        CachedInputWriteCostPerMillionTokens = source.CachedInputWriteCostPerMillionTokens,
        CostPerSearchUnit = source.CostPerSearchUnit,
        AudioCostPerMinute = source.AudioCostPerMinute,
        AudioCostPerThousandCharacters = source.AudioCostPerThousandCharacters,
        ReasoningCostPerMillionTokens = source.ReasoningCostPerMillionTokens,
    };
    internal ModelCost ToDomain() => new()
    {
        Id = Id,
        CostName = CostName,
        PricingModel = PricingModel,
        PricingConfiguration = PricingConfiguration,
        InputCostPerMillionTokens = InputCostPerMillionTokens,
        OutputCostPerMillionTokens = OutputCostPerMillionTokens,
        EmbeddingCostPerMillionTokens = EmbeddingCostPerMillionTokens,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        ModelType = ModelType,
        IsActive = IsActive,
        EffectiveDate = EffectiveDate,
        ExpiryDate = ExpiryDate,
        Description = Description,
        Priority = Priority,
        BatchProcessingMultiplier = BatchProcessingMultiplier,
        SupportsBatchProcessing = SupportsBatchProcessing,
        CachedInputCostPerMillionTokens = CachedInputCostPerMillionTokens,
        CachedInputWriteCostPerMillionTokens = CachedInputWriteCostPerMillionTokens,
        CostPerSearchUnit = CostPerSearchUnit,
        AudioCostPerMinute = AudioCostPerMinute,
        AudioCostPerThousandCharacters = AudioCostPerThousandCharacters,
        ReasoningCostPerMillionTokens = ReasoningCostPerMillionTokens,
    };
}

