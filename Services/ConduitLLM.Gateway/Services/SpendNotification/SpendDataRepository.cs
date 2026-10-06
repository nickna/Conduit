using System.Text.Json;
using ConduitLLM.Core.Constants;
using ConduitLLM.Gateway.Serialization;
using StackExchange.Redis;

namespace ConduitLLM.Gateway.Services.SpendNotification
{
    /// <summary>
    /// Repository for managing spend-related data in Redis
    /// </summary>
    public interface ISpendDataRepository
    {
        /// <summary>
        /// Records spending pattern data in Redis
        /// </summary>
        Task RecordSpendingPatternAsync(int virtualKeyId, decimal amount, decimal totalSpend);
        
        /// <summary>
        /// Gets spending pattern data for a virtual key
        /// </summary>
        Task<SpendingPatternData?> GetSpendingPatternAsync(int virtualKeyId);
        
        /// <summary>
        /// Gets all spending pattern keys
        /// </summary>
        Task<List<int>> GetAllPatternKeysAsync();
        
        /// <summary>
        /// Checks if an alert has been sent for a threshold
        /// </summary>
        Task<bool> IsAlertSentAsync(int virtualKeyId, int threshold);
        
        /// <summary>
        /// Marks an alert as sent with TTL
        /// </summary>
        Task<bool> MarkAlertSentAsync(int virtualKeyId, int threshold, TimeSpan ttl);
        
        /// <summary>
        /// Sets a cooldown for an alert type
        /// </summary>
        Task SetAlertCooldownAsync(int virtualKeyId, string alertType, TimeSpan cooldown);
        
        /// <summary>
        /// Checks if an alert is in cooldown
        /// </summary>
        Task<bool> IsAlertInCooldownAsync(int virtualKeyId, string alertType);
        
        /// <summary>
        /// Resets budget alerts for a virtual key
        /// </summary>
        Task ResetBudgetAlertsAsync(int virtualKeyId);
        
        /// <summary>
        /// Registers an instance in Redis
        /// </summary>
        Task RegisterInstanceAsync(string instanceId, SpendNotificationInstanceData instanceData);
        
        /// <summary>
        /// Unregisters an instance from Redis
        /// </summary>
        Task UnregisterInstanceAsync(string instanceId);
    }

    /// <summary>
    /// Represents spending pattern data
    /// </summary>
    public class SpendingPatternData
    {
        public decimal LastAmount { get; set; }
        public decimal TotalSpend { get; set; }
        public long LastUpdate { get; set; }
        public int HourlyCount { get; set; }
        public decimal HourlyTotal { get; set; }
        public int DailyCount { get; set; }
        public decimal DailyTotal { get; set; }
    }

    /// <summary>
    /// Redis implementation of spend data repository
    /// </summary>
    public class SpendDataRepository : ISpendDataRepository
    {
        private readonly IDatabase _database;
        private readonly ILogger<SpendDataRepository> _logger;
        
        
        private readonly TimeSpan _patternRetentionPeriod = TimeSpan.FromHours(24);
        
        public SpendDataRepository(IDatabase database, ILogger<SpendDataRepository> logger)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task RecordSpendingPatternAsync(int virtualKeyId, decimal amount, decimal totalSpend)
        {
            try
            {
                var patternKey = RedisKeys.Spend.Patterns(virtualKeyId.ToString());
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                
                // Get existing pattern data
                var existingData = await _database.HashGetAllAsync(patternKey);
                var patternData = ParseExistingPattern(existingData, amount, now);
                
                // Store updated pattern data
                var hashEntries = new[]
                {
                    new HashEntry("lastAmount", patternData.LastAmount.ToString("F2")),
                    new HashEntry("totalSpend", patternData.TotalSpend.ToString("F2")),
                    new HashEntry("lastUpdate", patternData.LastUpdate.ToString()),
                    new HashEntry("hourlyCount", patternData.HourlyCount.ToString()),
                    new HashEntry("hourlyTotal", patternData.HourlyTotal.ToString("F2")),
                    new HashEntry("dailyCount", patternData.DailyCount.ToString()),
                    new HashEntry("dailyTotal", patternData.DailyTotal.ToString("F2"))
                };
                
                await _database.HashSetAsync(patternKey, hashEntries);
                await _database.KeyExpireAsync(patternKey, _patternRetentionPeriod);
                
                // Add to spend history stream
                var streamEntry = new NameValueEntry[]
                {
                    new("virtualKeyId", virtualKeyId.ToString()),
                    new("amount", amount.ToString("F2")),
                    new("totalSpend", totalSpend.ToString("F2")),
                    new("timestamp", now.ToString())
                };
                
                await _database.StreamAddAsync(RedisKeys.Spend.HistoryStream, streamEntry, maxLength: 10000);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording spending pattern for VirtualKey {VirtualKeyId}", virtualKeyId);
            }
        }

        public async Task<SpendingPatternData?> GetSpendingPatternAsync(int virtualKeyId)
        {
            try
            {
                var patternKey = RedisKeys.Spend.Patterns(virtualKeyId.ToString());
                var patternData = await _database.HashGetAllAsync(patternKey);
                
                if (patternData.Length == 0) return null;
                
                return new SpendingPatternData
                {
                    LastAmount = ParseDecimalValue(patternData, "lastAmount"),
                    TotalSpend = ParseDecimalValue(patternData, "totalSpend"),
                    LastUpdate = ParseLongValue(patternData, "lastUpdate"),
                    HourlyCount = ParseIntValue(patternData, "hourlyCount"),
                    HourlyTotal = ParseDecimalValue(patternData, "hourlyTotal"),
                    DailyCount = ParseIntValue(patternData, "dailyCount"),
                    DailyTotal = ParseDecimalValue(patternData, "dailyTotal")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting spending pattern for VirtualKey {VirtualKeyId}", virtualKeyId);
                return null;
            }
        }

        public Task<List<int>> GetAllPatternKeysAsync()
        {
            var keys = new List<int>();
            try
            {
                var server = _database.Multiplexer.GetServer(_database.Multiplexer.GetEndPoints()[0]);
                var patternKeys = server.Keys(pattern: RedisKeys.Spend.PatternsScanPattern()).ToArray();
                
                foreach (var key in patternKeys)
                {
                    var keyString = key.ToString();
                    if (int.TryParse(keyString.Split(':').Last(), out var virtualKeyId))
                    {
                        keys.Add(virtualKeyId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all pattern keys");
            }
            return Task.FromResult(keys);
        }

        public async Task<bool> IsAlertSentAsync(int virtualKeyId, int threshold)
        {
            var alertKey = RedisKeys.Spend.SentAlert(virtualKeyId.ToString(), threshold.ToString());
            return await _database.KeyExistsAsync(alertKey);
        }

        public async Task<bool> MarkAlertSentAsync(int virtualKeyId, int threshold, TimeSpan ttl)
        {
            var alertKey = RedisKeys.Spend.SentAlert(virtualKeyId.ToString(), threshold.ToString());
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var result = await _database.StringSetAsync(alertKey, timestamp, when: When.NotExists);
            
            if (result)
            {
                await _database.KeyExpireAsync(alertKey, ttl);
            }
            
            return result;
        }

        public async Task SetAlertCooldownAsync(int virtualKeyId, string alertType, TimeSpan cooldown)
        {
            var cooldownKey = RedisKeys.Spend.Cooldown(virtualKeyId.ToString(), alertType);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            await _database.StringSetAsync(cooldownKey, timestamp, cooldown);
        }

        public async Task<bool> IsAlertInCooldownAsync(int virtualKeyId, string alertType)
        {
            var cooldownKey = RedisKeys.Spend.Cooldown(virtualKeyId.ToString(), alertType);
            return await _database.KeyExistsAsync(cooldownKey);
        }

        public async Task ResetBudgetAlertsAsync(int virtualKeyId)
        {
            try
            {
                var pattern = RedisKeys.Spend.SentAlertScanPattern(virtualKeyId.ToString());
                var server = _database.Multiplexer.GetServer(_database.Multiplexer.GetEndPoints()[0]);
                var keys = server.Keys(pattern: pattern).ToArray();
                
                if (keys.Any())
                {
                    await _database.KeyDeleteAsync(keys);
                    _logger.LogInformation("Budget alerts reset for VirtualKey {VirtualKeyId}", virtualKeyId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting budget alerts for VirtualKey {VirtualKeyId}", virtualKeyId);
            }
        }

        public async Task RegisterInstanceAsync(string instanceId, SpendNotificationInstanceData instanceData)
        {
            var key = RedisKeys.Spend.Instance(instanceId);
            await _database.HashSetAsync(
                key,
                "data",
                JsonSerializer.Serialize(
                    instanceData,
                    GatewayInternalJsonContext.Default.SpendNotificationInstanceData));
            await _database.KeyExpireAsync(key, TimeSpan.FromMinutes(2));
        }

        public async Task UnregisterInstanceAsync(string instanceId)
        {
            var key = RedisKeys.Spend.Instance(instanceId);
            await _database.KeyDeleteAsync(key);
        }

        private SpendingPatternData ParseExistingPattern(HashEntry[] existingData, decimal amount, long now)
        {
            var pattern = new SpendingPatternData
            {
                LastAmount = amount,
                TotalSpend = amount,
                LastUpdate = now,
                HourlyCount = 1,
                HourlyTotal = amount,
                DailyCount = 1,
                DailyTotal = amount
            };

            if (existingData.Length > 0)
            {
                var lastUpdate = ParseLongValue(existingData, "lastUpdate");
                var hourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
                var dayAgo = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
                
                pattern.TotalSpend = ParseDecimalValue(existingData, "totalSpend") + amount;
                
                if (lastUpdate > hourAgo)
                {
                    pattern.HourlyCount = ParseIntValue(existingData, "hourlyCount") + 1;
                    pattern.HourlyTotal = ParseDecimalValue(existingData, "hourlyTotal") + amount;
                }
                
                if (lastUpdate > dayAgo)
                {
                    pattern.DailyCount = ParseIntValue(existingData, "dailyCount") + 1;
                    pattern.DailyTotal = ParseDecimalValue(existingData, "dailyTotal") + amount;
                }
            }
            
            return pattern;
        }

        private static int ParseIntValue(HashEntry[] data, string name)
        {
            var value = data.FirstOrDefault(x => x.Name == name).Value;
            return value.HasValue && !value.IsNullOrEmpty ? int.Parse(value.ToString()) : 0;
        }

        private static long ParseLongValue(HashEntry[] data, string name)
        {
            var value = data.FirstOrDefault(x => x.Name == name).Value;
            return value.HasValue && !value.IsNullOrEmpty ? long.Parse(value.ToString()) : 0;
        }

        private static decimal ParseDecimalValue(HashEntry[] data, string name)
        {
            var value = data.FirstOrDefault(x => x.Name == name).Value;
            return value.HasValue && !value.IsNullOrEmpty ? decimal.Parse(value.ToString()) : 0;
        }
    }
}
