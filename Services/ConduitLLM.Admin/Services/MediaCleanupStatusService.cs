using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Options;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Service for tracking and reporting media cleanup status.
    /// Uses Redis for last run tracking and GlobalSettings for the runtime toggle.
    /// </summary>
    public class MediaCleanupStatusService : IMediaCleanupStatusService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConnectionMultiplexer? _redis;
        private readonly MediaLifecycleOptions _options;
        private readonly bool _isPublicMediaBaseUrlConfigured;
        private readonly ILogger<MediaCleanupStatusService> _logger;

        // Redis keys
        private const string REDIS_KEY_LAST_RUN = "media:cleanup:last-run";
        private const string REDIS_KEY_OPERATION_PREFIX = "media:cleanup:last-run:";
        private const string REDIS_KEY_RECONCILIATION_DRIFT = "media:cleanup:reconciliation-drift";
        private const string REDIS_KEY_LEADER = "media:cleanup:current-leader";

        // In-process fallback keeps status useful for single-instance deployments without Redis.
        private LastRunInfo? _lastRunFallback;
        private string? _leaderFallback;
        private readonly ConcurrentDictionary<string, LastRunInfo> _operationRunFallback = new();
        private ReconciliationDriftInfo _reconciliationDriftFallback = new();

        /// <summary>
        /// GlobalSetting key for the runtime enabled toggle.
        /// </summary>
        public const string SETTING_KEY_ENABLED = "MediaCleanup.Enabled";

        /// <summary>
        /// GlobalSetting key for the simple retention override (days).
        /// When set, overrides policy-based retention for all media.
        /// </summary>
        public const string SETTING_KEY_SIMPLE_RETENTION = "MediaCleanup.SimpleRetentionDays";

        /// <summary>
        /// Minimum allowed retention days for simple override.
        /// </summary>
        public const int MIN_RETENTION_DAYS = 1;

        /// <summary>
        /// Maximum allowed retention days for simple override.
        /// </summary>
        public const int MAX_RETENTION_DAYS = 365;

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaCleanupStatusService"/> class.
        /// </summary>
        public MediaCleanupStatusService(
            IServiceScopeFactory scopeFactory,
            IOptions<MediaLifecycleOptions> options,
            ILogger<MediaCleanupStatusService> logger,
            IConnectionMultiplexer? redis = null,
            IOptions<S3StorageOptions>? s3Options = null)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
            _redis = redis;
            _isPublicMediaBaseUrlConfigured =
                !string.IsNullOrWhiteSpace(s3Options?.Value.PublicBaseUrl);
        }

        /// <inheritdoc />
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
        public async Task<MediaCleanupStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IConfigurationDbContext>();
            var budgetService = scope.ServiceProvider.GetRequiredService<IMediaDeletionBudgetService>();
            var storageService = scope.ServiceProvider.GetService<IMediaStorageService>();
            var approvalService = scope.ServiceProvider.GetRequiredService<IMediaCleanupApprovalService>();

            // Get budget info
            var monthlyDeleteCount = await budgetService.GetMonthlyDeleteCountAsync(cancellationToken);
            var remainingBudget = await budgetService.GetRemainingBudgetAsync(_options.MonthlyDeleteBudget, cancellationToken);

            // Get last run info from Redis
            var lastRunInfo = await GetLastRunInfoAsync();
            var reconciliationDrift = await GetReconciliationDriftAsync();
            var operationStatuses = new List<MediaCleanupOperationStatusDto>();
            foreach (var cleanupType in MediaCleanupTypes.All)
            {
                var operationInfo = await GetLastRunInfoAsync(cleanupType);
                operationStatuses.Add(new MediaCleanupOperationStatusDto
                {
                    CleanupType = cleanupType,
                    IsEnabled = IsOperationEnabled(cleanupType),
                    LastRunTimeUtc = operationInfo?.LastRunTimeUtc,
                    LastRunStatus = operationInfo?.Status,
                    TriggeredBy = operationInfo?.TriggeredBy,
                    LastRunFilesDeleted = operationInfo?.FilesDeleted ?? 0,
                    LastRunBytesFreed = operationInfo?.BytesFreed ?? 0,
                    LastRunDurationSeconds = operationInfo?.DurationSeconds
                });
            }

            // Get default retention policy
            var defaultPolicy = await context.MediaRetentionPolicies
                .Where(p => p.IsDefault && p.IsActive)
                .Select(p => new RetentionPolicySummaryDto
                {
                    Name = p.Name,
                    PositiveBalanceRetentionDays = p.PositiveBalanceRetentionDays,
                    ZeroBalanceRetentionDays = p.ZeroBalanceRetentionDays,
                    NegativeBalanceRetentionDays = p.NegativeBalanceRetentionDays
                })
                .FirstOrDefaultAsync(cancellationToken);

            // Count active policies
            var activePoliciesCount = await context.MediaRetentionPolicies
                .CountAsync(p => p.IsActive, cancellationToken);

            // Check if enabled via runtime toggle
            var isEnabled = await IsEnabledAsync(cancellationToken);

            // Calculate next run time
            DateTime? nextScheduledRun = null;
            if (isEnabled && lastRunInfo?.LastRunTimeUtc != null)
            {
                nextScheduledRun = lastRunInfo.LastRunTimeUtc.Value.AddMinutes(_options.ScheduleIntervalMinutes);
                if (nextScheduledRun < DateTime.UtcNow)
                {
                    // If scheduled time has passed, it's likely running now or about to
                    nextScheduledRun = DateTime.UtcNow.AddMinutes(1);
                }
            }
            else if (isEnabled)
            {
                // First run would be ~30 seconds after startup
                nextScheduledRun = DateTime.UtcNow.AddMinutes(_options.ScheduleIntervalMinutes);
            }

            // Get current leader
            var currentLeader = await GetCurrentLeaderAsync();

            // Get simple retention override
            var simpleRetentionOverride = await GetSimpleRetentionOverrideAsync(cancellationToken);
            var pendingApprovals = await approvalService.ListPendingAsync(cancellationToken);

            // Calculate budget percentage
            var budgetUsedPercent = _options.MonthlyDeleteBudget > 0
                ? (double)monthlyDeleteCount / _options.MonthlyDeleteBudget * 100
                : 0;

            return new MediaCleanupStatusDto
            {
                IsEnabled = isEnabled,
                IsDryRunMode = _options.DryRunMode,
                IsSoftDeleteEnabled = _options.EnableSoftDelete,
                SoftDeleteGracePeriodDays = _options.SoftDeleteGracePeriodDays,
                StorageBackend = MediaStorageConfigurationGuard.GetBackendName(storageService),
                IsPublicMediaBaseUrlConfigured = _isPublicMediaBaseUrlConfigured,
                TestScopeActive = _options.TestVirtualKeyGroups.Count > 0,
                TestVirtualKeyGroups = _options.TestVirtualKeyGroups
                    .Distinct()
                    .Order()
                    .ToList(),
                UntrackedObjectCount = reconciliationDrift.UntrackedObjectCount,
                UntrackedBytes = reconciliationDrift.UntrackedBytes,
                LastRunTimeUtc = lastRunInfo?.LastRunTimeUtc,
                LastRunStatus = lastRunInfo?.Status,
                LastRunTriggeredBy = lastRunInfo?.TriggeredBy,
                LastRunFilesDeleted = lastRunInfo?.FilesDeleted ?? 0,
                LastRunBytesFreed = lastRunInfo?.BytesFreed ?? 0,
                LastRunDurationSeconds = lastRunInfo?.DurationSeconds,
                MonthlyDeleteCount = monthlyDeleteCount,
                MonthlyDeleteBudget = _options.MonthlyDeleteBudget,
                MonthlyDeleteBudgetRemaining = remainingBudget,
                MonthlyBudgetUsedPercent = Math.Round(budgetUsedPercent, 2),
                BudgetAlertThresholdPercent = Math.Clamp(
                    _options.BudgetAlertThresholdPercent,
                    0,
                    100),
                BudgetBackend = budgetService.BackendName,
                IsBudgetBackendPersistent = budgetService.IsPersistent,
                BudgetFailureMode = _options.BudgetFailureMode.ToString(),
                BudgetLastFailureAtUtc = budgetService.LastFailureAtUtc,
                ScheduleIntervalMinutes = _options.ScheduleIntervalMinutes,
                MaxBatchSize = _options.MaxBatchSize,
                MaxRecordsPerRun = _options.MaxRecordsPerRun,
                DefaultRetentionPolicy = defaultPolicy,
                ActiveRetentionPoliciesCount = activePoliciesCount,
                SimpleRetentionOverrideDays = simpleRetentionOverride,
                NextScheduledRunUtc = nextScheduledRun,
                CurrentLeaderInstanceId = currentLeader,
                OperationStatuses = operationStatuses,
                PendingApprovals = pendingApprovals.ToList()
            };
        }

        /// <inheritdoc />
        public async Task RecordRunCompletionAsync(
            int filesDeleted,
            long bytesFreed,
            double durationSeconds,
            string status,
            string leaderInstanceId,
            string triggeredBy,
            CancellationToken cancellationToken = default)
        {
            var runInfo = CreateRunInfo(
                filesDeleted, bytesFreed, durationSeconds, status, leaderInstanceId, triggeredBy);
            _lastRunFallback = runInfo;
            _leaderFallback = leaderInstanceId;

            await PersistRunInfoAsync(REDIS_KEY_LAST_RUN, runInfo, leaderInstanceId);

            _logger.LogDebug(
                "Recorded cleanup run completion: {FilesDeleted} files, {BytesFreed} bytes, {Duration:F2}s",
                filesDeleted, bytesFreed, durationSeconds);
        }

        /// <inheritdoc />
        public async Task RecordOperationCompletionAsync(
            string cleanupType,
            int filesDeleted,
            long bytesFreed,
            double durationSeconds,
            string status,
            string leaderInstanceId,
            string triggeredBy,
            CancellationToken cancellationToken = default)
        {
            var runInfo = CreateRunInfo(
                filesDeleted, bytesFreed, durationSeconds, status, leaderInstanceId, triggeredBy);
            _operationRunFallback[cleanupType] = runInfo;
            _leaderFallback = leaderInstanceId;

            await PersistRunInfoAsync(
                REDIS_KEY_OPERATION_PREFIX + cleanupType, runInfo, leaderInstanceId);

            _logger.LogDebug(
                "Recorded {CleanupType} cleanup completion: {Status}, {FilesDeleted} files, {Duration:F2}s",
                cleanupType, status, filesDeleted, durationSeconds);
        }

        /// <inheritdoc />
        public async Task RecordReconciliationDriftAsync(
            int untrackedObjectCount,
            long untrackedBytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var drift = new ReconciliationDriftInfo
            {
                UntrackedObjectCount = untrackedObjectCount,
                UntrackedBytes = untrackedBytes,
                ObservedAtUtc = DateTime.UtcNow
            };
            _reconciliationDriftFallback = drift;

            if (_redis == null)
            {
                return;
            }

            try
            {
                var db = _redis.GetDatabase();
                await db.StringSetAsync(
                    REDIS_KEY_RECONCILIATION_DRIFT,
                    JsonSerializer.Serialize(
                        drift,
                        MediaCleanupRedisJsonContext.Default.ReconciliationDriftInfo),
                    TimeSpan.FromDays(7));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording reconciliation drift");
            }
        }

        private async Task PersistRunInfoAsync(
            string redisKey,
            LastRunInfo runInfo,
            string leaderInstanceId)
        {
            if (_redis == null)
            {
                return;
            }

            try
            {
                var db = _redis.GetDatabase();
                var json = JsonSerializer.Serialize(
                    runInfo,
                    MediaCleanupRedisJsonContext.Default.LastRunInfo);
                await db.StringSetAsync(redisKey, json, TimeSpan.FromDays(7));
                await db.StringSetAsync(REDIS_KEY_LEADER, leaderInstanceId, TimeSpan.FromMinutes(35));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording cleanup run status to Redis key {RedisKey}", redisKey);
            }
        }

        private static LastRunInfo CreateRunInfo(
            int filesDeleted,
            long bytesFreed,
            double durationSeconds,
            string status,
            string leaderInstanceId,
            string triggeredBy) => new()
            {
                LastRunTimeUtc = DateTime.UtcNow,
                FilesDeleted = filesDeleted,
                BytesFreed = bytesFreed,
                DurationSeconds = durationSeconds,
                Status = status,
                LeaderInstanceId = leaderInstanceId,
                TriggeredBy = triggeredBy
            };

        /// <inheritdoc />
        public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
        {
            // First check the config option
            if (!_options.IsSchedulerEnabled)
            {
                return false;
            }

            // Then check the runtime toggle in GlobalSettings
            using var scope = _scopeFactory.CreateScope();
            var settingRepository = scope.ServiceProvider.GetRequiredService<IGlobalSettingRepository>();

            try
            {
                var setting = await settingRepository.GetByKeyAsync(SETTING_KEY_ENABLED);
                if (setting == null)
                {
                    // If no setting exists, default to the config value
                    return _options.IsSchedulerEnabled;
                }

                if (bool.TryParse(setting.Value, out var enabled))
                {
                    return enabled;
                }

                // Handle common string representations
                var normalized = setting.Value.Trim().ToLowerInvariant();
                return normalized == "1" || normalized == "yes" || normalized == "on" || normalized == "true";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking cleanup enabled state from GlobalSettings");
                // Default to config value on error
                return _options.IsSchedulerEnabled;
            }
        }

        /// <inheritdoc />
        public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var globalSettingService = scope.ServiceProvider.GetRequiredService<IAdminGlobalSettingService>();

            // Use the service layer which publishes GlobalSettingChanged events
            await globalSettingService.UpdateSettingByKeyAsync(new UpdateGlobalSettingByKeyDto
            {
                Key = SETTING_KEY_ENABLED,
                Value = enabled.ToString().ToLowerInvariant(),
                Description = "Runtime toggle for the media cleanup service"
            });

            _logger.LogInformation("Media cleanup service enabled state changed to: {Enabled}", enabled);
        }

        /// <inheritdoc />
        public async Task<int?> GetSimpleRetentionOverrideAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var settingRepository = scope.ServiceProvider.GetRequiredService<IGlobalSettingRepository>();

            try
            {
                var setting = await settingRepository.GetByKeyAsync(SETTING_KEY_SIMPLE_RETENTION, cancellationToken);
                if (setting == null || string.IsNullOrWhiteSpace(setting.Value))
                {
                    return null;
                }

                if (int.TryParse(setting.Value, out var days) && days >= MIN_RETENTION_DAYS && days <= MAX_RETENTION_DAYS)
                {
                    return days;
                }

                _logger.LogWarning(
                    "Invalid simple retention override value '{Value}' - must be {Min}-{Max} days",
                    setting.Value, MIN_RETENTION_DAYS, MAX_RETENTION_DAYS);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading simple retention override from GlobalSettings");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task SetSimpleRetentionOverrideAsync(int? days, CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var globalSettingService = scope.ServiceProvider.GetRequiredService<IAdminGlobalSettingService>();

            try
            {
                if (days.HasValue)
                {
                    // Validate the value
                    if (days.Value < MIN_RETENTION_DAYS || days.Value > MAX_RETENTION_DAYS)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(days),
                            $"Retention days must be between {MIN_RETENTION_DAYS} and {MAX_RETENTION_DAYS}");
                    }

                    // Use the service layer which publishes GlobalSettingChanged events
                    await globalSettingService.UpdateSettingByKeyAsync(new UpdateGlobalSettingByKeyDto
                    {
                        Key = SETTING_KEY_SIMPLE_RETENTION,
                        Value = days.Value.ToString(),
                        Description = "Simple retention override - all media deleted after this many days regardless of balance"
                    });

                    _logger.LogInformation("Simple retention override set to {Days} days", days.Value);
                }
                else
                {
                    // Clear the override by deleting the setting
                    await globalSettingService.DeleteSettingByKeyAsync(SETTING_KEY_SIMPLE_RETENTION);
                    _logger.LogInformation("Simple retention override cleared - using policy-based retention");
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                throw; // Re-throw validation errors
            }
        }

        private bool IsOperationEnabled(string cleanupType) => cleanupType switch
        {
            MediaCleanupTypes.Expiration => _options.EnableExpirationCleanup,
            MediaCleanupTypes.Purge => true,
            MediaCleanupTypes.Reconciliation => _options.EnableReconciliation,
            MediaCleanupTypes.Quota => _options.EnableQuotaCleanup,
            MediaCleanupTypes.Retention => _options.EnableRetentionCleanup,
            _ => false
        };

        private async Task<ReconciliationDriftInfo> GetReconciliationDriftAsync()
        {
            if (_redis == null)
            {
                return _reconciliationDriftFallback;
            }

            try
            {
                var db = _redis.GetDatabase();
                var json = await db.StringGetAsync(REDIS_KEY_RECONCILIATION_DRIFT);
                return json.IsNullOrEmpty
                    ? _reconciliationDriftFallback
                    : JsonSerializer.Deserialize(
                        json.ToString(),
                        MediaCleanupRedisJsonContext.Default.ReconciliationDriftInfo)
                        ?? _reconciliationDriftFallback;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading reconciliation drift");
                return _reconciliationDriftFallback;
            }
        }

        private async Task<LastRunInfo?> GetLastRunInfoAsync(string? cleanupType = null)
        {
            var fallback = cleanupType == null
                ? _lastRunFallback
                : _operationRunFallback.GetValueOrDefault(cleanupType);

            if (_redis == null)
            {
                return fallback;
            }

            try
            {
                var db = _redis.GetDatabase();
                var redisKey = cleanupType == null
                    ? REDIS_KEY_LAST_RUN
                    : REDIS_KEY_OPERATION_PREFIX + cleanupType;
                var json = await db.StringGetAsync(redisKey);

                if (json.IsNullOrEmpty)
                {
                    return fallback;
                }

                return JsonSerializer.Deserialize(
                    json.ToString(),
                    MediaCleanupRedisJsonContext.Default.LastRunInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading last run info from Redis");
                return fallback;
            }
        }

        private async Task<string?> GetCurrentLeaderAsync()
        {
            if (_redis == null)
            {
                return _leaderFallback;
            }

            try
            {
                var db = _redis.GetDatabase();
                var leader = await db.StringGetAsync(REDIS_KEY_LEADER);
                return leader.IsNullOrEmpty ? _leaderFallback : leader.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading current leader from Redis");
                return _leaderFallback;
            }
        }

    }
}
