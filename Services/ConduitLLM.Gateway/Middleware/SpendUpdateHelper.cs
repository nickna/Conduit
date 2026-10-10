using ConduitLLM.Core.Interfaces;
using ConduitLLM.Configuration.Exceptions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Gateway.Metrics;
using IVirtualKeyRuntimeService = ConduitLLM.Core.Interfaces.IVirtualKeyRuntimeService;

namespace ConduitLLM.Gateway.Middleware
{
    /// <summary>
    /// Helper methods for updating virtual key spending with 3-tier fallback:
    /// 1. Redis batch queue (primary, high throughput)
    /// 2. Direct database write (fallback when Redis is down)
    /// 3. In-memory fallback queue (last resort, drained on next flush cycle)
    /// </summary>
    public static class SpendUpdateHelper
    {
        /// <summary>
        /// Updates virtual key spending with cascading fallback to prevent data loss.
        /// </summary>
        public static async Task UpdateSpendAsync(
            int virtualKeyId,
            decimal cost,
            IBatchSpendUpdateService batchSpendService,
            IVirtualKeyRuntimeService virtualKeyService,
            ILogger logger,
            DateTime? billedAtUtc = null)
        {
            // Tier 1: Try Redis batch queue (primary path)
            if (batchSpendService.IsHealthy)
            {
                try
                {
                    await batchSpendService.QueueSpendUpdateAsync(virtualKeyId, cost, billedAtUtc);
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Redis batch queue failed for VirtualKey {VirtualKeyId} ({Cost:C}), falling back to direct DB update",
                        virtualKeyId, cost);
                    BillingMetrics.RecordSpendUpdateFailure("redis_queue_failed");
                }
            }
            else
            {
                logger.LogWarning("BatchSpendUpdateService unhealthy, using direct update for VirtualKey {VirtualKeyId}", virtualKeyId);
            }

            // Tier 2: Try direct database write. UpdateSpendAsync reports expected
            // persistence failures with false, so do not treat task completion alone
            // as a successful charge.
            Exception? databaseFailure = null;
            try
            {
                if (await virtualKeyService.UpdateSpendAsync(virtualKeyId, cost))
                {
                    return;
                }

                logger.LogError(
                    "Direct DB spend update returned false for VirtualKey {VirtualKeyId} ({Cost:C})",
                    virtualKeyId, cost);
                BillingMetrics.RecordSpendUpdateFailure("direct_db_failed");
            }
            catch (Exception ex)
            {
                databaseFailure = ex;
                logger.LogError(ex,
                    "Direct DB spend update also failed for VirtualKey {VirtualKeyId} ({Cost:C}), queuing to in-memory fallback",
                    virtualKeyId, cost);
                BillingMetrics.RecordSpendUpdateFailure("direct_db_failed");
            }

            // Tier 3: In-memory fallback queue (drained on next successful flush cycle)
            batchSpendService.QueueFallbackUpdate(virtualKeyId, cost, billedAtUtc);
            BillingMetrics.RecordPotentialRevenueLoss(cost, "fallback_queue");

            // The in-memory queue is recoverable during this process lifetime, but it is
            // not durable. Propagate the failure so callers cannot record successful
            // billing until Redis or the database has actually accepted the spend.
            throw new BillingSystemException(
                $"Spend update for Virtual Key {virtualKeyId} was not durably persisted",
                virtualKeyId,
                "database_update_failed",
                databaseFailure);
        }
    }
}
