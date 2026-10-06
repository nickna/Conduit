using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Services;

/// <summary>
/// Service for managing and retrieving model costs. Pure repository operations — caching is handled by the FusionModelCostService decorator.
/// </summary>
public class ModelCostService : IModelCostService
{
    private readonly IModelCostRepository _modelCostRepository;
    private readonly IModelProviderMappingRepository _modelProviderMappingRepository;
    private readonly ILogger<ModelCostService> _logger;

    /// <summary>
    /// Creates a new instance of the ModelCostService
    /// </summary>
    /// <param name="modelCostRepository">The model cost repository</param>
    /// <param name="modelProviderMappingRepository">The model provider mapping repository</param>
    /// <param name="logger">The logger</param>
    public ModelCostService(
        IModelCostRepository modelCostRepository,
        IModelProviderMappingRepository modelProviderMappingRepository,
        ILogger<ModelCostService> logger)
    {
        _modelCostRepository = modelCostRepository ?? throw new ArgumentNullException(nameof(modelCostRepository));
        _modelProviderMappingRepository = modelProviderMappingRepository ?? throw new ArgumentNullException(nameof(modelProviderMappingRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ModelCost?> GetCostForModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("Model ID cannot be empty", nameof(modelId));
        }

        // Get all model costs with their associated ModelProviderTypeAssociations
        var allCosts = await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
            _modelCostRepository.GetPaginatedAsync, cancellationToken: cancellationToken);

        // Find a cost where one of its associated ModelProviderTypeAssociations has this identifier
        var now = DateTime.UtcNow;
        var modelCost = allCosts
            .Where(cost => cost.IsActive && cost.EffectiveDate <= now)
            .Where(cost => !cost.ExpiryDate.HasValue || cost.ExpiryDate.Value > now)
            .Where(cost => cost.ModelProviderTypeAssociations.Any(assoc =>
                assoc.Identifier == modelId && assoc.IsEnabled))
            .OrderByDescending(cost => cost.Priority)
            .ThenByDescending(cost => cost.EffectiveDate)
            .FirstOrDefault();

        if (modelCost == null)
        {
            _logger.LogDebug("No model cost found for identifier: {ModelId}", modelId);
        }

        return modelCost;
    }

    /// <inheritdoc />
    public async Task<ModelCost?> GetCostByIdAsync(int modelCostId, CancellationToken cancellationToken = default)
    {
        var modelCost = await _modelCostRepository.GetByIdAsync(modelCostId, cancellationToken);

        if (modelCost == null)
        {
            _logger.LogDebug("No model cost found for ID: {ModelCostId}", modelCostId);
            return null;
        }

        // Validate the cost is active and within date range
        var now = DateTime.UtcNow;
        if (!modelCost.IsActive || modelCost.EffectiveDate > now ||
            (modelCost.ExpiryDate.HasValue && modelCost.ExpiryDate.Value <= now))
        {
            _logger.LogDebug("Model cost ID {ModelCostId} exists but is not active or outside date range", modelCostId);
            return null;
        }

        return modelCost;
    }

    /// <inheritdoc />
    public async Task<List<ModelCost>> ListModelCostsAsync(CancellationToken cancellationToken = default)
    {
        return await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
            _modelCostRepository.GetPaginatedAsync, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddModelCostAsync(ModelCost modelCost, CancellationToken cancellationToken = default)
    {
        if (modelCost == null)
        {
            throw new ArgumentNullException(nameof(modelCost));
        }

        modelCost.CreatedAt = DateTime.UtcNow;
        modelCost.UpdatedAt = DateTime.UtcNow;

        await _modelCostRepository.CreateAsync(modelCost, cancellationToken);
        _logger.LogInformation("Created model cost {CostName} (ID: {CostId}) with input={InputCost}/M, output={OutputCost}/M",
            modelCost.CostName, modelCost.Id, modelCost.InputCostPerMillionTokens, modelCost.OutputCostPerMillionTokens);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateModelCostAsync(ModelCost modelCost, CancellationToken cancellationToken = default)
    {
        if (modelCost == null)
        {
            throw new ArgumentNullException(nameof(modelCost));
        }

        var existingCost = await _modelCostRepository.GetByIdAsync(modelCost.Id, cancellationToken);

        if (existingCost == null)
        {
            _logger.LogWarning("Attempted to update non-existent model cost {ModelCostId}", modelCost.Id);
            return false;
        }

        // Update properties
        existingCost.CostName = modelCost.CostName;
        existingCost.PricingModel = modelCost.PricingModel;
        existingCost.PricingConfiguration = modelCost.PricingConfiguration;
        existingCost.InputCostPerMillionTokens = modelCost.InputCostPerMillionTokens;
        existingCost.OutputCostPerMillionTokens = modelCost.OutputCostPerMillionTokens;
        existingCost.EmbeddingCostPerMillionTokens = modelCost.EmbeddingCostPerMillionTokens;
        existingCost.ModelType = modelCost.ModelType;
        existingCost.IsActive = modelCost.IsActive;
        existingCost.EffectiveDate = modelCost.EffectiveDate;
        existingCost.ExpiryDate = modelCost.ExpiryDate;
        existingCost.Description = modelCost.Description;
        existingCost.Priority = modelCost.Priority;
        existingCost.BatchProcessingMultiplier = modelCost.BatchProcessingMultiplier;
        existingCost.SupportsBatchProcessing = modelCost.SupportsBatchProcessing;
        existingCost.CachedInputCostPerMillionTokens = modelCost.CachedInputCostPerMillionTokens;
        existingCost.CachedInputWriteCostPerMillionTokens = modelCost.CachedInputWriteCostPerMillionTokens;
        existingCost.CostPerSearchUnit = modelCost.CostPerSearchUnit;
        existingCost.AudioCostPerMinute = modelCost.AudioCostPerMinute;
        existingCost.AudioCostPerThousandCharacters = modelCost.AudioCostPerThousandCharacters;
        existingCost.ReasoningCostPerMillionTokens = modelCost.ReasoningCostPerMillionTokens;
        existingCost.UpdatedAt = DateTime.UtcNow;

        var result = await _modelCostRepository.UpdateAsync(existingCost, cancellationToken);
        if (result)
        {
            _logger.LogInformation("Updated model cost {CostName} (ID: {ModelCostId})", existingCost.CostName, modelCost.Id);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteModelCostAsync(int id, CancellationToken cancellationToken = default)
    {
        var result = await _modelCostRepository.DeleteAsync(id, cancellationToken);
        if (result)
        {
            _logger.LogInformation("Deleted model cost {ModelCostId}", id);
        }
        else
        {
            _logger.LogWarning("Attempted to delete non-existent model cost {ModelCostId}", id);
        }
        return result;
    }

    /// <inheritdoc />
    public Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        // No-op: caching is handled by the FusionModelCostService decorator
        return Task.CompletedTask;
    }
}
