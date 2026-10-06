namespace ConduitLLM.Core.Interfaces
{
    /// <summary>
    /// Provides cache stampede prevention for distributed cache operations.
    /// When cache entries expire, this service ensures only one instance performs the
    /// expensive database fallback while other concurrent requests wait.
    /// </summary>
    public interface IDistributedCachePopulator
    {
        Task<T?> GetOrPopulateAsync<T>(string lockKey, Func<CancellationToken, Task<T?>> cacheCheck,
            Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default) where T : class;
        /// <summary>
        /// Gets a value from cache, or populates it using the factory function with stampede prevention.
        /// Uses hybrid locking (local + distributed) to prevent multiple instances from simultaneously
        /// hitting the database when a cache entry expires.
        /// </summary>
        /// <typeparam name="T">The type of the cached value.</typeparam>
        /// <param name="lockKey">A unique key for the distributed lock (e.g., "populate:modelcost:pattern:gpt-4").</param>
        /// <param name="cacheCheck">A function that checks if the value exists in cache and returns it.</param>
        /// <param name="factory">A function that fetches the value from the database when cache is empty.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The cached or freshly populated value, or null if not found.</returns>
        /// <remarks>
        /// The locking strategy is:
        /// 1. Check cache (fast path, no lock)
        /// 2. Acquire local SemaphoreSlim (per-key) to prevent same-instance stampede
        /// 3. Double-check cache after local lock
        /// 4. Acquire distributed lock to prevent cross-instance stampede
        /// 5. Triple-check cache after distributed lock
        /// 6. Call factory (database fallback)
        /// 7. Release locks in reverse order
        /// </remarks>
        Task<T?> GetOrPopulateAsync<T>(
            string lockKey,
            Func<Task<T?>> cacheCheck,
            Func<Task<T?>> factory,
            CancellationToken cancellationToken = default) where T : class;
    }
}
