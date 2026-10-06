using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Interface for distributed webhook metrics tracking
    /// </summary>
    public interface IWebhookMetricsService
    {
        /// <summary>
        /// Records a webhook delivery attempt
        /// </summary>
        Task RecordAttemptAsync(string webhookUrl, string taskId, string taskType, string eventType);
        
        /// <summary>
        /// Records a successful webhook delivery
        /// </summary>
        Task RecordSuccessAsync(string webhookUrl, string taskId, long responseTimeMs);
        
        /// <summary>
        /// Records a failed webhook delivery
        /// </summary>
        Task RecordFailureAsync(string webhookUrl, string taskId, bool isPermanent);
        
        /// <summary>
        /// Gets aggregated statistics across all instances
        /// </summary>
        Task<WebhookStatistics> GetStatisticsAsync(string period = "last_hour");
        
        /// <summary>
        /// Gets statistics for a specific webhook URL
        /// </summary>
        Task<WebhookUrlStatistics> GetUrlStatisticsAsync(string webhookUrl);
        
        /// <summary>
        /// Adds a recent delivery event for tracking
        /// </summary>
        Task AddRecentEventAsync(string webhookUrl, string eventType, long? responseTimeMs = null, bool? isPermanent = null);
    }
    
    /// <summary>
    /// Redis-based implementation of webhook metrics tracking
    /// Provides distributed metrics aggregation across all instances
    /// 
    /// For detailed architecture and monitoring information, see:
    /// - Architecture: docs/architecture/webhook-delivery-system.md
    /// - Operations: docs/operations/webhook-monitoring.md
    /// </summary>
    public class RedisWebhookMetricsService : RedisWebhookServiceBase, IWebhookMetricsService
    {
        private const int MAX_RECENT_EVENTS = 1000;
        private const int MAX_RESPONSE_TIMES = 100;

        public RedisWebhookMetricsService(
            IConnectionMultiplexer redis,
            ILogger<RedisWebhookMetricsService> logger)
            : base(redis, logger)
        {
        }
        
        public async Task RecordAttemptAsync(string webhookUrl, string taskId, string taskType, string eventType)
        {
            try
            {
                var db = Redis.GetDatabase();
                var urlHash = GetUrlHash(webhookUrl);
                var metricsKey = RedisKeys.WebhookMetrics.UrlMetrics(urlHash);
                
                var transaction = db.CreateTransaction();
                
                // Increment total attempts
                _ = transaction.HashIncrementAsync(metricsKey, "total_attempts");
                _ = transaction.HashSetAsync(metricsKey, "last_attempt", DateTime.UtcNow.ToString("O"));
                _ = transaction.HashSetAsync(metricsKey, "url", webhookUrl);
                
                // Set expiry to keep data for 7 days
                _ = transaction.KeyExpireAsync(metricsKey, TimeSpan.FromDays(7));
                
                // Add to recent events
                await AddRecentEventInternalAsync(transaction, webhookUrl, "attempt", null, null);
                
                await transaction.ExecuteAsync();
                
                Logger.LogDebug("Recorded delivery attempt for {WebhookUrl}", webhookUrl);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error recording delivery attempt for {WebhookUrl}", webhookUrl);
            }
        }
        
        public async Task RecordSuccessAsync(string webhookUrl, string taskId, long responseTimeMs)
        {
            try
            {
                var db = Redis.GetDatabase();
                var urlHash = GetUrlHash(webhookUrl);
                var metricsKey = RedisKeys.WebhookMetrics.UrlMetrics(urlHash);
                
                var transaction = db.CreateTransaction();
                
                // Update success metrics
                _ = transaction.HashIncrementAsync(metricsKey, "successes");
                _ = transaction.HashSetAsync(metricsKey, "last_success", DateTime.UtcNow.ToString("O"));
                
                // Store response time in sorted set for percentile calculations
                var responseTimesKey = RedisKeys.WebhookMetrics.ResponseTimes(urlHash);
                _ = transaction.SortedSetAddAsync(responseTimesKey, 
                    $"{Guid.NewGuid()}", responseTimeMs);
                
                // Keep only recent response times
                _ = transaction.SortedSetRemoveRangeByRankAsync(responseTimesKey, 0, -MAX_RESPONSE_TIMES - 1);
                _ = transaction.KeyExpireAsync(responseTimesKey, TimeSpan.FromDays(1));
                
                // Update average response time
                _ = transaction.HashIncrementAsync(metricsKey, "total_response_time", responseTimeMs);
                _ = transaction.HashIncrementAsync(metricsKey, "response_count");
                
                // Set expiry
                _ = transaction.KeyExpireAsync(metricsKey, TimeSpan.FromDays(7));
                
                // Add to recent events
                await AddRecentEventInternalAsync(transaction, webhookUrl, "success", responseTimeMs, null);
                
                await transaction.ExecuteAsync();
                
                Logger.LogDebug("Recorded successful delivery for {WebhookUrl}, response time: {ResponseTime}ms", 
                    webhookUrl, responseTimeMs);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error recording success for {WebhookUrl}", webhookUrl);
            }
        }
        
        public async Task RecordFailureAsync(string webhookUrl, string taskId, bool isPermanent)
        {
            try
            {
                var db = Redis.GetDatabase();
                var urlHash = GetUrlHash(webhookUrl);
                var metricsKey = RedisKeys.WebhookMetrics.UrlMetrics(urlHash);
                
                var transaction = db.CreateTransaction();
                
                // Update failure metrics
                _ = transaction.HashIncrementAsync(metricsKey, "failures");
                _ = transaction.HashSetAsync(metricsKey, "last_failure", DateTime.UtcNow.ToString("O"));
                
                if (!isPermanent)
                {
                    _ = transaction.HashIncrementAsync(metricsKey, "pending_retries");
                }
                else
                {
                    _ = transaction.HashIncrementAsync(metricsKey, "permanent_failures");
                }
                
                // Set expiry
                _ = transaction.KeyExpireAsync(metricsKey, TimeSpan.FromDays(7));
                
                // Add to recent events
                await AddRecentEventInternalAsync(transaction, webhookUrl, "failure", null, isPermanent);
                
                await transaction.ExecuteAsync();
                
                Logger.LogDebug("Recorded failed delivery for {WebhookUrl}, permanent: {IsPermanent}", 
                    webhookUrl, isPermanent);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error recording failure for {WebhookUrl}", webhookUrl);
            }
        }
        
        public async Task<WebhookStatistics> GetStatisticsAsync(string period = "last_hour")
        {
            try
            {
                var db = Redis.GetDatabase();
                var stats = new WebhookStatistics
                {
                    Period = period,
                    UrlStatistics = new List<WebhookUrlStatistics>()
                };
                
                // Calculate cutoff time based on period
                var cutoffTime = GetCutoffTime(period);
                
                // Get all webhook URL metrics keys
                var server = Redis.GetPrimaryServer();
                var keys = server.Keys(pattern: RedisKeys.WebhookMetrics.UrlMetricsScanPattern).ToList();
                
                var tasks = new List<Task<WebhookUrlStatistics?>>();
                
                foreach (var key in keys)
                {
                    tasks.Add(GetUrlStatisticsFromKeyAsync(db, key, cutoffTime));
                }
                
                var urlStatsList = await Task.WhenAll(tasks);
                
                // Aggregate statistics
                foreach (var urlStats in urlStatsList.Where(s => s != null))
                {
                    stats.UrlStatistics.Add(urlStats!);
                    stats.TotalDeliveries += urlStats!.TotalDeliveries;
                    stats.SuccessfulDeliveries += urlStats.SuccessfulDeliveries;
                    stats.FailedDeliveries += urlStats.FailedDeliveries;
                    stats.PendingDeliveries += (int)(urlStats.PendingRetries ?? 0);
                }
                
                // Calculate overall success rate
                stats.SuccessRate = stats.TotalDeliveries > 0
                    ? (double)stats.SuccessfulDeliveries / stats.TotalDeliveries * 100
                    : 0;
                
                // Calculate average response time from all URLs
                if (stats.UrlStatistics.Any())
                {
                    var avgResponseTimes = stats.UrlStatistics
                        .Where(u => u.AverageResponseTimeMs > 0)
                        .Select(u => u.AverageResponseTimeMs)
                        .ToList();
                    
                    stats.AverageResponseTimeMs = avgResponseTimes.Any() 
                        ? avgResponseTimes.Average() 
                        : 0;
                }
                
                return stats;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting webhook statistics for period {Period}", period);
                return new WebhookStatistics { Period = period, UrlStatistics = new List<WebhookUrlStatistics>() };
            }
        }
        
        public async Task<WebhookUrlStatistics> GetUrlStatisticsAsync(string webhookUrl)
        {
            try
            {
                var db = Redis.GetDatabase();
                var urlHash = GetUrlHash(webhookUrl);
                var metricsKey = RedisKeys.WebhookMetrics.UrlMetrics(urlHash);
                
                var hashEntries = await db.HashGetAllAsync(metricsKey);
                
                if (!hashEntries.Any())
                {
                    return new WebhookUrlStatistics { Url = webhookUrl, IsHealthy = true };
                }
                
                var metrics = hashEntries.ToDictionary(
                    e => e.Name.ToString(),
                    e => e.Value.ToString());
                
                var totalAttempts = GetLongValue(metrics, "total_attempts");
                var successes = GetLongValue(metrics, "successes");
                var failures = GetLongValue(metrics, "failures");
                var pendingRetries = GetLongValue(metrics, "pending_retries");
                
                // Calculate average response time
                double avgResponseTime = 0;
                var responseCount = GetLongValue(metrics, "response_count");
                if (responseCount > 0)
                {
                    var totalResponseTime = GetLongValue(metrics, "total_response_time");
                    avgResponseTime = (double)totalResponseTime / responseCount;
                }
                
                // Get percentile response times
                var responseTimesKey = RedisKeys.WebhookMetrics.ResponseTimes(urlHash);
                var p95ResponseTime = await GetPercentileResponseTimeAsync(db, responseTimesKey, 0.95);
                var p99ResponseTime = await GetPercentileResponseTimeAsync(db, responseTimesKey, 0.99);
                
                return new WebhookUrlStatistics
                {
                    Url = webhookUrl,
                    TotalDeliveries = (int)totalAttempts,
                    SuccessfulDeliveries = (int)successes,
                    FailedDeliveries = (int)failures,
                    PendingRetries = pendingRetries,
                    AverageResponseTimeMs = avgResponseTime,
                    P95ResponseTimeMs = p95ResponseTime,
                    P99ResponseTimeMs = p99ResponseTime,
                    SuccessRate = totalAttempts > 0 ? (double)successes / totalAttempts * 100 : 0,
                    IsHealthy = failures < 5 || (totalAttempts > 0 && (double)successes / totalAttempts > 0.95)
                };
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting statistics for webhook URL {WebhookUrl}", webhookUrl);
                // Metrics store unavailable — report unhealthy rather than fabricating a healthy reading
                return new WebhookUrlStatistics { Url = webhookUrl, IsHealthy = false };
            }
        }
        
        public async Task AddRecentEventAsync(string webhookUrl, string eventType, long? responseTimeMs = null, bool? isPermanent = null)
        {
            try
            {
                var db = Redis.GetDatabase();
                var transaction = db.CreateTransaction();
                await AddRecentEventInternalAsync(transaction, webhookUrl, eventType, responseTimeMs, isPermanent);
                await transaction.ExecuteAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error adding recent event for {WebhookUrl}", webhookUrl);
            }
        }
        
        private async Task AddRecentEventInternalAsync(
            ITransaction transaction, 
            string webhookUrl, 
            string eventType, 
            long? responseTimeMs, 
            bool? isPermanent)
        {
            var eventData = new Dictionary<string, object>
            {
                ["url"] = webhookUrl,
                ["type"] = eventType,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };
            
            if (responseTimeMs.HasValue)
                eventData["response_time_ms"] = responseTimeMs.Value;
            
            if (isPermanent.HasValue)
                eventData["is_permanent"] = isPermanent.Value;
            
            var eventJson = System.Text.Json.JsonSerializer.Serialize(
                eventData,
                Serialization.AsyncTaskJsonContext.Default.DictionaryStringObject);
            
            // Add to sorted set with timestamp as score
            _ = transaction.SortedSetAddAsync(RedisKeys.WebhookMetrics.RecentEvents, eventJson, 
                new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds());
            
            // Keep only recent events
            _ = transaction.SortedSetRemoveRangeByRankAsync(RedisKeys.WebhookMetrics.RecentEvents, 0, -MAX_RECENT_EVENTS - 1);
            
            // Set expiry
            _ = transaction.KeyExpireAsync(RedisKeys.WebhookMetrics.RecentEvents, TimeSpan.FromDays(1));
            
            await Task.CompletedTask;
        }
        
        private async Task<WebhookUrlStatistics?> GetUrlStatisticsFromKeyAsync(
            IDatabase db, 
            RedisKey key, 
            DateTime cutoffTime)
        {
            var hashEntries = await db.HashGetAllAsync(key);
            
            if (!hashEntries.Any())
                return null;
            
            var metrics = hashEntries.ToDictionary(
                e => e.Name.ToString(),
                e => e.Value.ToString());
            
            // Check if metrics are within the time period
            if (metrics.TryGetValue("last_attempt", out var lastAttemptStr) &&
                DateTime.TryParse(lastAttemptStr, out var lastAttempt))
            {
                if (lastAttempt < cutoffTime)
                    return null; // Skip metrics outside the period
            }
            
            var url = metrics.GetValueOrDefault("url", "unknown");
            var totalAttempts = GetLongValue(metrics, "total_attempts");
            var successes = GetLongValue(metrics, "successes");
            var failures = GetLongValue(metrics, "failures");
            var pendingRetries = GetLongValue(metrics, "pending_retries");
            
            // Calculate average response time
            double avgResponseTime = 0;
            var responseCount = GetLongValue(metrics, "response_count");
            if (responseCount > 0)
            {
                var totalResponseTime = GetLongValue(metrics, "total_response_time");
                avgResponseTime = (double)totalResponseTime / responseCount;
            }
            
            return new WebhookUrlStatistics
            {
                Url = url,
                TotalDeliveries = (int)totalAttempts,
                SuccessfulDeliveries = (int)successes,
                FailedDeliveries = (int)failures,
                PendingRetries = pendingRetries,
                AverageResponseTimeMs = avgResponseTime,
                SuccessRate = totalAttempts > 0 ? (double)successes / totalAttempts * 100 : 0,
                IsHealthy = failures < 5 || (totalAttempts > 0 && (double)successes / totalAttempts > 0.95)
            };
        }
        
        private async Task<double> GetPercentileResponseTimeAsync(IDatabase db, string key, double percentile)
        {
            try
            {
                var count = await db.SortedSetLengthAsync(key);
                if (count == 0)
                    return 0;
                
                var index = (long)Math.Ceiling(count * percentile) - 1;
                var values = await db.SortedSetRangeByRankAsync(key, index, index);
                
                if (values.Length > 0)
                {
                    // The score is the response time
                    var score = await db.SortedSetScoreAsync(key, values[0]);
                    return score ?? 0;
                }
                
                return 0;
            }
            catch
            {
                return 0;
            }
        }
        
        private DateTime GetCutoffTime(string period)
        {
            return period switch
            {
                "last_hour" => DateTime.UtcNow.AddHours(-1),
                "last_day" => DateTime.UtcNow.AddDays(-1),
                "last_week" => DateTime.UtcNow.AddDays(-7),
                "last_month" => DateTime.UtcNow.AddDays(-30),
                _ => DateTime.UtcNow.AddHours(-1)
            };
        }
        
        private long GetLongValue(Dictionary<string, string> metrics, string key)
        {
            if (metrics.TryGetValue(key, out var value) && long.TryParse(value, out var result))
                return result;
            return 0;
        }
        
    }
}
