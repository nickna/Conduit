using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Interfaces;

/// <summary>
/// Service for caching function discovery results (tool definitions)
/// </summary>
public interface IFunctionDiscoveryCacheService
{
    Task<List<Tool>> GetOrLoadAsync(List<int> ids, Func<CancellationToken, Task<FunctionDiscoveryLoad>> load,
        int? ttlMinutes = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Gets cached tool definitions for a set of function configuration IDs
    /// </summary>
    /// <param name="functionConfigurationIds">List of function configuration IDs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cached tool definitions or null if not found or caching is disabled</returns>
    Task<List<Tool>?> GetCachedToolsAsync(
        List<int> functionConfigurationIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates all function discovery cache entries
    /// Called when function configurations are created, updated, or deleted
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task InvalidateAllFunctionDiscoveryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates cache entries for a specific function configuration
    /// </summary>
    /// <param name="functionConfigurationId">Function configuration ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task InvalidateFunctionConfigurationAsync(
        int functionConfigurationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if function discovery caching is enabled globally
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if caching is enabled, false otherwise</returns>
    Task<bool> IsCachingEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets function discovery cache statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cache statistics</returns>
    Task<CacheStats> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

public sealed record FunctionDiscoveryLoad(List<Tool> Tools, int? TtlMinutes);
