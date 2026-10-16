using ConduitLLM.Functions.Entities;

namespace ConduitLLM.Functions.Interfaces;

/// <summary>
/// Service for managing function cost configurations with caching.
/// </summary>
/// <remarks>
/// This service is analogous to IModelCostService for LLM operations.
/// It provides cached access to function cost configurations and handles
/// priority-based selection when multiple active costs exist.
///
/// Caching Strategy:
/// - L1 Cache: In-process Memory (15 minute TTL)
/// - L2 Cache: Redis Distributed (1 hour TTL)
/// - Priority Selection: Higher priority wins when multiple costs are active
/// - Time Filtering: Respects EffectiveDate and ExpiryDate
/// </remarks>
public interface IFunctionCostService
{
    /// <summary>
    /// Gets the active cost configuration for a function configuration.
    /// </summary>
    /// <param name="functionConfigurationId">ID of the function configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active FunctionCost, or null if no active cost is found.</returns>
    /// <remarks>
    /// Selection logic:
    /// 1. Filters by IsActive = true
    /// 2. Filters by EffectiveDate &lt;= now &lt; ExpiryDate
    /// 3. Orders by Priority descending
    /// 4. Returns highest priority cost
    ///
    /// Results are cached for performance.
    /// </remarks>
    Task<FunctionCost?> GetCostForConfigurationAsync(
        int functionConfigurationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a function cost by ID.
    /// </summary>
    /// <param name="costId">ID of the cost configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The FunctionCost, or null if not found.</returns>
    Task<FunctionCost?> GetCostByIdAsync(
        int costId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all function costs, optionally filtered by active status.
    /// </summary>
    /// <param name="activeOnly">If true, returns only active costs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of function costs.</returns>
    Task<List<FunctionCost>> ListCostsAsync(
        bool activeOnly = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new function cost configuration.
    /// </summary>
    /// <param name="cost">The cost configuration to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>ID of the created cost configuration.</returns>
    Task<int> CreateCostAsync(
        FunctionCost cost,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing function cost configuration.
    /// </summary>
    /// <param name="cost">The cost configuration to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateCostAsync(
        FunctionCost cost,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a function cost configuration.
    /// </summary>
    /// <param name="costId">ID of the cost configuration to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteCostAsync(
        int costId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached cost configurations.
    /// </summary>
    /// <remarks>
    /// Call this after creating, updating, or deleting cost configurations
    /// to ensure changes are immediately reflected.
    /// Invalidates all list, cost-ID and configuration entries in both tiers by
    /// rotating their shared namespace. Other service instances recheck that
    /// namespace before using cached data. Retired payloads expire normally.
    /// An overlapping read may finish with its old snapshot but cannot populate
    /// the new namespace. A failed distributed invalidation is reported to the caller.
    /// </remarks>
    Task ClearCacheAsync();
}
