using ConduitLLM.Persistence;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>
/// Fixed-shape persistence required to resolve Gateway model routes and route policy.
/// </summary>
public interface IModelProviderMappingRuntimeStore
{
    Task<ModelProviderMappingRuntimeRecord?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelProviderMappingRuntimeRecord>> GetByAliasAsync(
        string modelAlias,
        CancellationToken cancellationToken = default);

    Task<ModelProviderMappingRuntimePage> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<ModelProviderMappingRuntimePage> GetByProviderPaginatedAsync(
        int providerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelProviderMappingRuntimeRecord>> GetByModelIdAsync(
        int modelId,
        CancellationToken cancellationToken = default);

    Task<int?> GetCanonicalModelIdForAssociationAsync(
        int associationId,
        CancellationToken cancellationToken = default);

    Task<ModelRoutePolicyRuntimeRecord?> GetRoutePolicyAsync(
        string modelAlias,
        CancellationToken cancellationToken = default);

    Task<ModelCostRuntimeRecord?> GetModelCostByIdAsync(
        int modelCostId,
        CancellationToken cancellationToken = default);

    Task<ModelCostRuntimeRecord?> GetModelCostForIdentifierAsync(
        string modelIdentifier,
        CancellationToken cancellationToken = default);
}
