using System.Diagnostics;
using System.Text.Json;
using StackExchange.Redis;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Redis-based Virtual Key cache with immediate invalidation for security-critical validation
    /// </summary>
    public class RedisVirtualKeyCache : RedisCacheServiceBase, ConduitLLM.Core.Interfaces.IVirtualKeyCache, IBatchInvalidatable
    {
        private readonly ISubscriber _subscriber;

        public RedisVirtualKeyCache(
            IConnectionMultiplexer redis,
            ILogger<RedisVirtualKeyCache> logger)
            : base(redis, logger, TimeSpan.FromMinutes(30))
        {
            _subscriber = redis.GetSubscriber();

            // Subscribe to invalidation messages
            _subscriber.Subscribe(RedisChannel.Literal(CacheKeys.VirtualKey.InvalidationChannel), OnKeyInvalidated);
            _subscriber.Subscribe(RedisChannel.Literal(CacheKeys.VirtualKey.BatchInvalidationChannel), OnBatchInvalidated);
        }

        #region Legacy Stats Override — VirtualKeyCache uses different key names

        protected override Task TrackHitAsync(string serviceName)
            => Database.StringIncrementAsync(CacheKeys.Stats.VirtualKeyHits);

        protected override Task TrackMissAsync(string serviceName)
            => Database.StringIncrementAsync(CacheKeys.Stats.VirtualKeyMisses);

        protected override Task TrackInvalidationAsync(string serviceName, long count = 1)
            => Database.StringIncrementAsync(CacheKeys.Stats.VirtualKeyInvalidations, count);

        #endregion

        /// <summary>
        /// Get Virtual Key from cache with immediate fallback to database if not found
        /// </summary>
        public async Task<VirtualKey?> GetVirtualKeyAsync(
            string keyHash,
            Func<string, Task<VirtualKey?>> databaseFallback)
        {
            var cacheKey = CacheKeys.VirtualKey.ByHash(keyHash);
            var sw = Stopwatch.StartNew();

            try
            {
                // Try Redis first - this is ~50x faster than database
                var virtualKey = await TryGetCacheEntryAsync(
                    cacheKey,
                    GatewayRedisJsonContext.CachedVirtualKey);

                if (virtualKey != null)
                {
                    // Validate key is still enabled and not expired
                    if (IsKeyValid(virtualKey))
                    {
                        Logger.LogDebug("Virtual Key cache hit: {KeyHash}", keyHash);
                        await TrackHitAsync(CacheKeys.Stats.VirtualKeyService);
                        GatewayCacheMetrics.RecordHit("virtualkey");
                        GatewayCacheMetrics.RecordLatency("virtualkey", "get", sw.Elapsed.TotalSeconds);
                        return virtualKey;
                    }

                    // Invalid key in cache, remove it
                    await Database.KeyDeleteAsync(cacheKey);
                    Logger.LogDebug("Removed invalid Virtual Key from cache: {KeyHash}", keyHash);
                }

                // Cache miss or invalid key - fallback to database
                Logger.LogDebug("Virtual Key cache miss, querying database: {KeyHash}", keyHash);
                await TrackMissAsync(CacheKeys.Stats.VirtualKeyService);
                GatewayCacheMetrics.RecordMiss("virtualkey");
                var dbKey = await databaseFallback(keyHash);

                if (dbKey != null && IsKeyValid(dbKey))
                {
                    await SetVirtualKeyAsync(keyHash, dbKey);
                    return dbKey;
                }

                GatewayCacheMetrics.RecordLatency("virtualkey", "get", sw.Elapsed.TotalSeconds);
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error accessing Virtual Key cache, falling back to database: {KeyHash}", keyHash);
                GatewayCacheMetrics.RecordError("virtualkey", "get");
                return await databaseFallback(keyHash);
            }
        }

        /// <summary>
        /// Cache a Virtual Key with automatic expiry
        /// </summary>
        public async Task SetVirtualKeyAsync(string keyHash, VirtualKey virtualKey)
        {
            var cacheKey = CacheKeys.VirtualKey.ByHash(keyHash);

            try
            {
                var expiry = CalculateExpiry(virtualKey);

                await SetCacheEntryAsync(
                    cacheKey,
                    virtualKey,
                    GatewayRedisJsonContext.CachedVirtualKey,
                    expiry);

                Logger.LogDebug("Cached Virtual Key: {KeyHash}, expires in {ExpiryMinutes} minutes",
                    keyHash, expiry.TotalMinutes);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error caching Virtual Key: {KeyHash}", keyHash);
                // Don't throw - caching is optimization, not critical path
            }
        }

        /// <summary>
        /// Immediately invalidate a Virtual Key across all instances
        /// CRITICAL: Call this when keys are disabled, deleted, or quota exceeded
        /// </summary>
        public async Task InvalidateVirtualKeyAsync(string keyHash)
        {
            var cacheKey = CacheKeys.VirtualKey.ByHash(keyHash);

            try
            {
                await Database.KeyDeleteAsync(cacheKey);
                await _subscriber.PublishAsync(RedisChannel.Literal(CacheKeys.VirtualKey.InvalidationChannel), keyHash);
                await TrackInvalidationAsync(CacheKeys.Stats.VirtualKeyService);
                GatewayCacheMetrics.RecordInvalidation("virtualkey", "explicit");

                Logger.LogInformation("Invalidated Virtual Key across all instances: {KeyHash}", keyHash);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error invalidating Virtual Key: {KeyHash}", keyHash);
                GatewayCacheMetrics.RecordError("virtualkey", "invalidate");
                throw; // This is critical for security - must not fail silently
            }
        }

        /// <summary>
        /// Bulk invalidate multiple keys (useful for quota updates)
        /// </summary>
        public async Task InvalidateVirtualKeysAsync(string[] keyHashes)
        {
            var tasks = new Task[keyHashes.Length];
            for (int i = 0; i < keyHashes.Length; i++)
            {
                tasks[i] = InvalidateVirtualKeyAsync(keyHashes[i]);
            }

            await Task.WhenAll(tasks);

            Logger.LogInformation("Bulk invalidated {Count} Virtual Keys", keyHashes.Length);
        }

        /// <summary>
        /// Get cache statistics for monitoring
        /// </summary>
        public async Task<CacheStats> GetStatsAsync()
        {
            try
            {
                var hitCountTask = Database.StringGetAsync(CacheKeys.Stats.VirtualKeyHits);
                var missCountTask = Database.StringGetAsync(CacheKeys.Stats.VirtualKeyMisses);
                var invalidationCountTask = Database.StringGetAsync(CacheKeys.Stats.VirtualKeyInvalidations);
                var resetTimeTask = Database.StringGetAsync(CacheKeys.Stats.VirtualKeyResetTime);

                await Task.WhenAll(hitCountTask, missCountTask, invalidationCountTask, resetTimeTask);

                var hitCountValue = await hitCountTask;
                var missCountValue = await missCountTask;
                var invalidationCountValue = await invalidationCountTask;
                var resetTimeValue = await resetTimeTask;

                long hitCount = hitCountValue.HasValue ? (long)hitCountValue : 0;
                long missCount = missCountValue.HasValue ? (long)missCountValue : 0;
                long invalidationCount = invalidationCountValue.HasValue ? (long)invalidationCountValue : 0;

                DateTime lastResetTime = DateTime.UtcNow;
                if (resetTimeValue.HasValue)
                {
                    if (long.TryParse(resetTimeValue.ToString(), out var ticks))
                    {
                        lastResetTime = new DateTime(ticks, DateTimeKind.Utc);
                    }
                }
                else
                {
                    await Database.StringSetAsync(CacheKeys.Stats.VirtualKeyResetTime, DateTime.UtcNow.Ticks.ToString());
                }

                return new CacheStats
                {
                    HitCount = hitCount,
                    MissCount = missCount,
                    InvalidationCount = invalidationCount,
                    AverageGetTime = TimeSpan.Zero,
                    LastResetTime = lastResetTime
                };
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting cache statistics");
                return new CacheStats();
            }
        }

        #region Pub/Sub Handlers

        private void OnKeyInvalidated(RedisChannel channel, RedisValue keyHash)
        {
            _ = OnKeyInvalidatedAsync(keyHash);
        }

        private async Task OnKeyInvalidatedAsync(RedisValue keyHash)
        {
            try
            {
                var cacheKey = CacheKeys.VirtualKey.ByHash(keyHash.ToString());
                await Database.KeyDeleteAsync(cacheKey);

                Logger.LogDebug("Invalidated Virtual Key from pub/sub: {KeyHash}", keyHash.ToString());
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling key invalidation: {KeyHash}", keyHash.ToString());
            }
        }

        private void OnBatchInvalidated(RedisChannel channel, RedisValue message)
        {
            _ = OnBatchInvalidatedAsync(message);
        }

        private async Task OnBatchInvalidatedAsync(RedisValue message)
        {
            try
            {
                var batchMessage = JsonSerializer.Deserialize(
                    message!.ToString(),
                    GatewayRedisJsonContext.Default.VirtualKeyBatchInvalidation);
                if (batchMessage?.KeyHashes != null)
                {
                    var batch = Database.CreateBatch();
                    var deleteTasks = new List<Task<bool>>();

                    foreach (var keyHash in batchMessage.KeyHashes)
                    {
                        var cacheKey = CacheKeys.VirtualKey.ByHash(keyHash);
                        deleteTasks.Add(batch.KeyDeleteAsync(cacheKey));
                    }

                    batch.Execute();
                    await Task.WhenAll(deleteTasks);

                    Logger.LogDebug(
                        "Batch invalidated {Count} virtual keys from pub/sub",
                        batchMessage.KeyHashes.Length);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling batch key invalidation");
            }
        }

        #endregion

        #region Batch Invalidation

        /// <summary>
        /// Batch invalidate multiple virtual keys for optimal performance
        /// </summary>
        public async Task<BatchInvalidationResult> InvalidateBatchAsync(
            IEnumerable<InvalidationRequest> requests,
            CancellationToken cancellationToken = default)
        {
            var keyHashes = requests
                .Where(r => r.EntityType == CacheType.VirtualKey.ToString())
                .Select(r => CacheKeys.VirtualKey.ByHash(r.EntityId))
                .ToArray();

            if (keyHashes.Length == 0)
            {
                return new BatchInvalidationResult
                {
                    Success = true,
                    ProcessedCount = 0,
                    Duration = TimeSpan.Zero
                };
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var batch = Database.CreateBatch();
                var deleteTasks = new List<Task<bool>>();

                foreach (var key in keyHashes)
                {
                    deleteTasks.Add(batch.KeyDeleteAsync(key));
                }

                batch.Execute();
                await Task.WhenAll(deleteTasks);

                await TrackInvalidationAsync(CacheKeys.Stats.VirtualKeyService, keyHashes.Length);

                // Publish batch invalidation message to other instances
                var batchMessage = new VirtualKeyBatchInvalidation
                {
                    KeyHashes = keyHashes.Select(k => k.Replace(CacheKeys.VirtualKey.Prefix, "")).ToArray(),
                    Timestamp = DateTime.UtcNow
                };

                await _subscriber.PublishAsync(
                    RedisChannel.Literal(CacheKeys.VirtualKey.BatchInvalidationChannel),
                    JsonSerializer.Serialize(
                        batchMessage,
                        GatewayRedisJsonContext.Default.VirtualKeyBatchInvalidation));

                stopwatch.Stop();

                Logger.LogInformation(
                    "Batch invalidated {Count} virtual keys in {Duration}ms",
                    keyHashes.Length,
                    stopwatch.ElapsedMilliseconds);

                return new BatchInvalidationResult
                {
                    Success = true,
                    ProcessedCount = keyHashes.Length,
                    Duration = stopwatch.Elapsed
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Logger.LogError(ex, "Failed to batch invalidate virtual keys");

                return new BatchInvalidationResult
                {
                    Success = false,
                    ProcessedCount = 0,
                    Duration = stopwatch.Elapsed,
                    Error = ex.Message
                };
            }
        }

        #endregion

        #region Helpers

        private static bool IsKeyValid(VirtualKey key)
        {
            return key.IsEnabled &&
                   (key.ExpiresAt == null || key.ExpiresAt > DateTime.UtcNow);
        }

        private TimeSpan CalculateExpiry(VirtualKey key)
        {
            if (key.ExpiresAt.HasValue)
            {
                var timeUntilExpiry = key.ExpiresAt.Value - DateTime.UtcNow;
                if (timeUntilExpiry < DefaultExpiry)
                {
                    return timeUntilExpiry;
                }
            }

            return DefaultExpiry;
        }

        #endregion

        internal sealed class VirtualKeyBatchInvalidation
        {
            public string[] KeyHashes { get; set; } = Array.Empty<string>();
            public DateTime Timestamp { get; set; }
        }
    }
}
