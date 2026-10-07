using ConduitLLM.Core.Extensions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Admin.Metrics;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Background service for media lifecycle management.
    /// Uses distributed locking to ensure only one instance runs cleanup across a cluster.
    /// Combines scheduling, retention evaluation, and deletion in a single service.
    /// </summary>
    public class MediaCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IDistributedLockProvider _lockService;
        private readonly MediaLifecycleOptions _options;
        private readonly ILogger<MediaCleanupService> _logger;
        private readonly IMediaStorageConfigurationGuard? _storageConfigurationGuard;
        private readonly string _instanceId;

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaCleanupService"/> class.
        /// </summary>
        /// <param name="serviceScopeFactory">Factory for creating service scopes</param>
        /// <param name="lockService">Distributed lock service for leader election</param>
        /// <param name="options">Media lifecycle configuration options</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="storageConfigurationGuard">Storage safety guard</param>
        public MediaCleanupService(
            IServiceScopeFactory serviceScopeFactory,
            IDistributedLockProvider lockService,
            IOptions<MediaLifecycleOptions> options,
            ILogger<MediaCleanupService> logger,
            IMediaStorageConfigurationGuard? storageConfigurationGuard = null)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _lockService = lockService;
            _options = options.Value;
            _logger = logger;
            _storageConfigurationGuard = storageConfigurationGuard;
            _instanceId = Guid.NewGuid().ToString("N")[..8];
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.IsSchedulerEnabled)
            {
                _logger.LogInformation(
                    "Media cleanup service disabled for instance {InstanceId}. Set MediaLifecycle:Enabled=true to enable.",
                    _instanceId);
                return;
            }

            _logger.LogInformation(
                "Media cleanup service starting on instance {InstanceId} - DryRun: {DryRun}, Interval: {Interval} minutes",
                _instanceId, _options.DryRunMode, _options.ScheduleIntervalMinutes);

            // Initial delay to let the application fully start
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Check runtime toggle before attempting cleanup
                    if (await IsRuntimeEnabledAsync(stoppingToken))
                    {
                        await RunScheduledCleanupAsync(stoppingToken);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Media cleanup service is disabled via runtime toggle on instance {InstanceId}",
                            _instanceId);
                    }

                    await Task.Delay(
                        TimeSpan.FromMinutes(_options.ScheduleIntervalMinutes),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unexpected error in media cleanup service on instance {InstanceId}",
                        _instanceId);

                    // Wait before retrying to avoid tight error loops
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation(
                "Media cleanup service stopping on instance {InstanceId}",
                _instanceId);
        }

        /// <summary>
        /// Checks if the cleanup service is enabled via runtime toggle.
        /// </summary>
        private async Task<bool> IsRuntimeEnabledAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var statusService = scope.ServiceProvider.GetService<IMediaCleanupStatusService>();
                if (statusService == null)
                {
                    // If status service is not available, fall back to config
                    return _options.IsSchedulerEnabled;
                }
                return await statusService.IsEnabledAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking runtime enabled state, using config default");
                return _options.IsSchedulerEnabled;
            }
        }

        public async Task RunScheduledCleanupAsync(CancellationToken stoppingToken)
        {
            if (_storageConfigurationGuard != null &&
                !await _storageConfigurationGuard.ValidateAsync(stoppingToken))
            {
                _logger.LogCritical(
                    "Instance {InstanceId} refused to run media cleanup because the storage configuration is unsafe.",
                    _instanceId);
                return;
            }

            await using var lockHandle = await _lockService.TryAcquireAsync(
                MediaCleanupLock.Key,
                TimeSpan.Zero,
                stoppingToken);

            if (lockHandle == null)
            {
                _logger.LogDebug(
                    "Instance {InstanceId} could not acquire cleanup lock - another instance is leader",
                    _instanceId);
                return;
            }

            _logger.LogInformation(
                "Instance {InstanceId} acquired cleanup leadership",
                _instanceId);

            using var operationCancellation = lockHandle.CreateOperationCancellation(
                stoppingToken, MediaCleanupLock.OperationDeadline);

            try
            {
                await RunCleanupAsync(operationCancellation.Token);
                operationCancellation.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                _logger.LogDebug(
                    "Instance {InstanceId} releasing cleanup leadership",
                    _instanceId);
            }
        }

        private async Task RunCleanupAsync(CancellationToken stoppingToken)
        {
            var stopwatch = Stopwatch.StartNew();
            using var activity = AdminRequestMetrics.StartMediaCleanupActivity(_instanceId, _options.DryRunMode);
            var status = "Completed";

            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IConfigurationDbContext>();
            var deletionEngine = scope.ServiceProvider.GetRequiredService<IMediaDeletionEngine>();
            var reconciliationService =
                scope.ServiceProvider.GetRequiredService<IMediaReconciliationService>();
            var statusService = scope.ServiceProvider.GetService<IMediaCleanupStatusService>();
            var totalDeleted = 0;
            var totalTombstoned = 0;
            long totalBytesFreed = 0;
            var operationFailures = 0;
            var processedRecordIds = new HashSet<Guid>();
            var runLimiter = new CleanupRunLimiter(_options.MaxRecordsPerRun);

            try
            {
                if (_options.TestVirtualKeyGroups.Any())
                {
                    _logger.LogInformation(
                        "Running in test mode - only processing groups: {Groups}",
                        string.Join(", ", _options.TestVirtualKeyGroups));
                }

                var purgeOperation = new MediaDeletionOperationContext(
                    MediaCleanupTypes.Purge, "scheduled", _instanceId);
                var purgeResult = await deletionEngine.ExecuteOperationAsync(
                    purgeOperation,
                    () => ProcessPurgeAsync(
                        context,
                        deletionEngine,
                        purgeOperation,
                        runLimiter,
                        stoppingToken),
                    stoppingToken);
                totalDeleted += purgeResult.FilesDeleted;
                totalBytesFreed += purgeResult.BytesFreed;
                operationFailures += purgeResult.Failures;

                if (_options.EnableExpirationCleanup)
                {
                    var operation = new MediaDeletionOperationContext(
                        MediaCleanupTypes.Expiration, "scheduled", _instanceId);
                    var result = await deletionEngine.ExecuteOperationAsync(
                        operation,
                        () => ProcessExpiredMediaAsync(
                            context, deletionEngine, operation,
                            processedRecordIds, runLimiter, stoppingToken),
                        stoppingToken);
                    totalDeleted += result.FilesDeleted;
                    totalTombstoned += result.RecordsTombstoned;
                    totalBytesFreed += result.BytesFreed;
                    operationFailures += result.Failures;
                }

                if (_options.EnableReconciliation)
                {
                    var operation = new MediaDeletionOperationContext(
                        MediaCleanupTypes.Reconciliation, "scheduled", _instanceId);
                    var result = await deletionEngine.ExecuteOperationAsync(
                        operation,
                        () => reconciliationService.ReconcileAsync(
                            operation, stoppingToken),
                        stoppingToken);
                    totalDeleted += result.FilesDeleted;
                    totalTombstoned += result.RecordsTombstoned;
                    totalBytesFreed += result.BytesFreed;
                    operationFailures += result.Failures;
                }

                if (_options.EnableQuotaCleanup)
                {
                    var operation = new MediaDeletionOperationContext(
                        MediaCleanupTypes.Quota, "scheduled", _instanceId);
                    var quotaService = scope.ServiceProvider.GetRequiredService<IMediaQuotaService>();
                    var result = await deletionEngine.ExecuteOperationAsync(
                        operation,
                        () => ProcessQuotaMediaAsync(
                            context,
                            quotaService,
                            deletionEngine,
                            operation,
                            processedRecordIds,
                            runLimiter,
                            stoppingToken),
                        stoppingToken);
                    totalDeleted += result.FilesDeleted;
                    totalBytesFreed += result.BytesFreed;
                    operationFailures += result.Failures;
                }

                if (_options.EnableRetentionCleanup)
                {
                    var simpleRetentionOverride =
                        await ResolveSimpleRetentionOverrideAsync(
                            statusService,
                            stoppingToken);
                    var operation = new MediaDeletionOperationContext(
                        MediaCleanupTypes.Retention, "scheduled", _instanceId);
                    var result = await deletionEngine.ExecuteOperationAsync(
                        operation,
                        () => ProcessRetentionMediaAsync(
                            context, deletionEngine, operation,
                            processedRecordIds, runLimiter,
                            simpleRetentionOverride, stoppingToken),
                        stoppingToken);
                    totalDeleted += result.FilesDeleted;
                    totalTombstoned += result.RecordsTombstoned;
                    totalBytesFreed += result.BytesFreed;
                    operationFailures += result.Failures;
                }

                if (runLimiter.WasTruncated)
                {
                    status = operationFailures > 0
                        ? runLimiter.Status + " with errors"
                        : runLimiter.Status;
                }
                else if (operationFailures > 0)
                {
                    status = "Completed with errors";
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                status = "Cancelled";
                throw;
            }
            catch (Exception ex)
            {
                status = $"Failed: {ex.Message}";
                throw;
            }
            finally
            {
                stopwatch.Stop();

                _logger.LogInformation(
                    "Media cleanup {Status}. Permanently deleted {DeletedCount} files, tombstoned {TombstonedCount} records, and freed {Bytes:N0} bytes in {Duration:F2}s",
                    status,
                    totalDeleted,
                    totalTombstoned,
                    totalBytesFreed,
                    stopwatch.Elapsed.TotalSeconds);

                // Record run completion for status tracking
                if (statusService != null)
                {
                    using var completionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await statusService.RecordRunCompletionAsync(
                        totalDeleted,
                        totalBytesFreed,
                        stopwatch.Elapsed.TotalSeconds,
                        status,
                        _instanceId,
                        "scheduled",
                        stoppingToken.IsCancellationRequested ? completionCancellation.Token : stoppingToken);
                }

            }
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
        private async Task<MediaDeletionEngineResult> ProcessPurgeAsync(
            IConfigurationDbContext context,
            IMediaDeletionEngine deletionEngine,
            MediaDeletionOperationContext operation,
            CleanupRunLimiter runLimiter,
            CancellationToken stoppingToken)
        {
            var defaultPolicyGrace = await context.MediaRetentionPolicies
                .Where(policy => policy.IsDefault && policy.IsActive)
                .Select(policy => (int?)policy.SoftDeleteGracePeriodDays)
                .FirstOrDefaultAsync(stoppingToken);
            var fallbackGrace = Math.Max(
                0,
                defaultPolicyGrace ?? _options.SoftDeleteGracePeriodDays);
            var baseQuery = context.MediaRecords
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(media => media.DeletedAt != null);

            if (_options.TestVirtualKeyGroups.Any())
            {
                baseQuery = baseQuery.Where(media => context.VirtualKeys.Any(key =>
                    key.Id == media.VirtualKeyId &&
                    _options.TestVirtualKeyGroups.Contains(key.VirtualKeyGroupId)));
            }

            var policyWindows = await context.MediaRetentionPolicies
                .AsNoTracking()
                .Select(policy => new
                {
                    policy.Id,
                    GraceDays = policy.SoftDeleteGracePeriodDays
                })
                .ToListAsync(stoppingToken);
            var now = DateTime.UtcNow;
            IQueryable<MediaRecord> purgeQuery = baseQuery
                .Where(media => context.VirtualKeys.Any(key =>
                    key.Id == media.VirtualKeyId &&
                    key.VirtualKeyGroup.MediaRetentionPolicyId == null))
                .Where(media => media.DeletedAt < now.AddDays(-fallbackGrace));
            foreach (var policyWindow in policyWindows)
            {
                var policyId = policyWindow.Id;
                var cutoff = now.AddDays(-Math.Max(0, policyWindow.GraceDays));
                var assignedQuery = baseQuery
                    .Where(media => context.VirtualKeys.Any(key =>
                        key.Id == media.VirtualKeyId &&
                        key.VirtualKeyGroup.MediaRetentionPolicyId == policyId))
                    .Where(media => media.DeletedAt < cutoff);
                purgeQuery = purgeQuery.Concat(assignedQuery);
            }

            return await ProcessPagedMediaAsync(
                purgeQuery,
                deletionEngine,
                operation,
                runLimiter,
                processedRecordIds: null,
                groupId: null,
                purge: true,
                stoppingToken);
        }

        private async Task<MediaDeletionEngineResult> ProcessExpiredMediaAsync(
            IConfigurationDbContext context,
            IMediaDeletionEngine deletionEngine,
            MediaDeletionOperationContext operation,
            HashSet<Guid> processedRecordIds,
            CleanupRunLimiter runLimiter,
            CancellationToken stoppingToken)
        {
            var now = DateTime.UtcNow;
            var query = context.MediaRecords
                .AsNoTracking()
                .Where(media => media.ExpiresAt != null && media.ExpiresAt <= now);

            if (_options.TestVirtualKeyGroups.Any())
            {
                query = query.Where(media => context.VirtualKeys.Any(key =>
                    key.Id == media.VirtualKeyId &&
                    _options.TestVirtualKeyGroups.Contains(key.VirtualKeyGroupId)));
            }

            return await ProcessPagedMediaAsync(
                query,
                deletionEngine,
                operation,
                runLimiter,
                processedRecordIds,
                groupId: null,
                purge: false,
                stoppingToken);
        }

        private async Task<MediaDeletionEngineResult> ProcessRetentionMediaAsync(
            IConfigurationDbContext context,
            IMediaDeletionEngine deletionEngine,
            MediaDeletionOperationContext operation,
            HashSet<Guid> processedRecordIds,
            CleanupRunLimiter runLimiter,
            int? simpleRetentionOverride,
            CancellationToken stoppingToken)
        {
            IQueryable<int> groupQuery = context.VirtualKeyGroups.Select(group => group.Id);
            if (_options.TestVirtualKeyGroups.Any())
            {
                groupQuery = groupQuery.Where(groupId => _options.TestVirtualKeyGroups.Contains(groupId));
            }

            var groupIds = await groupQuery.ToListAsync(stoppingToken);
            var result = MediaDeletionEngineResult.Empty;

            foreach (var groupId in groupIds)
            {
                stoppingToken.ThrowIfCancellationRequested();

                var groupResult = await ProcessGroupAsync(
                    groupId,
                    context,
                    deletionEngine,
                    operation,
                    processedRecordIds,
                    runLimiter,
                    simpleRetentionOverride,
                    stoppingToken);
                result = result.Combine(groupResult);

                if (groupResult.BudgetExhausted ||
                    groupResult.StatusOverride == runLimiter.Status)
                    break;

                await Task.Delay(100, stoppingToken);
            }

            AdminMediaCleanupMetrics.GroupsProcessed.Observe(groupIds.Count);
            return result;
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
        private async Task<MediaDeletionEngineResult> ProcessQuotaMediaAsync(
            IConfigurationDbContext context,
            IMediaQuotaService quotaService,
            IMediaDeletionEngine deletionEngine,
            MediaDeletionOperationContext operation,
            HashSet<Guid> processedRecordIds,
            CleanupRunLimiter runLimiter,
            CancellationToken stoppingToken)
        {
            var usages = await quotaService.GetGroupUsagesAsync(
                cancellationToken: stoppingToken);
            var overQuota = usages
                .Where(usage => usage.IsOverQuota)
                .Where(usage =>
                    !_options.TestVirtualKeyGroups.Any() ||
                    _options.TestVirtualKeyGroups.Contains(usage.VirtualKeyGroupId))
                .ToList();
            var result = MediaDeletionEngineResult.Empty;

            foreach (var usage in overQuota)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (runLimiter.IsExhausted)
                {
                    runLimiter.MarkTruncated();
                    return result with { StatusOverride = runLimiter.Status };
                }

                var recentCutoff = DateTime.UtcNow.AddDays(-usage.RecentAccessWindowDays);
                var eligibleQuery = context.MediaRecords
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(media => context.VirtualKeys.Any(key =>
                        key.Id == media.VirtualKeyId &&
                        key.VirtualKeyGroupId == usage.VirtualKeyGroupId))
                    .Where(media =>
                        !usage.RespectRecentAccess ||
                        media.DeletedAt != null ||
                        media.LastAccessedAt == null ||
                        media.LastAccessedAt < recentCutoff);
                var totalEligibleCount = await eligibleQuery.CountAsync(stoppingToken);
                if (totalEligibleCount == 0)
                {
                    _logger.LogWarning(
                        "Group {GroupId} is over media quota but recent-access protection left no eligible files",
                        usage.VirtualKeyGroupId);
                    continue;
                }

                var totalEligibleBytes = await eligibleQuery
                    .SumAsync(media => media.SizeBytes ?? 0, stoppingToken);
                var projectedFiles = usage.TotalFiles;
                var projectedBytes = usage.TotalSizeBytes;
                var processedInScope = 0;
                DateTime? cursorCreatedAt = null;
                var cursorId = Guid.Empty;
                var pageSize = Math.Clamp(_options.CleanupPageSize, 1, 1000);

                while (ExceedsQuota(usage, projectedFiles, projectedBytes) &&
                       !runLimiter.IsExhausted &&
                       processedInScope < totalEligibleCount)
                {
                    var take = Math.Min(pageSize, runLimiter.Remaining);
                    var pageQuery = eligibleQuery;
                    if (cursorCreatedAt.HasValue)
                    {
                        var createdAt = cursorCreatedAt.Value;
                        pageQuery = pageQuery.Where(media =>
                            media.CreatedAt > createdAt ||
                            (media.CreatedAt == createdAt &&
                             media.Id.CompareTo(cursorId) > 0));
                    }

                    var page = await pageQuery
                        .OrderBy(media => media.CreatedAt)
                        .ThenBy(media => media.Id)
                        .Select(media => new MediaRecord
                        {
                            Id = media.Id,
                            StorageKey = media.StorageKey,
                            SizeBytes = media.SizeBytes,
                            CreatedAt = media.CreatedAt
                        })
                        .Take(take)
                        .ToListAsync(stoppingToken);
                    if (page.Count == 0)
                        break;

                    processedInScope += page.Count;
                    runLimiter.Consume(page.Count);
                    var last = page[^1];
                    cursorCreatedAt = last.CreatedAt;
                    cursorId = last.Id;
                    var evictions = new List<MediaRecord>();
                    foreach (var candidate in page)
                    {
                        if (!ExceedsQuota(usage, projectedFiles, projectedBytes))
                            break;
                        if (processedRecordIds.Contains(candidate.Id))
                            continue;

                        evictions.Add(candidate);
                        projectedFiles--;
                        projectedBytes = Math.Max(
                            0,
                            projectedBytes - (candidate.SizeBytes ?? 0));
                    }

                    if (evictions.Count > 0)
                    {
                        _logger.LogInformation(
                            "Group {GroupId} exceeds media quota ({Files}/{MaxFiles} files, {Bytes}/{MaxBytes} bytes); evicting {Count} oldest eligible files",
                            usage.VirtualKeyGroupId,
                            usage.TotalFiles,
                            usage.MaxFileCount,
                            usage.TotalSizeBytes,
                            usage.MaxStorageSizeBytes,
                            evictions.Count);

                        var hasMore = processedInScope < totalEligibleCount;
                        var groupResult = await deletionEngine.DeleteAsync(
                            new MediaDeletionRequest(
                                evictions,
                                operation,
                                usage.VirtualKeyGroupId,
                                processedRecordIds,
                                Purge: true,
                                TotalEligibleCount: totalEligibleCount,
                                TotalEligibleBytes: totalEligibleBytes,
                                IsFinalPage:
                                    !ExceedsQuota(
                                        usage,
                                        projectedFiles,
                                        projectedBytes) ||
                                    !hasMore),
                            stoppingToken);
                        result = result.Combine(groupResult);

                        if (groupResult.BudgetExhausted ||
                            groupResult.StatusOverride?.StartsWith(
                                "Skipped",
                                StringComparison.OrdinalIgnoreCase) == true ||
                            groupResult.StatusOverride?.StartsWith(
                                "Blocked",
                                StringComparison.OrdinalIgnoreCase) == true)
                        {
                            return result;
                        }
                    }
                }

                if (runLimiter.IsExhausted &&
                    ExceedsQuota(usage, projectedFiles, projectedBytes))
                {
                    runLimiter.MarkTruncated();
                    return result with { StatusOverride = runLimiter.Status };
                }
                if (result.BudgetExhausted)
                    break;
            }

            return result;
        }

        private static bool ExceedsQuota(
            MediaGroupQuotaUsage usage,
            int projectedFiles,
            long projectedBytes)
        {
            return
                (usage.MaxFileCount.HasValue && projectedFiles > usage.MaxFileCount.Value) ||
                (usage.MaxStorageSizeBytes.HasValue &&
                    projectedBytes > usage.MaxStorageSizeBytes.Value);
        }

        private async Task<MediaDeletionEngineResult> ProcessGroupAsync(
            int groupId,
            IConfigurationDbContext context,
            IMediaDeletionEngine deletionEngine,
            MediaDeletionOperationContext operation,
            HashSet<Guid> processedRecordIds,
            CleanupRunLimiter runLimiter,
            int? simpleRetentionOverride,
            CancellationToken stoppingToken)
        {
            try
            {
                // Get VirtualKeyGroup with retention policy
                var group = await context.VirtualKeyGroups
                    .Include(g => g.MediaRetentionPolicy)
                    .FirstOrDefaultAsync(g => g.Id == groupId, stoppingToken);

                if (group == null)
                {
                    _logger.LogWarning("VirtualKeyGroup {GroupId} not found", groupId);
                    return MediaDeletionEngineResult.Empty;
                }

                var retention = await ResolveRetentionSettingsAsync(
                    group,
                    context,
                    simpleRetentionOverride,
                    stoppingToken);
                if (retention == null)
                    return MediaDeletionEngineResult.Empty;

                var cutoffDate = DateTime.UtcNow.AddDays(-retention.Value.retentionDays);
                var recentAccessCutoff = DateTime.UtcNow.AddDays(
                    -retention.Value.recentAccessWindowDays);
                var eligibleQuery = context.MediaRecords
                    .AsNoTracking()
                    .Where(media => context.VirtualKeys.Any(key =>
                        key.Id == media.VirtualKeyId &&
                        key.VirtualKeyGroupId == group.Id))
                    .Where(media => media.CreatedAt < cutoffDate)
                    .Where(media =>
                        !retention.Value.respectRecentAccess ||
                        media.LastAccessedAt == null ||
                        media.LastAccessedAt < recentAccessCutoff);

                return await ProcessPagedMediaAsync(
                    eligibleQuery,
                    deletionEngine,
                    operation,
                    runLimiter,
                    processedRecordIds,
                    groupId,
                    purge: false,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing cleanup for group {GroupId}", groupId);
                AdminMediaCleanupMetrics.CleanupErrors
                    .WithLabels(MediaCleanupTypes.Retention, "group_processing")
                    .Inc();
                return new MediaDeletionEngineResult(Failures: 1);
            }
        }

        /// <summary>
        /// Resolves retention settings for a group, preferring simple override over policy-based retention.
        /// Returns null if no retention settings can be determined.
        /// </summary>
        private async Task<(int retentionDays, bool respectRecentAccess, int recentAccessWindowDays)?> ResolveRetentionSettingsAsync(
            VirtualKeyGroup group,
            IConfigurationDbContext context,
            int? simpleRetentionOverride,
            CancellationToken stoppingToken)
        {
            if (simpleRetentionOverride.HasValue)
            {
                _logger.LogDebug(
                    "Group {GroupId}: Using simple retention override of {Days} days (ignoring balance-based policy)",
                    group.Id, simpleRetentionOverride.Value);
                return (simpleRetentionOverride.Value, false, 0);
            }

            // No override - use balance-aware policy-based retention
            var policy = group.MediaRetentionPolicy ?? await GetDefaultPolicyAsync(context, stoppingToken);
            if (policy == null)
            {
                _logger.LogDebug(
                    "No retention policy found for group {GroupId} and no default policy exists",
                    group.Id);
                return null;
            }

            var retentionDays = group.Balance switch
            {
                > 0 => policy.PositiveBalanceRetentionDays,
                0 => policy.ZeroBalanceRetentionDays,
                < 0 => policy.NegativeBalanceRetentionDays
            };

            _logger.LogDebug(
                "Group {GroupId} balance: {Balance:C}, retention days: {Days} (policy: {PolicyName})",
                group.Id, group.Balance, retentionDays, policy.Name);

            return (retentionDays, policy.RespectRecentAccess, policy.RecentAccessWindowDays);
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
        private async Task<MediaDeletionEngineResult> ProcessPagedMediaAsync(
            IQueryable<MediaRecord> eligibleQuery,
            IMediaDeletionEngine deletionEngine,
            MediaDeletionOperationContext operation,
            CleanupRunLimiter runLimiter,
            ISet<Guid>? processedRecordIds,
            int? groupId,
            bool purge,
            CancellationToken stoppingToken)
        {
            var totalEligibleCount = await eligibleQuery.CountAsync(stoppingToken);
            if (totalEligibleCount == 0)
                return MediaDeletionEngineResult.Empty;
            if (runLimiter.IsExhausted)
            {
                runLimiter.MarkTruncated();
                return new MediaDeletionEngineResult(
                    StatusOverride: runLimiter.Status);
            }

            var totalEligibleBytes = await eligibleQuery
                .SumAsync(media => media.SizeBytes ?? 0, stoppingToken);
            _logger.LogInformation(
                "Found {Count} media files eligible for {CleanupType} cleanup in group {GroupId}",
                totalEligibleCount,
                operation.CleanupType,
                groupId);

            var result = MediaDeletionEngineResult.Empty;
            var processedInScope = 0;
            DateTime? cursorCreatedAt = null;
            var cursorId = Guid.Empty;
            var pageSize = Math.Clamp(_options.CleanupPageSize, 1, 1000);

            while (!runLimiter.IsExhausted && processedInScope < totalEligibleCount)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var take = Math.Min(pageSize, runLimiter.Remaining);
                var pageQuery = eligibleQuery;
                if (cursorCreatedAt.HasValue)
                {
                    var createdAt = cursorCreatedAt.Value;
                    pageQuery = pageQuery.Where(media =>
                        media.CreatedAt > createdAt ||
                        (media.CreatedAt == createdAt && media.Id.CompareTo(cursorId) > 0));
                }

                var page = await pageQuery
                    .OrderBy(media => media.CreatedAt)
                    .ThenBy(media => media.Id)
                    .Select(media => new MediaRecord
                    {
                        Id = media.Id,
                        StorageKey = media.StorageKey,
                        SizeBytes = media.SizeBytes,
                        CreatedAt = media.CreatedAt
                    })
                    .Take(take)
                    .ToListAsync(stoppingToken);
                if (page.Count == 0)
                    break;

                processedInScope += page.Count;
                runLimiter.Consume(page.Count);
                var last = page[^1];
                cursorCreatedAt = last.CreatedAt;
                cursorId = last.Id;
                var hasMore = processedInScope < totalEligibleCount;
                var candidates = processedRecordIds == null
                    ? page
                    : page.Where(media => !processedRecordIds.Contains(media.Id)).ToList();

                if (candidates.Count > 0)
                {
                    var pageResult = await deletionEngine.DeleteAsync(
                        new MediaDeletionRequest(
                            candidates,
                            operation,
                            groupId,
                            processedRecordIds,
                            Purge: purge,
                            TotalEligibleCount: totalEligibleCount,
                            TotalEligibleBytes: totalEligibleBytes,
                            IsFinalPage: !hasMore),
                        stoppingToken);
                    result = result.Combine(pageResult);

                    if (pageResult.BudgetExhausted ||
                        pageResult.StatusOverride?.StartsWith(
                            "Skipped",
                            StringComparison.OrdinalIgnoreCase) == true ||
                        pageResult.StatusOverride?.StartsWith(
                            "Blocked",
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return result;
                    }
                }

                if (runLimiter.IsExhausted && hasMore)
                {
                    runLimiter.MarkTruncated();
                    return result with { StatusOverride = runLimiter.Status };
                }
            }

            return result;
        }

        private static async Task<MediaRetentionPolicy?> GetDefaultPolicyAsync(
            IConfigurationDbContext context,
            CancellationToken stoppingToken)
        {
            return await context.MediaRetentionPolicies
                .FirstOrDefaultAsync(p => p.IsDefault && p.IsActive, stoppingToken);
        }

        /// <summary>
        /// Resolves the simple retention override once for the entire scheduled run.
        /// Returns null if the status service is unavailable or policy-based retention is active.
        /// </summary>
        private async Task<int?> ResolveSimpleRetentionOverrideAsync(
            IMediaCleanupStatusService? statusService,
            CancellationToken stoppingToken)
        {
            try
            {
                if (statusService == null)
                {
                    return null;
                }
                return await statusService.GetSimpleRetentionOverrideAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking simple retention override, using policy-based retention");
                return null;
            }
        }

        /// <inheritdoc />
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Media cleanup service received stop signal on instance {InstanceId}",
                _instanceId);

            await base.StopAsync(cancellationToken);
        }

        private sealed class CleanupRunLimiter
        {
            public CleanupRunLimiter(int maxRecords)
            {
                MaxRecords = Math.Max(1, maxRecords);
            }

            public int MaxRecords { get; }
            public int ProcessedRecords { get; private set; }
            public int Remaining => Math.Max(0, MaxRecords - ProcessedRecords);
            public bool IsExhausted => Remaining == 0;
            public bool WasTruncated { get; private set; }
            public string Status =>
                $"Partial: record cap reached after {ProcessedRecords:N0}/{MaxRecords:N0} records";

            public void Consume(int count)
            {
                ProcessedRecords = Math.Min(
                    MaxRecords,
                    ProcessedRecords + Math.Max(0, count));
            }

            public void MarkTruncated() => WasTruncated = true;
        }
    }
}
