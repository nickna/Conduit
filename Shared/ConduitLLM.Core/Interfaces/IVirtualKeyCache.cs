using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Interfaces
{
    /// <summary>
    /// Cache interface for Virtual Key operations with immediate invalidation
    /// </summary>
    public interface IVirtualKeyCache
    {
        /// <summary>
        /// Get Virtual Key from cache with database fallback
        /// </summary>
        /// <param name="keyHash">The hashed key to look up</param>
        /// <param name="databaseFallback">Function to fetch from database on cache miss</param>
        /// <returns>Virtual Key entity or null if not found</returns>
        Task<VirtualKey?> GetVirtualKeyAsync(string keyHash, Func<string, Task<VirtualKey?>> databaseFallback);

        /// <summary>
        /// Immediately invalidate a Virtual Key in cache and notify all instances
        /// SECURITY CRITICAL: Used when keys are disabled/compromised
        /// </summary>
        /// <param name="keyHash">The hashed key to invalidate</param>
        Task InvalidateVirtualKeyAsync(string keyHash);

        /// <summary>
        /// Bulk invalidate multiple Virtual Keys efficiently
        /// </summary>
        /// <param name="keyHashes">Array of hashed keys to invalidate</param>
        Task InvalidateVirtualKeysAsync(string[] keyHashes, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get cache performance statistics
        /// </summary>
        Task<CacheStats> GetStatsAsync();
    }
}
