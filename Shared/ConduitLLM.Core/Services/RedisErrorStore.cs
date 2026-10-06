using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Redis implementation of error store
    /// </summary>
    public class RedisErrorStore : IRedisErrorStore
    {
        private readonly IDatabase _db;
        private readonly ILogger<RedisErrorStore> _logger;

        public RedisErrorStore(
            IConnectionMultiplexer redis,
            ILogger<RedisErrorStore> logger)
        {
            _db = redis?.GetDatabase() ?? throw new ArgumentNullException(nameof(redis));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task TrackFatalErrorAsync(int keyId, ProviderErrorInfo error)
        {
            var fatalKey = CacheKeys.ProviderError.FatalByKey(keyId);
            var requestKey = CacheKeys.ProviderError.FatalRequestsByType(
                keyId, error.ErrorType.ToString());
            var requestId = string.IsNullOrWhiteSpace(error.RequestId)
                ? $"{error.OccurredAt.Ticks}:{Guid.NewGuid():N}"
                : error.RequestId;
            var requestScore = new DateTimeOffset(error.OccurredAt).ToUnixTimeMilliseconds();
            
            var tasks = new List<Task>
            {
                _db.HashIncrementAsync(fatalKey, "count"),
                _db.HashSetAsync(fatalKey, new[]
                {
                    new HashEntry("error_type", error.ErrorType.ToString()),
                    new HashEntry("last_seen", error.OccurredAt.ToString("O")),
                    new HashEntry("last_error_message", error.ErrorMessage),
                    new HashEntry("last_status_code", error.HttpStatusCode ?? 0)
                }),
                _db.SortedSetAddAsync(requestKey, requestId, requestScore)
            };
            
            // Set first_seen only if it doesn't exist
            tasks.Add(_db.HashSetAsync(fatalKey, "first_seen",
                error.OccurredAt.ToString("O"), When.NotExists));

            await Task.WhenAll(tasks);

            // Set TTL of 30 days (same as warnings) to prevent unbounded growth
            await Task.WhenAll(
                _db.KeyExpireAsync(fatalKey, TimeSpan.FromDays(30)),
                _db.KeyExpireAsync(requestKey, TimeSpan.FromDays(30)));
        }

        public async Task TrackWarningAsync(int keyId, ProviderErrorInfo error)
        {
            var warningKey = CacheKeys.ProviderError.WarningsByKey(keyId);
            var warningData = JsonSerializer.Serialize(
                new ProviderWarningEntry(
                    error.ErrorType.ToString(),
                    error.ErrorMessage,
                    error.OccurredAt),
                CoreInternalJsonContext.Default.ProviderWarningEntry);
            
            await _db.SortedSetAddAsync(warningKey, 
                warningData, 
                new DateTimeOffset(error.OccurredAt).ToUnixTimeSeconds());
            
            // Trim old warnings (keep last 100)
            await _db.SortedSetRemoveRangeByRankAsync(warningKey, 0, -101);
            
            // Set TTL of 30 days
            await _db.KeyExpireAsync(warningKey, TimeSpan.FromDays(30));
        }

        public async Task UpdateProviderSummaryAsync(int providerId, bool isFatal)
        {
            var summaryKey = CacheKeys.ProviderError.ProviderSummary(providerId);
            
            var tasks = new List<Task>
            {
                _db.HashIncrementAsync(summaryKey, "total_errors"),
                _db.HashSetAsync(summaryKey, "last_error", DateTime.UtcNow.ToString("O"))
            };
            
            if (isFatal)
            {
                tasks.Add(_db.HashIncrementAsync(summaryKey, "fatal_errors"));
            }
            else
            {
                tasks.Add(_db.HashIncrementAsync(summaryKey, "warnings"));
            }
            
            await Task.WhenAll(tasks);
        }

        public async Task AddToGlobalFeedAsync(ProviderErrorInfo error)
        {
            var feedKey = CacheKeys.ProviderError.RecentFeed;
            var feedEntry = JsonSerializer.Serialize(
                new ProviderFeedEntry(
                    error.KeyCredentialId,
                    error.ProviderId,
                    error.ErrorType.ToString(),
                    error.ErrorMessage,
                    error.OccurredAt),
                CoreInternalJsonContext.Default.ProviderFeedEntry);
            
            await _db.SortedSetAddAsync(feedKey, 
                feedEntry, 
                new DateTimeOffset(error.OccurredAt).ToUnixTimeSeconds());
            
            // Keep only last 1000 entries
            await _db.SortedSetRemoveRangeByRankAsync(feedKey, 0, -1001);
        }

        public async Task<FatalErrorInfo?> GetFatalErrorAsync(int keyId)
        {
            var fatalKey = CacheKeys.ProviderError.FatalByKey(keyId);
            var data = await _db.HashGetAllAsync(fatalKey);
            
            if (data.Length == 0)
                return null;
            
            var dict = data.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());
            
            if (!Enum.TryParse<ProviderErrorType>(
                    dict.GetValueOrDefault("error_type"),
                    ignoreCase: true,
                    out var errorType) ||
                !int.TryParse(
                    dict.GetValueOrDefault("count"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var count) ||
                !DateTime.TryParse(
                    dict.GetValueOrDefault("first_seen"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var firstSeen) ||
                !DateTime.TryParse(
                    dict.GetValueOrDefault("last_seen"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var lastSeen))
            {
                _logger.LogWarning(
                    "Ignoring incomplete fatal-error hash for key {KeyId}",
                    keyId);
                return null;
            }

            return new FatalErrorInfo
            {
                ErrorType = errorType,
                Count = count,
                FirstSeen = firstSeen,
                LastSeen = lastSeen,
                LastErrorMessage = dict.GetValueOrDefault("last_error_message") ?? string.Empty,
                LastStatusCode = int.TryParse(dict.GetValueOrDefault("last_status_code"), out var code)
                    ? code : null,
                DisabledAt = ParseOptionalDateTime(dict.GetValueOrDefault("disabled_at"))
            };
        }

        public async Task<long> GetDistinctFatalRequestCountAsync(
            int keyId,
            ProviderErrorType errorType,
            TimeSpan window)
        {
            var requestKey = CacheKeys.ProviderError.FatalRequestsByType(
                keyId, errorType.ToString());
            var cutoff = new DateTimeOffset(DateTime.UtcNow - window).ToUnixTimeMilliseconds();

            await _db.SortedSetRemoveRangeByScoreAsync(
                requestKey,
                double.NegativeInfinity,
                cutoff - 1);

            return await _db.SortedSetLengthAsync(
                requestKey,
                cutoff,
                double.PositiveInfinity);
        }

        public Task<bool> TryAcquireKeyDisableAsync(int keyId, TimeSpan ttl)
        {
            return _db.StringSetAsync(
                CacheKeys.ProviderError.DisableGuard(keyId),
                "1",
                ttl,
                When.NotExists);
        }

        public async Task MarkKeyDisabledAsync(
            int keyId,
            DateTime disabledAt,
            ProviderErrorType errorType)
        {
            var fatalKey = CacheKeys.ProviderError.FatalByKey(keyId);
            await Task.WhenAll(
                _db.HashSetAsync(fatalKey, new HashEntry[]
                {
                    new("disabled_at", disabledAt.ToString("O")),
                    new("error_type", errorType.ToString()),
                    new("reprobe_attempt_count", 0)
                }),
                _db.HashDeleteAsync(fatalKey, new RedisValue[]
                {
                    "last_reprobe_at",
                    "next_reprobe_at"
                }),
                _db.SetAddAsync(CacheKeys.ProviderError.DisabledKeys, keyId));
        }

        public async Task<IReadOnlyList<DisabledKeyReprobeState>> GetDisabledKeyReprobeStatesAsync()
        {
            var members = await _db.SetMembersAsync(CacheKeys.ProviderError.DisabledKeys);
            if (members.Length == 0)
            {
                return Array.Empty<DisabledKeyReprobeState>();
            }

            var stateTasks = members
                .Where(member => member.HasValue)
                .Select(async member =>
                {
                    var keyId = (int)member;
                    var entries = await _db.HashGetAllAsync(
                        CacheKeys.ProviderError.FatalByKey(keyId));
                    if (entries.Length == 0)
                    {
                        await _db.SetRemoveAsync(CacheKeys.ProviderError.DisabledKeys, keyId);
                        return null;
                    }

                    var values = entries.ToDictionary(
                        entry => entry.Name.ToString(),
                        entry => entry.Value.ToString());
                    if (!Enum.TryParse<ProviderErrorType>(
                            values.GetValueOrDefault("error_type"),
                            out var errorType) ||
                        !values.TryGetValue("disabled_at", out var disabledAtValue) ||
                        !DateTime.TryParse(
                            disabledAtValue,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var disabledAt))
                    {
                        return null;
                    }

                    return new DisabledKeyReprobeState
                    {
                        KeyId = keyId,
                        ErrorType = errorType,
                        DisabledAt = disabledAt,
                        AttemptCount = int.TryParse(
                            values.GetValueOrDefault("reprobe_attempt_count"),
                            out var attempts) ? attempts : 0,
                        LastAttemptAt = ParseOptionalDateTime(
                            values.GetValueOrDefault("last_reprobe_at")),
                        NextAttemptAt = ParseOptionalDateTime(
                            values.GetValueOrDefault("next_reprobe_at"))
                    };
                })
                .ToList();

            var states = await Task.WhenAll(stateTasks);
            return states.Where(state => state != null).Select(state => state!).ToList();
        }

        public Task<bool> TryAcquireKeyReprobeAsync(int keyId, TimeSpan ttl)
        {
            return _db.StringSetAsync(
                CacheKeys.ProviderError.ReprobeGuard(keyId),
                "1",
                ttl,
                When.NotExists);
        }

        public async Task RecordKeyReprobeAttemptAsync(
            int keyId,
            int attemptCount,
            DateTime attemptedAt,
            DateTime nextAttemptAt)
        {
            await _db.HashSetAsync(
                CacheKeys.ProviderError.FatalByKey(keyId),
                new HashEntry[]
                {
                    new("reprobe_attempt_count", attemptCount),
                    new("last_reprobe_at", attemptedAt.ToString("O")),
                    new("next_reprobe_at", nextAttemptAt.ToString("O"))
                });
        }

        public async Task MarkKeyReprobeRequiresManualAsync(
            int keyId,
            ProviderErrorType errorType)
        {
            await Task.WhenAll(
                _db.HashSetAsync(
                    CacheKeys.ProviderError.FatalByKey(keyId),
                    "error_type",
                    errorType.ToString()),
                _db.SetRemoveAsync(CacheKeys.ProviderError.DisabledKeys, keyId));
        }

        public async Task MarkProviderDisabledAsync(int providerId, DateTime disabledAt, string reason)
        {
            var summaryKey = CacheKeys.ProviderError.ProviderSummary(providerId);
            await Task.WhenAll(
                _db.HashSetAsync(summaryKey, "provider_disabled_at", disabledAt.ToString("O")),
                _db.HashSetAsync(summaryKey, "provider_disable_reason", reason)
            );
        }

        public async Task ClearProviderDisabledAsync(int providerId)
        {
            var summaryKey = CacheKeys.ProviderError.ProviderSummary(providerId);
            await _db.HashDeleteAsync(summaryKey, new RedisValue[]
            {
                "provider_disabled_at",
                "provider_disable_reason"
            });
        }

        public async Task AddDisabledKeyToProviderAsync(int providerId, int keyId)
        {
            var setKey = CacheKeys.ProviderError.DisabledKeysByProvider(providerId);
            await _db.SetAddAsync(setKey, keyId);
        }

        public async Task RemoveDisabledKeyFromProviderAsync(int providerId, int keyId)
        {
            var setKey = CacheKeys.ProviderError.DisabledKeysByProvider(providerId);
            await _db.SetRemoveAsync(setKey, keyId);
        }

        public async Task<IReadOnlyList<ErrorFeedEntry>> GetRecentErrorsAsync(int limit = 100)
        {
            var feedKey = CacheKeys.ProviderError.RecentFeed;
            var entries = await _db.SortedSetRangeByScoreAsync(
                feedKey, 
                order: Order.Descending, 
                take: limit);
            
            var errors = new List<ErrorFeedEntry>();
            
            foreach (var entry in entries)
            {
                try
                {
                    var data = JsonDocument.Parse(entry.ToString());
                    errors.Add(new ErrorFeedEntry
                    {
                        KeyId = data.RootElement.GetProperty("keyId").GetInt32(),
                        ProviderId = data.RootElement.GetProperty("providerId").GetInt32(),
                        ErrorType = data.RootElement.GetProperty("type").GetString() ?? "",
                        Message = data.RootElement.GetProperty("message").GetString() ?? "",
                        Timestamp = data.RootElement.GetProperty("timestamp").GetDateTime()
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse error feed entry");
                }
            }
            
            return errors;
        }

        public async Task<Dictionary<int, ErrorCountInfo>> GetErrorCountsByKeysAsync(
            int providerId, 
            IEnumerable<int> keyIds, 
            TimeSpan window)
        {
            var counts = new Dictionary<int, ErrorCountInfo>();
            var cutoff = DateTime.UtcNow - window;
            
            foreach (var keyId in keyIds)
            {
                var fatalKey = CacheKeys.ProviderError.FatalByKey(keyId);
                var lastSeenValue = await _db.HashGetAsync(fatalKey, "last_seen");
                
                if (lastSeenValue.HasValue)
                {
                    var lastSeenTime = DateTime.Parse(lastSeenValue.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    if (lastSeenTime >= cutoff)
                    {
                        var countValue = await _db.HashGetAsync(fatalKey, "count");
                        if (countValue.HasValue)
                        {
                            counts[keyId] = new ErrorCountInfo
                            {
                                Count = (int)countValue,
                                LastSeen = lastSeenTime
                            };
                        }
                    }
                }
            }
            
            return counts;
        }

        public async Task ClearErrorsForKeyAsync(int keyId, int? providerId = null)
        {
            var keysToDelete = new List<RedisKey>
            {
                CacheKeys.ProviderError.FatalByKey(keyId),
                CacheKeys.ProviderError.WarningsByKey(keyId),
                CacheKeys.ProviderError.DisableGuard(keyId),
                CacheKeys.ProviderError.ReprobeGuard(keyId)
            };
            keysToDelete.AddRange(ErrorThresholdConfiguration.FatalErrorPolicies.Keys.Select(
                errorType => (RedisKey)CacheKeys.ProviderError.FatalRequestsByType(
                    keyId, errorType.ToString())));

            // Delete error keys
            await _db.KeyDeleteAsync(keysToDelete.ToArray());
            await _db.SetRemoveAsync(CacheKeys.ProviderError.DisabledKeys, keyId);

            // Remove from provider's disabled keys set if providerId is known
            if (providerId.HasValue)
            {
                await RemoveDisabledKeyFromProviderAsync(providerId.Value, keyId);
            }

            _logger.LogInformation("Cleared errors for key {KeyId}", keyId);
        }

        private static DateTime? ParseOptionalDateTime(string? value) =>
            DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) ? parsed : null;

        public async Task<KeyErrorDetails?> GetKeyErrorDetailsAsync(int keyId)
        {
            var result = new KeyErrorDetails { KeyId = keyId };
            
            // Get fatal error information
            result.FatalError = await GetFatalErrorAsync(keyId);
            result.DisabledAt = result.FatalError?.DisabledAt;
            
            // Get recent warnings
            var warningKey = CacheKeys.ProviderError.WarningsByKey(keyId);
            var warnings = await _db.SortedSetRangeByScoreAsync(
                warningKey, 
                order: Order.Descending, 
                take: 10);
            
            foreach (var warning in warnings)
            {
                try
                {
                    var data = JsonDocument.Parse(warning.ToString());
                    var typeName = data.RootElement.GetProperty("type").GetString();
                    if (!Enum.TryParse<ProviderErrorType>(typeName, ignoreCase: true, out var warningType))
                    {
                        _logger.LogWarning(
                            "Ignoring warning with unknown provider error type {ErrorType}",
                            typeName);
                        continue;
                    }

                    result.RecentWarnings.Add(new WarningInfo
                    {
                        Type = warningType,
                        Message = data.RootElement.GetProperty("message").GetString() ?? "",
                        Timestamp = data.RootElement.GetProperty("timestamp").GetDateTime()
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse warning data");
                }
            }
            
            return result;
        }

        public async Task<ProviderErrorSummary?> GetProviderSummaryAsync(int providerId)
        {
            var summaryKey = CacheKeys.ProviderError.ProviderSummary(providerId);
            var disabledSetKey = CacheKeys.ProviderError.DisabledKeysByProvider(providerId);

            var summaryTask = _db.HashGetAllAsync(summaryKey);
            var disabledKeysTask = _db.SetMembersAsync(disabledSetKey);

            await Task.WhenAll(summaryTask, disabledKeysTask);

            var summaryData = await summaryTask;
            var disabledMembers = await disabledKeysTask;

            if (summaryData.Length == 0 && disabledMembers.Length == 0)
                return null;

            var dict = summaryData.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

            var disabledKeyIds = disabledMembers
                .Where(m => m.HasValue)
                .Select(m => (int)m)
                .ToList();

            return new ProviderErrorSummary
            {
                ProviderId = providerId,
                TotalErrors = int.Parse(dict.GetValueOrDefault("total_errors", "0"), CultureInfo.InvariantCulture),
                FatalErrors = int.Parse(dict.GetValueOrDefault("fatal_errors", "0"), CultureInfo.InvariantCulture),
                Warnings = int.Parse(dict.GetValueOrDefault("warnings", "0"), CultureInfo.InvariantCulture),
                DisabledKeyIds = disabledKeyIds,
                LastError = dict.TryGetValue("last_error", out var lastError)
                    ? DateTime.Parse(lastError, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                ProviderDisabledAt = dict.TryGetValue("provider_disabled_at", out var disabledAt)
                    ? DateTime.Parse(disabledAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                ProviderDisableReason = dict.GetValueOrDefault("provider_disable_reason")
            };
        }

        public async Task<ErrorStatistics> GetErrorStatisticsAsync(TimeSpan window)
        {
            var stats = new ErrorStatistics();
            var cutoff = DateTime.UtcNow - window;
            
            // Get recent errors from feed
            var feedKey = CacheKeys.ProviderError.RecentFeed;
            var entries = await _db.SortedSetRangeByScoreAsync(
                feedKey,
                new DateTimeOffset(cutoff).ToUnixTimeSeconds(),
                new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds());
            
            foreach (var entry in entries)
            {
                try
                {
                    var data = JsonDocument.Parse(entry.ToString());
                    var errorType = data.RootElement.GetProperty("type").GetString()!;

                    stats.TotalErrors++;

                    // Count by type
                    if (!stats.ErrorsByType.ContainsKey(errorType))
                        stats.ErrorsByType[errorType] = 0;
                    stats.ErrorsByType[errorType]++;

                    // Count by provider
                    var providerId = data.RootElement.GetProperty("providerId")
                        .GetInt32()
                        .ToString(CultureInfo.InvariantCulture);
                    if (!stats.ErrorsByProvider.ContainsKey(providerId))
                        stats.ErrorsByProvider[providerId] = 0;
                    stats.ErrorsByProvider[providerId]++;

                    // Check if fatal
                    var errorTypeEnum = Enum.Parse<ProviderErrorType>(errorType);
                    if ((int)errorTypeEnum <= 9)
                        stats.FatalErrors++;
                    else
                        stats.Warnings++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse statistics entry");
                }
            }
            
            return stats;
        }
    }
}
