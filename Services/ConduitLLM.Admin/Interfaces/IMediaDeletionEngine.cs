using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Admin.Interfaces;

/// <summary>
/// The single guarded implementation for deleting tracked media.
/// </summary>
public interface IMediaDeletionEngine
{
    /// <summary>
    /// Executes one named cleanup operation and records its status and metrics.
    /// </summary>
    Task<MediaDeletionEngineResult> ExecuteOperationAsync(
        MediaDeletionOperationContext operation,
        Func<Task<MediaDeletionEngineResult>> action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes candidates using the configured dry-run, budget, and batching controls.
    /// Callers must hold <see cref="MediaCleanupLock.Key"/> for the whole run.
    /// </summary>
    Task<MediaDeletionEngineResult> DeleteAsync(
        MediaDeletionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculates the count and bytes a scoped cleanup would affect.
    /// </summary>
    MediaDeletionPreview Preview(IEnumerable<MediaRecord> mediaRecords);
}

/// <summary>
/// Shared lock identity for every media-cleanup trigger.
/// </summary>
public static class MediaCleanupLock
{
    public const string Key = "media:cleanup:leader";
    public static readonly TimeSpan OperationDeadline = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Identifies one cleanup operation for status, metrics, and audit correlation.
/// </summary>
public sealed record MediaDeletionOperationContext(
    string CleanupType,
    string TriggeredBy,
    string LeaderInstanceId,
    bool Force = false);

/// <summary>
/// Candidates and scope supplied to the deletion engine.
/// </summary>
public sealed record MediaDeletionRequest(
    IReadOnlyCollection<MediaRecord> MediaRecords,
    MediaDeletionOperationContext Operation,
    int? GroupId = null,
    ISet<Guid>? ProcessedRecordIds = null,
    string? StatusOverride = null,
    IReadOnlyCollection<MediaStorageObject>? UntrackedStorageObjects = null,
    bool Purge = false,
    int? TotalEligibleCount = null,
    long? TotalEligibleBytes = null,
    bool IsFinalPage = true);

/// <summary>
/// Result of a guarded deletion operation.
/// </summary>
public sealed record MediaDeletionEngineResult(
    int FilesDeleted = 0,
    long BytesFreed = 0,
    int Failures = 0,
    bool BudgetExhausted = false,
    string? StatusOverride = null,
    int WouldDeleteCount = 0,
    long BytesWouldFree = 0,
    bool IsDryRun = false,
    string? OperationStatus = null,
    double DurationSeconds = 0,
    int RecordsTombstoned = 0,
    int WouldTombstoneCount = 0)
{
    public static MediaDeletionEngineResult Empty { get; } = new();

    public MediaDeletionEngineResult Combine(MediaDeletionEngineResult other) => new(
        FilesDeleted: FilesDeleted + other.FilesDeleted,
        BytesFreed: BytesFreed + other.BytesFreed,
        Failures: Failures + other.Failures,
        BudgetExhausted: BudgetExhausted || other.BudgetExhausted,
        StatusOverride: StatusOverride ?? other.StatusOverride,
        WouldDeleteCount: WouldDeleteCount + other.WouldDeleteCount,
        BytesWouldFree: BytesWouldFree + other.BytesWouldFree,
        IsDryRun: IsDryRun || other.IsDryRun,
        OperationStatus: OperationStatus ?? other.OperationStatus,
        DurationSeconds: DurationSeconds + other.DurationSeconds,
        RecordsTombstoned: RecordsTombstoned + other.RecordsTombstoned,
        WouldTombstoneCount: WouldTombstoneCount + other.WouldTombstoneCount);
}

/// <summary>
/// Non-mutating estimate for a scoped cleanup operation.
/// </summary>
public sealed record MediaDeletionPreview(int FileCount, long SizeBytes);
