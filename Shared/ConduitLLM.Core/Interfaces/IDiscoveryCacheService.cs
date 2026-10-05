using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Interfaces
{
    /// <summary>
    /// Service for caching discovery endpoint results
    /// </summary>
    public interface IDiscoveryCacheService
    {
        /// <summary>Loads one discovery variant, coalescing healthy misses in the selected cache implementation.</summary>
        async Task<DiscoveryModelsResult> GetOrLoadAsync(string cacheKey,
            Func<CancellationToken, Task<DiscoveryModelsResult>> load, CancellationToken cancellationToken = default)
        {
            var cached = await GetDiscoveryResultsAsync(cacheKey, cancellationToken);
            if (cached is not null) return cached;
            var result = await load(cancellationToken);
            await SetDiscoveryResultsAsync(cacheKey, result, cancellationToken);
            return result;
        }

        /// <summary>
        /// Gets cached discovery results for models
        /// </summary>
        /// <param name="cacheKey">Cache key for the results</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Cached discovery results or null if not found</returns>
        Task<DiscoveryModelsResult?> GetDiscoveryResultsAsync(string cacheKey, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets discovery results in cache
        /// </summary>
        /// <param name="cacheKey">Cache key for the results</param>
        /// <param name="results">Discovery results to cache</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task SetDiscoveryResultsAsync(string cacheKey, DiscoveryModelsResult results, CancellationToken cancellationToken = default);

        /// <summary>
        /// Invalidates all discovery cache entries
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        Task InvalidateAllDiscoveryAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Invalidates discovery cache entries matching a pattern
        /// </summary>
        /// <param name="pattern">Pattern to match (e.g., "discovery:models:*")</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task InvalidatePatternAsync(string pattern, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets discovery cache statistics
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Cache statistics</returns>
        Task<CacheStats> GetStatisticsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents cached discovery models result
    /// </summary>
    public class DiscoveryModelsResult
    {
        /// <summary>
        /// List of discovered models serialized as JsonElement for reliable cache round-tripping.
        /// Anonymous objects cannot survive JSON deserialization, so we store them as JsonElement
        /// which serializes/deserializes correctly and produces the same JSON output for API consumers.
        /// </summary>
        public List<JsonElement> Data { get; set; } = new();

        /// <summary>
        /// Total count of models
        /// </summary>
        public int Count { get; set; }

        /// <summary>
        /// When this result was cached
        /// </summary>
        public DateTime CachedAt { get; set; }

        /// <summary>
        /// Optional capability filter that was applied
        /// </summary>
        public string? CapabilityFilter { get; set; }
    }

}
