using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Configuration.Services;

/// <summary>
/// Read-only request-time model-cost service backed by the fixed-shape routing store.
/// Native Gateway uses this adapter while Admin and JIT Gateway retain the full
/// management service and caching decorator.
/// </summary>
public sealed class StoreBackedModelCostService : IModelCostService
{
    private readonly IModelProviderMappingRuntimeStore _store;

    public StoreBackedModelCostService(IModelProviderMappingRuntimeStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async Task<ModelCost?> GetCostForModelAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return MapActive(await _store.GetModelCostForIdentifierAsync(modelId, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<ModelCost?> GetCostByIdAsync(
        int modelCostId,
        CancellationToken cancellationToken = default) =>
        MapActive(await _store.GetModelCostByIdAsync(modelCostId, cancellationToken));

    /// <inheritdoc />
    public Task<List<ModelCost>> ListModelCostsAsync(CancellationToken cancellationToken = default) =>
        throw ManagementNotSupported();

    /// <inheritdoc />
    public Task AddModelCostAsync(
        ModelCost modelCost,
        CancellationToken cancellationToken = default) =>
        throw ManagementNotSupported();

    /// <inheritdoc />
    public Task<bool> UpdateModelCostAsync(
        ModelCost modelCost,
        CancellationToken cancellationToken = default) =>
        throw ManagementNotSupported();

    /// <inheritdoc />
    public Task<bool> DeleteModelCostAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        throw ManagementNotSupported();

    /// <inheritdoc />
    public Task ClearCacheAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private static ModelCost? MapActive(ModelCostRuntimeRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (!record.IsActive || record.EffectiveDate > now || record.ExpiryDate <= now)
        {
            return null;
        }

        return new ModelCost
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

    private static NotSupportedException ManagementNotSupported() => new(
        "The native Gateway model-cost service is read-only; use the Admin service for management operations.");
}
