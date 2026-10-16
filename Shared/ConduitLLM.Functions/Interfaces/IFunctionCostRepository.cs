using ConduitLLM.Functions.Entities;

namespace ConduitLLM.Functions.Interfaces;

/// <summary>
/// Repository interface for managing function cost configurations
/// </summary>
public interface IFunctionCostRepository
{
    /// <summary>
    /// Gets a function cost by ID
    /// </summary>
    /// <param name="id">The cost ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function cost or null if not found</returns>
    Task<FunctionCost?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a function cost by cost name
    /// </summary>
    /// <param name="costName">The cost name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function cost or null if not found</returns>
    Task<FunctionCost?> GetByCostNameAsync(string costName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all function costs
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A list of all function costs</returns>
    Task<List<FunctionCost>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active function costs
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A list of active function costs</returns>
    Task<List<FunctionCost>> GetAllActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the highest-priority eligible cost associated by an active mapping with the function configuration.
    /// Costs must be active, effective at or before the current UTC time, and unexpired (expiry is exclusive).
    /// Ineligible costs are excluded before ranking; equal priorities prefer the newest effective date.
    /// </summary>
    /// <param name="functionConfigurationId">The function configuration ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The best eligible function cost, or null when no eligible actively mapped cost exists.</returns>
    Task<FunctionCost?> GetActiveCostForFunctionAsync(int functionConfigurationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new function cost
    /// </summary>
    /// <param name="functionCost">The cost to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The ID of the created cost</returns>
    Task<int> CreateAsync(FunctionCost functionCost, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing function cost
    /// </summary>
    /// <param name="functionCost">The cost to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the entity was updated</returns>
    Task<bool> UpdateAsync(FunctionCost functionCost, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a function cost by ID
    /// </summary>
    /// <param name="id">The cost ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the entity was deleted</returns>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
