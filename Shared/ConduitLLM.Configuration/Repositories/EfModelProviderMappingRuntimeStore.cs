using ConduitLLM.Configuration.Entities;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// Fixed-query EF reference adapter for Gateway model routing and route policy.
/// </summary>
public sealed class EfModelProviderMappingRuntimeStore : IModelProviderMappingRuntimeStore
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfModelProviderMappingRuntimeStore(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    /// <inheritdoc />
    public async Task<ModelProviderMappingRuntimeRecord?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var mapping = await RuntimeGraph(context)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return mapping is null ? null : Map(mapping);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelProviderMappingRuntimeRecord>> GetByAliasAsync(
        string modelAlias,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);
        var normalizedAlias = modelAlias.ToLower();
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var mappings = await RuntimeGraph(context)
            .Where(mapping => mapping.ModelAlias.ToLower() == normalizedAlias)
            .OrderBy(mapping => mapping.RoutingPriority)
            .ThenBy(mapping => mapping.Id)
            .ToListAsync(cancellationToken);
        return mappings.Select(Map).ToArray();
    }

    /// <inheritdoc />
    public async Task<ModelProviderMappingRuntimePage> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePagination(page, pageSize);
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var totalCount = await context.ModelProviderMappings.CountAsync(cancellationToken);
        var mappings = await RuntimeGraph(context)
            .OrderBy(mapping => mapping.ModelAlias)
            .ThenBy(mapping => mapping.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new ModelProviderMappingRuntimePage(mappings.Select(Map).ToArray(), totalCount);
    }

    /// <inheritdoc />
    public async Task<ModelProviderMappingRuntimePage> GetByProviderPaginatedAsync(
        int providerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePagination(page, pageSize);
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var totalCount = await context.ModelProviderMappings
            .CountAsync(mapping => mapping.ProviderId == providerId, cancellationToken);
        var mappings = await RuntimeGraph(context)
            .Where(mapping => mapping.ProviderId == providerId)
            .OrderBy(mapping => mapping.ModelAlias)
            .ThenBy(mapping => mapping.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new ModelProviderMappingRuntimePage(mappings.Select(Map).ToArray(), totalCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelProviderMappingRuntimeRecord>> GetByModelIdAsync(
        int modelId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var mappings = await RuntimeGraph(context)
            .Where(mapping => mapping.ModelProviderTypeAssociation.ModelId == modelId)
            .OrderBy(mapping => mapping.ModelAlias)
            .ThenBy(mapping => mapping.Id)
            .ToListAsync(cancellationToken);
        return mappings.Select(Map).ToArray();
    }

    /// <inheritdoc />
    public async Task<int?> GetCanonicalModelIdForAssociationAsync(
        int associationId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ModelProviderTypeAssociations
            .AsNoTracking()
            .Where(association => association.Id == associationId)
            .Select(association => (int?)association.ModelId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ModelRoutePolicyRuntimeRecord?> GetRoutePolicyAsync(
        string modelAlias,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ModelRoutePolicies
            .AsNoTracking()
            .Where(policy => policy.ModelAlias == modelAlias)
            .Select(policy => new ModelRoutePolicyRuntimeRecord
            {
                Id = policy.Id,
                ModelAlias = policy.ModelAlias,
                Strategy = policy.Strategy,
                CostWeight = policy.CostWeight,
                SpeedWeight = policy.SpeedWeight,
                QualityWeight = policy.QualityWeight,
                CacheAffinityEnabled = policy.CacheAffinityEnabled,
                AffinityTtlSeconds = policy.AffinityTtlSeconds,
                MaxAffinityScorePenalty = policy.MaxAffinityScorePenalty,
                IsEnabled = policy.IsEnabled,
                CreatedAt = policy.CreatedAt,
                UpdatedAt = policy.UpdatedAt
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ModelCostRuntimeRecord?> GetModelCostByIdAsync(
        int modelCostId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var cost = await context.ModelCosts
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == modelCostId, cancellationToken);
        return cost is null ? null : MapCost(cost);
    }

    /// <inheritdoc />
    public async Task<ModelCostRuntimeRecord?> GetModelCostForIdentifierAsync(
        string modelIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentifier);
        var now = DateTime.UtcNow;
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var cost = await context.ModelCosts
            .AsNoTracking()
            .Where(candidate => candidate.IsActive && candidate.EffectiveDate <= now)
            .Where(candidate => !candidate.ExpiryDate.HasValue || candidate.ExpiryDate > now)
            .Where(candidate => candidate.ModelProviderTypeAssociations.Any(association =>
                association.Identifier == modelIdentifier && association.IsEnabled))
            .OrderByDescending(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.EffectiveDate)
            .FirstOrDefaultAsync(cancellationToken);
        return cost is null ? null : MapCost(cost);
    }

    private static IQueryable<ModelProviderMapping> RuntimeGraph(ConduitDbContext context) =>
        context.ModelProviderMappings
            .AsNoTracking()
            .Include(mapping => mapping.Provider)
            .Include(mapping => mapping.ModelProviderTypeAssociation)
                .ThenInclude(association => association.ModelCost)
            .Include(mapping => mapping.ModelProviderTypeAssociation)
                .ThenInclude(association => association.Model)
                    .ThenInclude(model => model.Series);

    private static ModelProviderMappingRuntimeRecord Map(ModelProviderMapping mapping) => new()
    {
        Id = mapping.Id,
        ModelAlias = mapping.ModelAlias,
        ProviderModelId = mapping.ProviderModelId,
        ProviderId = mapping.ProviderId,
        IsEnabled = mapping.IsEnabled,
        RoutingPriority = mapping.RoutingPriority,
        RoutingWeight = mapping.RoutingWeight,
        ProviderOptions = mapping.ProviderOptions,
        CreatedAt = mapping.CreatedAt,
        UpdatedAt = mapping.UpdatedAt,
        Provider = CopyProvider(mapping.Provider),
        Association = MapAssociation(mapping.ModelProviderTypeAssociation)
    };

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

    private static ModelProviderTypeAssociationRuntimeRecord MapAssociation(
        ModelProviderTypeAssociation association) => new()
    {
        Id = association.Id,
        ModelId = association.ModelId,
        IsEnabled = association.IsEnabled,
        MaxInputTokens = association.MaxInputTokens,
        MaxOutputTokens = association.MaxOutputTokens,
        InputModalitiesJson = association.InputModalitiesJson,
        OutputModalitiesJson = association.OutputModalitiesJson,
        OperationalCapabilitiesJson = association.OperationalCapabilitiesJson,
        CapabilitySource = association.CapabilitySource is null
            ? null
            : (int)association.CapabilitySource.Value,
        CapabilitiesLastVerifiedAt = association.CapabilitiesLastVerifiedAt,
        ProviderVariation = association.ProviderVariation,
        QualityScore = association.QualityScore,
        SpeedScore = association.SpeedScore,
        Identifier = association.Identifier,
        ProviderType = association.Provider is null ? null : (int)association.Provider.Value,
        ModelCostId = association.ModelCostId,
        IsPrimary = association.IsPrimary,
        Metadata = association.Metadata,
        Model = MapModel(association.Model),
        ModelCost = association.ModelCost is null ? null : MapCost(association.ModelCost)
    };

    private static ModelRuntimeRecord MapModel(Model model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Version = model.Version,
        Description = model.Description,
        ModelCardUrl = model.ModelCardUrl,
        ModelSeriesId = model.ModelSeriesId,
        SupportsVision = model.SupportsVision,
        SupportsImageGeneration = model.SupportsImageGeneration,
        SupportsVideoGeneration = model.SupportsVideoGeneration,
        SupportsEmbeddings = model.SupportsEmbeddings,
        SupportsSpeechToText = model.SupportsSpeechToText,
        SupportsTextToSpeech = model.SupportsTextToSpeech,
        SupportsRerank = model.SupportsRerank,
        SupportsChat = model.SupportsChat,
        SupportsFunctionCalling = model.SupportsFunctionCalling,
        SupportsStreaming = model.SupportsStreaming,
        InputModalitiesJson = model.InputModalitiesJson,
        OutputModalitiesJson = model.OutputModalitiesJson,
        CapabilitySource = (int)model.CapabilitySource,
        CapabilitiesLastVerifiedAt = model.CapabilitiesLastVerifiedAt,
        TokenizerType = (int)model.TokenizerType,
        MaxInputTokens = model.MaxInputTokens,
        MaxOutputTokens = model.MaxOutputTokens,
        IsActive = model.IsActive,
        ModelParameters = model.ModelParameters,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
        Series = new ModelSeriesRuntimeRecord
        {
            Id = model.Series.Id,
            AuthorId = model.Series.AuthorId,
            Name = model.Series.Name,
            Description = model.Series.Description,
            TokenizerType = (int)model.Series.TokenizerType,
            Parameters = model.Series.Parameters
        }
    };

    private static ModelCostRuntimeRecord MapCost(ModelCost cost) => new()
    {
        Id = cost.Id,
        CostName = cost.CostName,
        PricingModel = (int)cost.PricingModel,
        PricingConfiguration = cost.PricingConfiguration,
        InputCostPerMillionTokens = cost.InputCostPerMillionTokens,
        OutputCostPerMillionTokens = cost.OutputCostPerMillionTokens,
        EmbeddingCostPerMillionTokens = cost.EmbeddingCostPerMillionTokens,
        CreatedAt = cost.CreatedAt,
        UpdatedAt = cost.UpdatedAt,
        ModelType = cost.ModelType,
        IsActive = cost.IsActive,
        EffectiveDate = cost.EffectiveDate,
        ExpiryDate = cost.ExpiryDate,
        Description = cost.Description,
        Priority = cost.Priority,
        BatchProcessingMultiplier = cost.BatchProcessingMultiplier,
        SupportsBatchProcessing = cost.SupportsBatchProcessing,
        CachedInputCostPerMillionTokens = cost.CachedInputCostPerMillionTokens,
        CachedInputWriteCostPerMillionTokens = cost.CachedInputWriteCostPerMillionTokens,
        CostPerSearchUnit = cost.CostPerSearchUnit,
        AudioCostPerMinute = cost.AudioCostPerMinute,
        AudioCostPerThousandCharacters = cost.AudioCostPerThousandCharacters,
        ReasoningCostPerMillionTokens = cost.ReasoningCostPerMillionTokens
    };

    private static (int Page, int PageSize) NormalizePagination(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize));
}
