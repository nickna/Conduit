using ConduitLLM.Persistence;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>
/// Fixed-shape persistence required by Gateway media storage and ownership flows.
/// </summary>
public interface IMediaRuntimeStore
{
    Task<Guid> CreateAsync(
        MediaRuntimeRecord media,
        CancellationToken cancellationToken = default);

    Task<MediaRuntimeRecord?> GetByStorageKeyAsync(
        string storageKey,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAccessStatsAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaRuntimeRecord>> GetByVirtualKeyIdAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default);

    Task<MediaRuntimeStorageAggregate> GetAggregateStorageStatsAsync(
        int? virtualKeyGroupId = null,
        int virtualKeyLimit = 100,
        CancellationToken cancellationToken = default);

    Task<MediaRuntimeQuotaSnapshot?> GetQuotaSnapshotAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaRuntimeGroupQuotaUsage>> GetGroupQuotaUsagesAsync(
        int? virtualKeyGroupId = null,
        CancellationToken cancellationToken = default);
}
