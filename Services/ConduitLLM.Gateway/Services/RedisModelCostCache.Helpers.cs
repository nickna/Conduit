using System.Text.Json;
using StackExchange.Redis;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Redis-based Model Cost cache - Helper methods and event handlers
    /// </summary>
    public partial class RedisModelCostCache
    {
        /// <summary>
        /// Get cache performance statistics
        /// </summary>
        public async Task<CacheStats> GetStatsAsync()
        {
            try
            {
                var (hits, misses, invalidations, resetTime) = await GetBaseStatsAsync(ServiceName);
                var patternMatches = await Database.StringGetAsync(CacheKeys.Stats.PatternMatches());
                var pendingPatternMatches = Interlocked.Read(ref _bufferedPatternMatches);

                return new CacheStats
                {
                    HitCount = hits + PendingHits,
                    MissCount = misses + PendingMisses,
                    InvalidationCount = invalidations + PendingInvalidations,
                    PatternMatchCount = (patternMatches.HasValue ? (long)patternMatches : 0) + pendingPatternMatches,
                    LastResetTime = resetTime,
                    EntryCount = CountEntries(CacheKeys.ModelCost.Prefix + "*")
                };
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting model cost cache statistics");
                return new CacheStats { LastResetTime = DateTime.UtcNow };
            }
        }

        private async Task SetModelCostAsync(ModelCost cost)
        {
            var patternKey = CacheKeys.ModelCost.PatternPrefix + cost.CostName.ToLowerInvariant();

            // Create cached version with pre-parsed configuration
            var cachedCost = ConvertToCachedModelCost(cost);
            await SetCacheEntryAsync(
                patternKey,
                cachedCost,
                GatewayRedisJsonContext.Default.CachedModelCost);

            Logger.LogDebug("Model cost cached for cost name: {CostName}", cost.CostName);
        }

        /// <summary>
        /// Handle single invalidation messages from other instances
        /// </summary>
        private void OnCostInvalidated(RedisChannel channel, RedisValue costId)
        {
            // Fire-and-forget with proper exception handling - don't use async void
            _ = OnCostInvalidatedAsync(costId);
        }

        private async Task OnCostInvalidatedAsync(RedisValue costId)
        {
            try
            {
                if (int.TryParse(costId.ToString(), out var id))
                {
                    await InvalidateModelCostAsync(id);
                    Logger.LogDebug("Invalidated model cost from pub/sub: {CostId}", id);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling cost invalidation: {CostId}", costId.ToString());
            }
        }

        /// <summary>
        /// Handle batch invalidation messages from other instances
        /// </summary>
        private void OnBatchInvalidated(RedisChannel channel, RedisValue message)
        {
            // Fire-and-forget with proper exception handling - don't use async void
            _ = OnBatchInvalidatedAsync(message);
        }

        private async Task OnBatchInvalidatedAsync(RedisValue message)
        {
            try
            {
                var batchMessage = DeserializeBatchInvalidation(message!.ToString());
                if (batchMessage?.CostIds != null)
                {
                    var requests = batchMessage.CostIds.Select(id => new InvalidationRequest
                    {
                        EntityType = CacheType.ModelCost.ToString(),
                        EntityId = id,
                        Reason = "Batch invalidation from pub/sub"
                    });

                    await InvalidateBatchAsync(requests);

                    Logger.LogDebug(
                        "Batch invalidated {Count} model costs from pub/sub",
                        batchMessage.CostIds.Length);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling batch cost invalidation");
            }
        }

        /// <summary>
        /// Converts a ModelCost entity to a CachedModelCost with pre-parsed configuration.
        /// </summary>
        private CachedModelCost ConvertToCachedModelCost(ModelCost cost)
        {
            var cached = new CachedModelCost
            {
                Id = cost.Id,
                CostName = cost.CostName,
                PricingModel = cost.PricingModel,
                InputCostPerMillionTokens = cost.InputCostPerMillionTokens,
                OutputCostPerMillionTokens = cost.OutputCostPerMillionTokens,
                EmbeddingCostPerMillionTokens = cost.EmbeddingCostPerMillionTokens,
                BatchProcessingMultiplier = cost.BatchProcessingMultiplier,
                SupportsBatchProcessing = cost.SupportsBatchProcessing,
                CachedInputCostPerMillionTokens = cost.CachedInputCostPerMillionTokens,
                CachedInputWriteCostPerMillionTokens = cost.CachedInputWriteCostPerMillionTokens,
                CostPerSearchUnit = cost.CostPerSearchUnit,
                ModelType = cost.ModelType,
                IsActive = cost.IsActive,
                Priority = cost.Priority,
                Description = cost.Description
            };

            // Parse pricing configuration based on pricing model
            if (!string.IsNullOrEmpty(cost.PricingConfiguration))
            {
                try
                {
                    cached.ParsedPricingConfiguration = cost.PricingModel switch
                    {
                        PricingModel.PerVideo => JsonSerializer.Deserialize(
                            cost.PricingConfiguration,
                            GatewayRedisJsonContext.Default.PerVideoPricingConfig),
                        PricingModel.PerSecondVideo => JsonSerializer.Deserialize(
                            cost.PricingConfiguration,
                            GatewayRedisJsonContext.Default.PerSecondVideoPricingConfig),
                        PricingModel.InferenceSteps => JsonSerializer.Deserialize(
                            cost.PricingConfiguration,
                            GatewayRedisJsonContext.Default.InferenceStepsPricingConfig),
                        PricingModel.TieredTokens => JsonSerializer.Deserialize(
                            cost.PricingConfiguration,
                            GatewayRedisJsonContext.Default.TieredTokensPricingConfig),
                        PricingModel.PerImage => JsonSerializer.Deserialize(
                            cost.PricingConfiguration,
                            GatewayRedisJsonContext.Default.PerImagePricingConfig),
                        _ => null
                    };
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to parse PricingConfiguration for cost {CostName} with model {PricingModel}",
                        cost.CostName, cost.PricingModel);
                }
            }

            return cached;
        }
    }
}
