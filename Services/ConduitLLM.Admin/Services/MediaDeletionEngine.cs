using System.Diagnostics;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Metrics;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Admin.Services;

/// <summary>
/// Applies the common safety controls to every tracked-media deletion.
/// </summary>
public sealed class MediaDeletionEngine : IMediaDeletionEngine
{
    private readonly IMediaStorageService _storageService;
    private readonly IMediaDeletionBudgetService _budgetService;
    private readonly IMediaRecordRepository _mediaRepository;
    private readonly IMediaCleanupStatusService _statusService;
    private readonly IMediaCleanupApprovalService _approvalService;
    private readonly IMediaStorageConfigurationGuard _storageGuard;
    private readonly IEventBus? _eventBus;
    private readonly MediaLifecycleOptions _options;
    private readonly ILogger<MediaDeletionEngine> _logger;

    public MediaDeletionEngine(
        IMediaStorageService storageService,
        IMediaDeletionBudgetService budgetService,
        IMediaRecordRepository mediaRepository,
        IMediaCleanupStatusService statusService,
        IMediaCleanupApprovalService approvalService,
        IMediaStorageConfigurationGuard storageGuard,
        IOptions<MediaLifecycleOptions> options,
        ILogger<MediaDeletionEngine> logger,
        IEventBus? eventBus = null)
    {
        _storageService = storageService;
        _budgetService = budgetService;
        _mediaRepository = mediaRepository;
        _statusService = statusService;
        _approvalService = approvalService;
        _storageGuard = storageGuard;
        _eventBus = eventBus;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MediaDeletionEngineResult> ExecuteOperationAsync(
        MediaDeletionOperationContext operation,
        Func<Task<MediaDeletionEngineResult>> action,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = MediaDeletionEngineResult.Empty;
        var operationStatus = "Completed";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await action();
            cancellationToken.ThrowIfCancellationRequested();
            operationStatus = GetOperationStatus(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationStatus = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            operationStatus = $"Failed: {ex.Message}";
            result = new MediaDeletionEngineResult(Failures: 1);
            _logger.LogError(
                ex,
                "{CleanupType} media cleanup triggered by {TriggeredBy} failed",
                operation.CleanupType, operation.TriggeredBy);
            AdminMediaCleanupMetrics.CleanupErrors
                .WithLabels(operation.CleanupType, "operation")
                .Inc();
        }
        finally
        {
            stopwatch.Stop();
            using var completionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var completionToken = cancellationToken.IsCancellationRequested
                ? completionCancellation.Token : cancellationToken;
            await _statusService.RecordOperationCompletionAsync(
                operation.CleanupType,
                result.FilesDeleted,
                result.BytesFreed,
                stopwatch.Elapsed.TotalSeconds,
                operationStatus,
                operation.LeaderInstanceId,
                operation.TriggeredBy,
                completionToken);

            RecordMetrics(operation, result, operationStatus, stopwatch.Elapsed);
            if (!cancellationToken.IsCancellationRequested)
            {
                await PublishOperationalAlertsAsync(operation, result, operationStatus, cancellationToken);
            }
        }

        return result with
        {
            OperationStatus = operationStatus,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds
        };
    }

    /// <inheritdoc />
    public async Task<MediaDeletionEngineResult> DeleteAsync(
        MediaDeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processedRecordIds = request.ProcessedRecordIds;
        var trackedCandidates = request.MediaRecords
            .Where(record => processedRecordIds == null || processedRecordIds.Add(record.Id))
            .GroupBy(record => record.Id)
            .Select(group => group.First())
            .Select(record => new DeletionCandidate(
                record.StorageKey,
                record.SizeBytes ?? 0,
                record.Id,
                record.CreatedAt))
            .ToList();
        var trackedStorageKeys = trackedCandidates
            .Select(candidate => candidate.StorageKey)
            .ToHashSet(StringComparer.Ordinal);
        var untrackedCandidates = (request.UntrackedStorageObjects ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.StorageKey))
            .Where(item => !trackedStorageKeys.Contains(item.StorageKey))
            .GroupBy(item => item.StorageKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(item => new DeletionCandidate(
                item.StorageKey,
                item.SizeBytes,
                MediaRecordId: null,
                item.LastModifiedUtc));
        var candidates = trackedCandidates
            .Concat(untrackedCandidates)
            .ToList();

        var isDryRun = _options.DryRunMode && !request.Operation.Force;
        var approvalEligible =
            _options.RequireManualApprovalForLargeBatches &&
            request.Operation.TriggeredBy == "scheduled" &&
            request.Operation.CleanupType != MediaCleanupTypes.VirtualKey &&
            !request.Operation.Force;
        var activeApproval = approvalEligible
            ? await _approvalService.GetActiveApprovalAsync(
                request.Operation.CleanupType,
                request.GroupId,
                cancellationToken)
            : null;
        if (activeApproval != null)
        {
            candidates = candidates
                .Where(candidate => candidate.ScopeTimestampUtc <= activeApproval.CutoffUtc)
                .ToList();
        }

        if (candidates.Count == 0 || !string.IsNullOrWhiteSpace(request.StatusOverride))
        {
            if (candidates.Count == 0 &&
                activeApproval != null &&
                request.IsFinalPage)
            {
                await _approvalService.CompleteEmptyApprovalAsync(
                    activeApproval.Id,
                    cancellationToken);
            }

            return new MediaDeletionEngineResult(
                StatusOverride: request.StatusOverride,
                IsDryRun: isDryRun);
        }

        if (candidates.Any(candidate => !ShouldTombstone(candidate, request)) &&
            !await _storageGuard.ValidateAsync(cancellationToken))
        {
            return new MediaDeletionEngineResult(
                Failures: candidates.Count,
                StatusOverride: "Blocked: unsafe storage configuration");
        }

        var totalEligibleCount = request.TotalEligibleCount ?? candidates.Count;
        var totalEligibleBytes = request.TotalEligibleBytes ??
            candidates.Sum(candidate => candidate.SizeBytes);
        if (approvalEligible &&
            totalEligibleCount > _options.LargeBatchThreshold &&
            activeApproval == null)
        {
            await _approvalService.CreateOrRefreshPendingAsync(
                request.Operation.CleanupType,
                request.GroupId,
                totalEligibleCount,
                totalEligibleBytes,
                DateTime.UtcNow,
                cancellationToken);
            _logger.LogWarning(
                "{CleanupType} cleanup batch of {Count} files exceeds threshold of {Threshold}. Manual approval required.",
                request.Operation.CleanupType,
                totalEligibleCount,
                _options.LargeBatchThreshold);
            if (!isDryRun)
            {
                return new MediaDeletionEngineResult(
                    StatusOverride: "Skipped: pending manual approval",
                    IsDryRun: false);
            }
        }

        if (request.Operation.Force && _options.DryRunMode)
        {
            _logger.LogWarning(
                "Dry-run override requested for {CleanupType} cleanup by {TriggeredBy}",
                request.Operation.CleanupType,
                request.Operation.TriggeredBy);
        }

        var result = MediaDeletionEngineResult.Empty with { IsDryRun = isDryRun };
        var batchSize = Math.Clamp(_options.MaxBatchSize, 1, 1000);
        var reservationStride = Math.Max(
            1,
            Math.Min(batchSize, _options.BudgetReservationStride));
        var permanentBudgetExhausted = false;
        foreach (var batch in candidates.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var reservationBatch in batch.Chunk(reservationStride))
            {
                var permanentDeleteCount = reservationBatch.Count(candidate =>
                    !ShouldTombstone(candidate, request));
                var grantedPermanentDeletes = permanentDeleteCount;
                if (!isDryRun && permanentDeleteCount > 0)
                {
                    var reservation = permanentBudgetExhausted
                        ? new MediaDeletionBudgetReservation(
                            permanentDeleteCount,
                            0,
                            _options.MonthlyDeleteBudget)
                        : await _budgetService.ReserveAsync(
                            permanentDeleteCount,
                            _options.MonthlyDeleteBudget,
                            cancellationToken);
                    grantedPermanentDeletes = reservation.Granted;
                    if (reservation.StoreFailed)
                    {
                        _logger.LogWarning(
                            "Media delete budget store failed during {CleanupType}; applied {FailureMode}",
                            request.Operation.CleanupType,
                            reservation.FailureMode);
                    }

                    if (grantedPermanentDeletes < permanentDeleteCount)
                    {
                        permanentBudgetExhausted = true;
                        result = result with { BudgetExhausted = true };
                        _logger.LogWarning(
                            "Monthly media-delete budget granted {Granted} of {Requested} deletes for this stride",
                            grantedPermanentDeletes,
                            permanentDeleteCount);
                    }
                }

                var permittedBatch = SelectPermittedCandidates(
                    reservationBatch,
                    request,
                    grantedPermanentDeletes);
                if (permittedBatch.Length == 0)
                {
                    continue;
                }

                var batchResult = await ProcessBatchAsync(
                    permittedBatch,
                    request,
                    isDryRun,
                    cancellationToken);
                result = result.Combine(batchResult);
            }

            if (_options.DelayBetweenBatchesMs > 0)
            {
                await Task.Delay(_options.DelayBetweenBatchesMs, cancellationToken);
            }
        }

        if (activeApproval != null)
        {
            await _approvalService.RecordExecutionAsync(
                activeApproval.Id,
                result,
                request.IsFinalPage,
                cancellationToken);
        }

        return result;
    }

    private DeletionCandidate[] SelectPermittedCandidates(
        DeletionCandidate[] candidates,
        MediaDeletionRequest request,
        int permanentDeleteAllowance)
    {
        var remainingPermanentDeletes = permanentDeleteAllowance;
        return candidates
            .Where(candidate =>
            {
                if (ShouldTombstone(candidate, request))
                {
                    return true;
                }

                if (remainingPermanentDeletes <= 0)
                {
                    return false;
                }

                remainingPermanentDeletes--;
                return true;
            })
            .ToArray();
    }

    /// <inheritdoc />
    public MediaDeletionPreview Preview(IEnumerable<MediaRecord> mediaRecords)
    {
        var records = mediaRecords
            .GroupBy(record => record.Id)
            .Select(group => group.First())
            .ToList();
        return new MediaDeletionPreview(
            records.Count,
            records.Sum(record => record.SizeBytes ?? 0));
    }

    private async Task<MediaDeletionEngineResult> ProcessBatchAsync(
        DeletionCandidate[] batch,
        MediaDeletionRequest request,
        bool isDryRun,
        CancellationToken cancellationToken)
    {
        var filesDeleted = 0;
        long bytesFreed = 0;
        var failures = 0;
        var wouldDelete = 0;
        long bytesWouldFree = 0;
        var recordsTombstoned = 0;
        var wouldTombstone = 0;

        if (isDryRun)
        {
            foreach (var candidate in batch)
            {
                if (ShouldTombstone(candidate, request))
                {
                    wouldTombstone++;
                }
                else
                {
                    wouldDelete++;
                    bytesWouldFree += candidate.SizeBytes;
                }
            }
        }
        else
        {
            foreach (var candidate in batch.Where(candidate =>
                ShouldTombstone(candidate, request)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!await _mediaRepository.TombstoneAsync(
                            candidate.MediaRecordId!.Value,
                            DateTime.UtcNow,
                            cancellationToken))
                    {
                        failures++;
                        continue;
                    }

                    recordsTombstoned++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures++;
                    _logger.LogError(
                        ex,
                        "Failed to delete media {StorageKey}",
                        candidate.StorageKey);
                }
            }

            var permanentCandidates = batch
                .Where(candidate => !ShouldTombstone(candidate, request))
                .ToArray();
            if (permanentCandidates.Length > 0)
            {
                var permanentResult = await DeletePermanentBatchAsync(
                    permanentCandidates,
                    cancellationToken);
                filesDeleted += permanentResult.FilesDeleted;
                bytesFreed += permanentResult.BytesFreed;
                failures += permanentResult.Failures;
            }
        }

        if (filesDeleted > 0)
        {
            _logger.LogInformation(
                "{CleanupType} cleanup triggered by {TriggeredBy} deleted {Count} files for group {GroupId}",
                request.Operation.CleanupType,
                request.Operation.TriggeredBy,
                filesDeleted,
                request.GroupId);
        }

        if (failures > 0)
        {
            AdminMediaCleanupMetrics.CleanupErrors
                .WithLabels(request.Operation.CleanupType, "storage")
                .Inc(failures);
        }

        return new MediaDeletionEngineResult(
            FilesDeleted: filesDeleted,
            BytesFreed: bytesFreed,
            Failures: failures,
            WouldDeleteCount: wouldDelete,
            BytesWouldFree: bytesWouldFree,
            IsDryRun: isDryRun,
            RecordsTombstoned: recordsTombstoned,
            WouldTombstoneCount: wouldTombstone);
    }

    private async Task<(int FilesDeleted, long BytesFreed, int Failures)>
        DeletePermanentBatchAsync(
        DeletionCandidate[] candidates,
        CancellationToken cancellationToken)
    {
        var pending = candidates.ToDictionary(
            candidate => candidate.StorageKey,
            StringComparer.Ordinal);
        var confirmed = new List<DeletionCandidate>(candidates.Length);
        var failures = 0;
        var maxRetries = Math.Max(0, _options.DeleteThrottleMaxRetries);

        for (var attempt = 0; pending.Count > 0; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaBulkDeleteResult storageResult;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                Math.Max(1, _options.R2OperationTimeoutSeconds)));
            try
            {
                storageResult = await _storageService.DeleteManyAsync(
                    pending.Keys.ToArray(),
                    timeout.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                timeout.IsCancellationRequested)
            {
                storageResult = new MediaBulkDeleteResult
                {
                    Items = pending.Keys.Select(key => new MediaDeleteItemResult
                    {
                        StorageKey = key,
                        IsRetryable = true,
                        ErrorCode = "timeout",
                        ErrorMessage = "Storage bulk delete timed out"
                    }).ToList()
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk media deletion failed");
                failures += pending.Count;
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var retryableKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in storageResult.Items)
            {
                if (!pending.TryGetValue(item.StorageKey, out var candidate))
                    continue;

                if (item.Deleted)
                {
                    confirmed.Add(candidate);
                    pending.Remove(item.StorageKey);
                }
                else if (item.IsRetryable)
                {
                    retryableKeys.Add(item.StorageKey);
                }
                else
                {
                    failures++;
                    pending.Remove(item.StorageKey);
                    _logger.LogWarning(
                        "Storage failed to delete {StorageKey}: {ErrorCode} {ErrorMessage}",
                        item.StorageKey,
                        item.ErrorCode,
                        item.ErrorMessage);
                }
            }

            foreach (var unreportedKey in pending.Keys
                .Where(key => !retryableKeys.Contains(key))
                .ToList())
            {
                failures++;
                pending.Remove(unreportedKey);
                _logger.LogWarning(
                    "Storage returned no deletion outcome for {StorageKey}",
                    unreportedKey);
            }

            if (pending.Count == 0)
                break;

            if (attempt >= maxRetries)
            {
                failures += pending.Count;
                _logger.LogError(
                    "Storage throttling persisted after {Attempts} bulk delete attempts; {Count} objects remain",
                    attempt + 1,
                    pending.Count);
                break;
            }

            var delayMs = Math.Min(
                300_000d,
                Math.Max(0, _options.DeleteThrottleInitialBackoffMs) *
                Math.Pow(2, attempt));
            _logger.LogWarning(
                "Storage throttled or timed out bulk deletion; retrying {Count} objects in {DelayMs} ms (attempt {Attempt}/{MaxAttempts})",
                pending.Count,
                delayMs,
                attempt + 2,
                maxRetries + 1);
            if (delayMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
        }

        var bytesFreed = 0L;
        foreach (var candidate in confirmed)
        {
            bytesFreed += candidate.SizeBytes;
            if (candidate.MediaRecordId.HasValue &&
                !await _mediaRepository.HardDeleteAsync(
                    candidate.MediaRecordId.Value,
                    cancellationToken))
            {
                failures++;
                _logger.LogWarning(
                    "Storage object {StorageKey} was deleted but media record {MediaId} could not be removed",
                    candidate.StorageKey,
                    candidate.MediaRecordId);
            }
        }

        return (confirmed.Count, bytesFreed, failures);
    }

    private bool ShouldTombstone(
        DeletionCandidate candidate,
        MediaDeletionRequest request) =>
        _options.EnableSoftDelete &&
        !request.Purge &&
        request.Operation.CleanupType != MediaCleanupTypes.VirtualKey &&
        candidate.MediaRecordId.HasValue;

    private string GetOperationStatus(MediaDeletionEngineResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StatusOverride))
        {
            return result.StatusOverride;
        }

        if (result.BudgetExhausted)
        {
            return result.Failures > 0
                ? "Partial: deletion budget exhausted with errors"
                : "Partial: deletion budget exhausted";
        }

        if (result.Failures > 0)
        {
            return "Completed with errors";
        }

        return result.IsDryRun ? "Dry run completed" : "Completed";
    }

    private static void RecordMetrics(
        MediaDeletionOperationContext operation,
        MediaDeletionEngineResult result,
        string operationStatus,
        TimeSpan duration)
    {
        var metricStatus = operationStatus.StartsWith("Failed", StringComparison.Ordinal)
            ? "failed"
            : operationStatus == "Cancelled"
                ? "cancelled"
                : operationStatus.Contains("errors", StringComparison.OrdinalIgnoreCase)
                    ? "partial"
                    : operationStatus.Contains("budget", StringComparison.OrdinalIgnoreCase)
                        ? "budget_exhausted"
                        : operationStatus.StartsWith("Skipped", StringComparison.Ordinal)
                            ? "skipped"
                            : result.IsDryRun
                                ? "dry_run"
                                : "completed";

        AdminMediaCleanupMetrics.CleanupCycles
            .WithLabels(operation.CleanupType, metricStatus)
            .Inc();
        AdminMediaCleanupMetrics.CleanupRuns
            .WithLabels(operation.CleanupType, operation.TriggeredBy, metricStatus)
            .Inc();
        AdminMediaCleanupMetrics.CleanupDuration
            .WithLabels(operation.CleanupType)
            .Observe(duration.TotalSeconds);
        AdminMediaCleanupMetrics.LastRunTimestamp
            .WithLabels(operation.CleanupType)
            .Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        AdminMediaCleanupMetrics.LastRunSucceeded
            .WithLabels(operation.CleanupType)
            .Set(metricStatus is "completed" or "dry_run" ? 1 : 0);
        if (result.IsDryRun)
        {
            if (result.WouldDeleteCount > 0)
            {
                AdminMediaCleanupMetrics.DryRunFilesMatched
                    .WithLabels(operation.CleanupType)
                    .Inc(result.WouldDeleteCount);
            }
            if (result.BytesWouldFree > 0)
            {
                AdminMediaCleanupMetrics.DryRunBytesMatched
                    .WithLabels(operation.CleanupType)
                    .Inc(result.BytesWouldFree);
            }
            if (result.WouldTombstoneCount > 0)
            {
                AdminMediaCleanupMetrics.DryRunRecordsMatched
                    .WithLabels(operation.CleanupType)
                    .Inc(result.WouldTombstoneCount);
            }
        }
        else if (result.FilesDeleted > 0)
        {
            AdminMediaCleanupMetrics.FilesDeleted
                .WithLabels(operation.CleanupType)
                .Inc(result.FilesDeleted);
        }
        if (!result.IsDryRun && result.BytesFreed > 0)
        {
            AdminMediaCleanupMetrics.BytesFreed
                .WithLabels(operation.CleanupType)
                .Inc(result.BytesFreed);
        }
        if (!result.IsDryRun && result.RecordsTombstoned > 0)
        {
            AdminMediaCleanupMetrics.RecordsTombstoned
                .WithLabels(operation.CleanupType)
                .Inc(result.RecordsTombstoned);
        }
    }

    private async Task PublishOperationalAlertsAsync(
        MediaDeletionOperationContext operation,
        MediaDeletionEngineResult result,
        string operationStatus,
        CancellationToken cancellationToken)
    {
        if (_eventBus == null)
            return;

        try
        {
            if (result.Failures > 0 ||
                operationStatus.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
            {
                await _eventBus.PublishAsync(new MediaCleanupAlertRaised
                {
                    Kind = MediaCleanupAlertKind.OperationFailure,
                    CleanupType = operation.CleanupType,
                    Status = operationStatus,
                    TriggeredBy = operation.TriggeredBy,
                    LeaderInstanceId = operation.LeaderInstanceId
                }, cancellationToken);
            }

            if (_options.MonthlyDeleteBudget <= 0)
                return;

            var monthlyDeleteCount =
                await _budgetService.GetMonthlyDeleteCountAsync(cancellationToken);
            var budgetUsedPercent =
                (double)monthlyDeleteCount / _options.MonthlyDeleteBudget * 100;
            var alertThreshold = Math.Clamp(
                _options.BudgetAlertThresholdPercent,
                0,
                100);
            if (budgetUsedPercent < alertThreshold)
                return;

            await _eventBus.PublishAsync(new MediaCleanupAlertRaised
            {
                Kind = MediaCleanupAlertKind.BudgetThreshold,
                CleanupType = operation.CleanupType,
                Status = result.BudgetExhausted
                    ? "Deletion budget exhausted"
                    : "Deletion budget threshold reached",
                TriggeredBy = operation.TriggeredBy,
                LeaderInstanceId = operation.LeaderInstanceId,
                MonthlyDeleteCount = monthlyDeleteCount,
                MonthlyDeleteBudget = _options.MonthlyDeleteBudget,
                BudgetUsedPercent = Math.Round(budgetUsedPercent, 2)
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The cleanup operation owns this token; shutdown should not be delayed for alerts.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish operational alert for {CleanupType} media cleanup",
                operation.CleanupType);
        }
    }

    private sealed record DeletionCandidate(
        string StorageKey,
        long SizeBytes,
        Guid? MediaRecordId,
        DateTime ScopeTimestampUtc);
}
