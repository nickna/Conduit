#if !CONDUIT_NATIVE_AOT
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>JIT reference for atomic scalar billing audit batches.</summary>
public sealed class EfBillingAuditRuntimeWriter : IBillingAuditRuntimeWriter
{
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfBillingAuditRuntimeWriter(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BillingAuditRuntimeRecord>> WriteBatchAsync(
        IReadOnlyList<BillingAuditRuntimeRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var snapshots = new BillingAuditRuntimeRecord[records.Count];
        for (var index = 0; index < snapshots.Length; index++)
            snapshots[index] = records[index] ??
                throw new ArgumentException("The batch cannot contain null records.", nameof(records));
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshots.Length == 0) return Array.Empty<BillingAuditRuntimeRecord>();

        var entities = new BillingAuditEvent[snapshots.Length];
        for (var index = 0; index < snapshots.Length; index++)
        {
            var row = snapshots[index];
            entities[index] = new BillingAuditEvent
            {
                Id = row.Id,
                Timestamp = row.Timestamp,
                EventType = (BillingAuditEventType)row.EventType,
                VirtualKeyId = row.VirtualKeyId,
                VirtualKeyGroupId = row.VirtualKeyGroupId,
                Model = row.Model,
                RequestId = row.RequestId,
                UsageJson = row.UsageJson,
                CalculatedCost = row.CalculatedCost,
                FailureReason = row.FailureReason,
                ProviderType = row.ProviderType,
                HttpStatusCode = row.HttpStatusCode,
                MetadataJson = row.MetadataJson,
                RequestPath = row.RequestPath,
                IsEstimated = row.IsEstimated,
                ToolUsageJson = row.ToolUsageJson,
                ToolUsageCost = row.ToolUsageCost
            };
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await context.BillingAuditEvents.AddRangeAsync(entities, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var committed = new BillingAuditRuntimeRecord[snapshots.Length];
        for (var index = 0; index < snapshots.Length; index++)
            committed[index] = snapshots[index] with
            {
                Id = snapshots[index].Id == Guid.Empty ? entities[index].Id : snapshots[index].Id,
                Timestamp = snapshots[index].Timestamp == default ? entities[index].Timestamp : snapshots[index].Timestamp
            };
        return Array.AsReadOnly(committed);
    }
}
#endif
