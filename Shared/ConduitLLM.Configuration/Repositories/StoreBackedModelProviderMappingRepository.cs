using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// Read-only Gateway adapter from the fixed-shape runtime store to the legacy domain
/// repository contract. Admin and JIT hosts retain <see cref="ModelProviderMappingRepository"/>.
/// </summary>
public sealed class StoreBackedModelProviderMappingRepository : IModelProviderMappingRepository
{
    private const int PageSize = 100;

    private readonly IModelProviderMappingRuntimeStore _store;

    public StoreBackedModelProviderMappingRepository(IModelProviderMappingRuntimeStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async Task<ModelProviderMapping?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        ConvertNullable(await _store.GetByIdAsync(id, cancellationToken));

    /// <inheritdoc />
    public async Task<ModelProviderMapping?> GetByModelNameAsync(
        string modelName,
        CancellationToken cancellationToken = default) =>
        (await _store.GetByAliasAsync(modelName, cancellationToken))
            .Select(Convert)
            .FirstOrDefault();

    /// <inheritdoc />
    public async Task<List<ModelProviderMapping>> GetAllByModelNameAsync(
        string modelName,
        CancellationToken cancellationToken = default) =>
        (await _store.GetByAliasAsync(modelName, cancellationToken))
            .Select(Convert)
            .ToList();

    /// <inheritdoc />
    public Task<int?> GetCanonicalModelIdForAssociationAsync(
        int associationId,
        CancellationToken cancellationToken = default) =>
        _store.GetCanonicalModelIdForAssociationAsync(associationId, cancellationToken);

    /// <inheritdoc />
    [Obsolete("Use GetByProviderPaginatedAsync instead. This method loads all records into memory and will be removed in a future version.")]
    public async Task<List<ModelProviderMapping>> GetByProviderAsync(
        ProviderType providerType,
        CancellationToken cancellationToken = default) =>
        (await GetAllUnboundedAsync(cancellationToken))
            .Where(mapping => mapping.Provider.ProviderType == providerType)
            .ToList();

    /// <inheritdoc />
    public async Task<(List<ModelProviderMapping> Items, int TotalCount)> GetByProviderPaginatedAsync(
        int providerId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await _store.GetByProviderPaginatedAsync(
            providerId,
            pageNumber,
            pageSize,
            cancellationToken);
        return (page.Items.Select(Convert).ToList(), page.TotalCount);
    }

    /// <inheritdoc />
    public async Task<List<ModelProviderMapping>> GetByModelIdAsync(
        int modelId,
        CancellationToken cancellationToken = default) =>
        (await _store.GetByModelIdAsync(modelId, cancellationToken))
            .Select(Convert)
            .ToList();

    /// <inheritdoc />
    public async Task<(List<ModelProviderMapping> Items, int TotalCount)> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await _store.GetPaginatedAsync(page, pageSize, cancellationToken);
        return (result.Items.Select(Convert).ToList(), result.TotalCount);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default) =>
        await _store.GetByIdAsync(id, cancellationToken) is not null;

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        (await _store.GetPaginatedAsync(1, 1, cancellationToken)).TotalCount;

    /// <inheritdoc />
    public async Task<List<ModelProviderMapping>> GetAllUnboundedAsync(
        CancellationToken cancellationToken = default)
    {
        var mappings = new List<ModelProviderMapping>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await _store.GetPaginatedAsync(pageNumber, PageSize, cancellationToken);
            mappings.AddRange(page.Items.Select(Convert));
            if (mappings.Count >= page.TotalCount || page.Items.Count == 0)
            {
                return mappings;
            }
        }
    }

    /// <inheritdoc />
    [Obsolete("Use GetAllUnboundedAsync() for cache warming/exports, or GetPaginatedAsync() for bounded queries.")]
    public Task<List<ModelProviderMapping>> GetAllAsync(CancellationToken cancellationToken = default) =>
        GetAllUnboundedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> CreateAsync(
        ModelProviderMapping entity,
        CancellationToken cancellationToken = default) =>
        throw ReadOnlyException();

    /// <inheritdoc />
    public Task<bool> UpdateAsync(
        ModelProviderMapping entity,
        CancellationToken cancellationToken = default) =>
        throw ReadOnlyException();

    /// <inheritdoc />
    public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        throw ReadOnlyException();

    private static NotSupportedException ReadOnlyException() => new(
        "Native Gateway model-routing persistence is read-only; use the JIT Admin service for management operations.");

    private static ModelProviderMapping? ConvertNullable(ModelProviderMappingRuntimeRecord? record) =>
        record is null ? null : Convert(record);

    private static ModelProviderMapping Convert(ModelProviderMappingRuntimeRecord record)
    {
        var association = Convert(record.Association);
        return new ModelProviderMapping
        {
            Id = record.Id,
            ModelAlias = record.ModelAlias,
            ProviderModelId = record.ProviderModelId,
            ProviderId = record.ProviderId,
            IsEnabled = record.IsEnabled,
            RoutingPriority = record.RoutingPriority,
            RoutingWeight = record.RoutingWeight,
            ProviderOptions = record.ProviderOptions,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            Provider = CopyProvider(record.Provider),
            ModelProviderTypeAssociationId = association.Id,
            ModelProviderTypeAssociation = association
        };
    }

    private static Provider CopyProvider(Provider provider) => new()
    {
        Id = provider.Id,
        ProviderType = provider.ProviderType,
        ProviderName = provider.ProviderName,
        BaseUrl = provider.BaseUrl,
        Settings = provider.Settings is null
            ? null
            : new Dictionary<string, string>(provider.Settings),
        IsEnabled = provider.IsEnabled,
        TrustProviderReportedCosts = provider.TrustProviderReportedCosts,
        ProviderCostMarkupMultiplier = provider.ProviderCostMarkupMultiplier,
        CreatedAt = provider.CreatedAt,
        UpdatedAt = provider.UpdatedAt
    };

    private static ModelProviderTypeAssociation Convert(
        ModelProviderTypeAssociationRuntimeRecord record)
    {
        var model = Convert(record.Model);
        var cost = record.ModelCost is null ? null : Convert(record.ModelCost);
        return new ModelProviderTypeAssociation
        {
            Id = record.Id,
            ModelId = record.ModelId,
            IsEnabled = record.IsEnabled,
            MaxInputTokens = record.MaxInputTokens,
            MaxOutputTokens = record.MaxOutputTokens,
            InputModalitiesJson = record.InputModalitiesJson,
            OutputModalitiesJson = record.OutputModalitiesJson,
            OperationalCapabilitiesJson = record.OperationalCapabilitiesJson,
            CapabilitySource = record.CapabilitySource is null
                ? null
                : (ModelCapabilitySource)record.CapabilitySource.Value,
            CapabilitiesLastVerifiedAt = record.CapabilitiesLastVerifiedAt,
            ProviderVariation = record.ProviderVariation,
            QualityScore = record.QualityScore,
            SpeedScore = record.SpeedScore,
            Identifier = record.Identifier,
            Provider = record.ProviderType is null
                ? null
                : (ProviderType)record.ProviderType.Value,
            ModelCostId = record.ModelCostId,
            ModelCost = cost,
            IsPrimary = record.IsPrimary,
            Model = model,
            Metadata = record.Metadata
        };
    }

    private static Model Convert(ModelRuntimeRecord record)
    {
        var series = new ModelSeries
        {
            Id = record.Series.Id,
            AuthorId = record.Series.AuthorId,
            Name = record.Series.Name,
            Description = record.Series.Description,
            TokenizerType = (TokenizerType)record.Series.TokenizerType,
            Parameters = record.Series.Parameters
        };
        return new Model
        {
            Id = record.Id,
            Name = record.Name,
            Version = record.Version,
            Description = record.Description,
            ModelCardUrl = record.ModelCardUrl,
            ModelSeriesId = record.ModelSeriesId,
            Series = series,
            SupportsVision = record.SupportsVision,
            SupportsImageGeneration = record.SupportsImageGeneration,
            SupportsVideoGeneration = record.SupportsVideoGeneration,
            SupportsEmbeddings = record.SupportsEmbeddings,
            SupportsSpeechToText = record.SupportsSpeechToText,
            SupportsTextToSpeech = record.SupportsTextToSpeech,
            SupportsRerank = record.SupportsRerank,
            SupportsChat = record.SupportsChat,
            SupportsFunctionCalling = record.SupportsFunctionCalling,
            SupportsStreaming = record.SupportsStreaming,
            InputModalitiesJson = record.InputModalitiesJson,
            OutputModalitiesJson = record.OutputModalitiesJson,
            CapabilitySource = (ModelCapabilitySource)record.CapabilitySource,
            CapabilitiesLastVerifiedAt = record.CapabilitiesLastVerifiedAt,
            TokenizerType = (TokenizerType)record.TokenizerType,
            MaxInputTokens = record.MaxInputTokens,
            MaxOutputTokens = record.MaxOutputTokens,
            IsActive = record.IsActive,
            ModelParameters = record.ModelParameters,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt
        };
    }

    private static ModelCost Convert(ModelCostRuntimeRecord record) => new()
    {
        Id = record.Id,
        CostName = record.CostName,
        PricingModel = (PricingModel)record.PricingModel,
        PricingConfiguration = record.PricingConfiguration,
        InputCostPerMillionTokens = record.InputCostPerMillionTokens,
        OutputCostPerMillionTokens = record.OutputCostPerMillionTokens,
        EmbeddingCostPerMillionTokens = record.EmbeddingCostPerMillionTokens,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        ModelType = record.ModelType,
        IsActive = record.IsActive,
        EffectiveDate = record.EffectiveDate,
        ExpiryDate = record.ExpiryDate,
        Description = record.Description,
        Priority = record.Priority,
        BatchProcessingMultiplier = record.BatchProcessingMultiplier,
        SupportsBatchProcessing = record.SupportsBatchProcessing,
        CachedInputCostPerMillionTokens = record.CachedInputCostPerMillionTokens,
        CachedInputWriteCostPerMillionTokens = record.CachedInputWriteCostPerMillionTokens,
        CostPerSearchUnit = record.CostPerSearchUnit,
        AudioCostPerMinute = record.AudioCostPerMinute,
        AudioCostPerThousandCharacters = record.AudioCostPerThousandCharacters,
        ReasoningCostPerMillionTokens = record.ReasoningCostPerMillionTokens
    };
}
