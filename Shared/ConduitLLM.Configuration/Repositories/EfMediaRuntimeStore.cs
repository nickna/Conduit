using System.Diagnostics.CodeAnalysis;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// EF reference adapter for the Gateway media runtime contract.
/// </summary>
public sealed class EfMediaRuntimeStore : IMediaRuntimeStore
{
    private readonly IMediaRecordRepository _repository;
    private readonly IConfigurationDbContext _context;

    public EfMediaRuntimeStore(
        IMediaRecordRepository repository,
        IConfigurationDbContext context)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<Guid> CreateAsync(
        MediaRuntimeRecord media,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        var existing = await GetByStorageKeyAsync(
            media.StorageKey,
            includeDeleted: true,
            cancellationToken);
        if (existing is not null)
            return ApplyExistingIdentity(media, existing);

        var entity = ToEntity(media);
        Guid id;
        try
        {
            id = await _repository.CreateAsync(entity, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Media completion is delivered through the durable event bus as well as
            // the synchronous response path. A concurrent winner is a valid replay.
            existing = await GetByStorageKeyAsync(
                media.StorageKey,
                includeDeleted: true,
                cancellationToken);
            if (existing is null)
                throw;
            return ApplyExistingIdentity(media, existing);
        }
        media.Id = entity.Id;
        media.CreatedAt = entity.CreatedAt;
        return id;
    }

    private static Guid ApplyExistingIdentity(
        MediaRuntimeRecord requested,
        MediaRuntimeRecord existing)
    {
        if (existing.VirtualKeyId != requested.VirtualKeyId)
        {
            throw new InvalidOperationException(
                $"Storage key '{requested.StorageKey}' already belongs to a different virtual key.");
        }

        requested.Id = existing.Id;
        requested.CreatedAt = existing.CreatedAt;
        return existing.Id;
    }

    public async Task<MediaRuntimeRecord?> GetByStorageKeyAsync(
        string storageKey,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return null;
        var query = _context.MediaRecords.AsNoTracking();
        if (includeDeleted)
            query = query.IgnoreQueryFilters();
        var media = await query.FirstOrDefaultAsync(
            record => record.StorageKey == storageKey,
            cancellationToken);
        return media is null ? null : ToRecord(media);
    }

    public Task<bool> UpdateAccessStatsAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        _repository.UpdateAccessStatsAsync(id, cancellationToken);

    public async Task<IReadOnlyList<MediaRuntimeRecord>> GetByVirtualKeyIdAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default) =>
        (await _repository.GetByVirtualKeyIdAsync(
            virtualKeyId,
            includeDeleted: false,
            cancellationToken))
        .Select(ToRecord)
        .ToArray();

    public async Task<MediaRuntimeStorageAggregate> GetAggregateStorageStatsAsync(
        int? virtualKeyGroupId = null,
        int virtualKeyLimit = 100,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await _repository.GetAggregateStorageStatsAsync(
            virtualKeyGroupId,
            virtualKeyLimit,
            cancellationToken);
        return new MediaRuntimeStorageAggregate
        {
            TotalFiles = aggregate.TotalFiles,
            TotalSizeBytes = aggregate.TotalSizeBytes,
            ByProvider = aggregate.ByProvider,
            ByMediaType = aggregate.ByMediaType
                .Select(row => new MediaRuntimeTypeAggregate(
                    row.MediaType,
                    row.FileCount,
                    row.SizeBytes))
                .ToArray(),
            TopVirtualKeys = aggregate.TopVirtualKeys
                .Select(row => new MediaRuntimeVirtualKeyAggregate(
                    row.VirtualKeyId,
                    row.SizeBytes))
                .ToArray()
        };
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Native Gateway replaces this EF reference adapter with NpgsqlMediaRuntimeStore; native Admin EF query paths remain explicitly outside the supported data-plane contract.")]
    public async Task<MediaRuntimeQuotaSnapshot?> GetQuotaSnapshotAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default)
    {
        var assignment = await _context.VirtualKeys
            .AsNoTracking()
            .Where(key => key.Id == virtualKeyId)
            .Select(key => new
            {
                GroupId = key.VirtualKeyGroupId,
                PolicyId = key.VirtualKeyGroup.MediaRetentionPolicyId
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (assignment is null)
            return null;

        var policy = await ResolvePolicyAsync(assignment.PolicyId, cancellationToken);
        var usage = await QueryUsageAsync(assignment.GroupId, cancellationToken);
        return new MediaRuntimeQuotaSnapshot(
            assignment.GroupId,
            usage.TotalSizeBytes,
            usage.TotalFiles,
            policy?.MaxStorageSizeBytes,
            policy?.MaxFileCount,
            (int)(policy?.QuotaExceededBehavior ?? MediaQuotaExceededBehavior.Reject));
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Native Gateway replaces this EF reference adapter with NpgsqlMediaRuntimeStore; native Admin EF query paths remain explicitly outside the supported data-plane contract.")]
    public async Task<IReadOnlyList<MediaRuntimeGroupQuotaUsage>> GetGroupQuotaUsagesAsync(
        int? virtualKeyGroupId = null,
        CancellationToken cancellationToken = default)
    {
        var defaultPolicy = await _context.MediaRetentionPolicies
            .AsNoTracking()
            .Where(policy => policy.IsDefault && policy.IsActive)
            .OrderBy(policy => policy.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var groupsQuery = _context.VirtualKeyGroups.AsNoTracking();
        if (virtualKeyGroupId.HasValue)
            groupsQuery = groupsQuery.Where(group => group.Id == virtualKeyGroupId.Value);

        var groups = await groupsQuery
            .Select(group => new
            {
                group.Id,
                group.GroupName,
                group.MediaRetentionPolicyId
            })
            .ToListAsync(cancellationToken);
        var assignedPolicyIds = groups
            .Where(group => group.MediaRetentionPolicyId.HasValue)
            .Select(group => group.MediaRetentionPolicyId!.Value)
            .Distinct()
            .ToList();
        var assignedPolicies = await _context.MediaRetentionPolicies
            .AsNoTracking()
            .Where(policy => assignedPolicyIds.Contains(policy.Id) && policy.IsActive)
            .ToDictionaryAsync(policy => policy.Id, cancellationToken);
        var groupIds = groups.Select(group => group.Id).ToList();
        var usageRows = await _context.MediaRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Join(
                _context.VirtualKeys.AsNoTracking(),
                media => media.VirtualKeyId,
                key => key.Id,
                (media, key) => new
                {
                    key.VirtualKeyGroupId,
                    SizeBytes = media.SizeBytes ?? 0
                })
            .Where(item => groupIds.Contains(item.VirtualKeyGroupId))
            .GroupBy(item => item.VirtualKeyGroupId)
            .Select(group => new
            {
                GroupId = group.Key,
                TotalFiles = group.Count(),
                TotalSizeBytes = group.Sum(item => item.SizeBytes)
            })
            .ToDictionaryAsync(row => row.GroupId, cancellationToken);

        return groups.Select(group =>
        {
            MediaRetentionPolicy? policy = null;
            if (group.MediaRetentionPolicyId.HasValue)
                assignedPolicies.TryGetValue(group.MediaRetentionPolicyId.Value, out policy);
            policy ??= defaultPolicy;
            usageRows.TryGetValue(group.Id, out var usage);
            return new MediaRuntimeGroupQuotaUsage(
                group.Id,
                group.GroupName,
                policy?.Id,
                policy?.Name,
                usage?.TotalSizeBytes ?? 0,
                usage?.TotalFiles ?? 0,
                policy?.MaxStorageSizeBytes,
                policy?.MaxFileCount,
                (int)(policy?.QuotaExceededBehavior ?? MediaQuotaExceededBehavior.Reject),
                policy?.RespectRecentAccess ?? true,
                policy?.RecentAccessWindowDays ?? 7);
        }).ToArray();
    }

    private async Task<MediaRetentionPolicy?> ResolvePolicyAsync(
        int? assignedPolicyId,
        CancellationToken cancellationToken)
    {
        if (assignedPolicyId.HasValue)
        {
            var assigned = await _context.MediaRetentionPolicies
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    policy => policy.Id == assignedPolicyId.Value && policy.IsActive,
                    cancellationToken);
            if (assigned is not null)
                return assigned;
        }

        return await _context.MediaRetentionPolicies
            .AsNoTracking()
            .Where(policy => policy.IsDefault && policy.IsActive)
            .OrderBy(policy => policy.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Native Gateway replaces this EF reference adapter with NpgsqlMediaRuntimeStore; native Admin EF query paths remain explicitly outside the supported data-plane contract.")]
    private async Task<(int TotalFiles, long TotalSizeBytes)> QueryUsageAsync(
        int groupId,
        CancellationToken cancellationToken)
    {
        var row = await _context.MediaRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Join(
                _context.VirtualKeys.AsNoTracking().Where(key => key.VirtualKeyGroupId == groupId),
                media => media.VirtualKeyId,
                key => key.Id,
                (media, _) => new { SizeBytes = media.SizeBytes ?? 0 })
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalFiles = group.Count(),
                TotalSizeBytes = group.Sum(item => item.SizeBytes)
            })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? (0, 0) : (row.TotalFiles, row.TotalSizeBytes);
    }

    private static MediaRuntimeRecord ToRecord(MediaRecord media) => new()
    {
        Id = media.Id,
        StorageKey = media.StorageKey,
        VirtualKeyId = media.VirtualKeyId,
        MediaType = media.MediaType,
        ContentType = media.ContentType,
        SizeBytes = media.SizeBytes,
        ContentHash = media.ContentHash,
        Provider = media.Provider,
        Model = media.Model,
        Prompt = media.Prompt,
        StorageUrl = media.StorageUrl,
        PublicUrl = media.PublicUrl,
        ExpiresAt = media.ExpiresAt,
        CreatedAt = media.CreatedAt,
        LastAccessedAt = media.LastAccessedAt,
        AccessCount = media.AccessCount,
        DeletedAt = media.DeletedAt
    };

    private static MediaRecord ToEntity(MediaRuntimeRecord media) => new()
    {
        Id = media.Id,
        StorageKey = media.StorageKey,
        VirtualKeyId = media.VirtualKeyId,
        MediaType = media.MediaType,
        ContentType = media.ContentType,
        SizeBytes = media.SizeBytes,
        ContentHash = media.ContentHash,
        Provider = media.Provider,
        Model = media.Model,
        Prompt = media.Prompt,
        StorageUrl = media.StorageUrl,
        PublicUrl = media.PublicUrl,
        ExpiresAt = media.ExpiresAt,
        CreatedAt = media.CreatedAt,
        LastAccessedAt = media.LastAccessedAt,
        AccessCount = media.AccessCount,
        DeletedAt = media.DeletedAt
    };
}
