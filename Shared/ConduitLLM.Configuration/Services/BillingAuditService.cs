using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Services;

/// <summary>
/// Service for auditing billing events with batch writing and async processing.
/// </summary>
public class BillingAuditService : BatchAuditServiceBase<BillingAuditEvent>, IBillingAuditService
{
    private readonly IBillingAuditRuntimeWriter? _runtimeWriter;

    /// <summary>
    /// Creates a new instance of the BillingAuditService.
    /// </summary>
    /// <param name="serviceProvider">Service provider for creating scoped DbContexts</param>
    /// <param name="logger">Logger instance</param>
    public BillingAuditService(
        IServiceProvider serviceProvider,
        ILogger<BillingAuditService> logger)
        : base(serviceProvider, logger)
    {
    }

    private BillingAuditService(
        IServiceProvider serviceProvider,
        ILogger<BillingAuditService> logger,
        IBillingAuditRuntimeWriter runtimeWriter)
        : this(serviceProvider, logger)
    {
        _runtimeWriter = runtimeWriter;
    }

    /// <summary>
    /// Creates a scalar runtime audit queue with per-host flushing and without
    /// scheduled EF retention. The original constructor retains EF persistence.
    /// </summary>
    public static BillingAuditService CreateWithRuntimePersistence(
        IServiceProvider serviceProvider,
        IBillingAuditRuntimeWriter runtimeWriter,
        ILogger<BillingAuditService> logger)
    {
        ArgumentNullException.ThrowIfNull(runtimeWriter);
        return new BillingAuditService(serviceProvider, logger, runtimeWriter);
    }

    #region Template Method Implementations

    /// <inheritdoc/>
    protected override DbSet<BillingAuditEvent> GetDbSet(ConduitDbContext context)
        => context.BillingAuditEvents;

    /// <inheritdoc/>
    protected override string EntityName => "Billing";

    /// <inheritdoc />
    protected override async Task PersistEventsAsync(
        IReadOnlyList<BillingAuditEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (_runtimeWriter is null)
        {
            await base.PersistEventsAsync(events, cancellationToken);
            return;
        }

        var snapshots = new BillingAuditRuntimeRecord[events.Count];
        for (var index = 0; index < events.Count; index++)
        {
            var row = events[index];
            if (row.VirtualKey is not null)
                throw new ArgumentException("Billing audit navigation graphs are outside scalar runtime persistence.", nameof(events));
            snapshots[index] = new BillingAuditRuntimeRecord
            {
                Id = row.Id,
                Timestamp = row.Timestamp,
                EventType = (int)row.EventType,
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

        var committed = await _runtimeWriter.WriteBatchAsync(snapshots, cancellationToken);
        if (committed is null || committed.Count != snapshots.Length)
            throw new InvalidOperationException("Billing audit writer returned an invalid committed batch.");
        var committedSnapshots = new BillingAuditRuntimeRecord[snapshots.Length];
        var committedIds = new HashSet<Guid>();
        for (var index = 0; index < snapshots.Length; index++)
        {
            var supplied = snapshots[index];
            var returned = committed[index];
            if (returned is null || returned.Id == Guid.Empty || !committedIds.Add(returned.Id) ||
                (supplied.Id != Guid.Empty && returned.Id != supplied.Id) ||
                (supplied.Timestamp == default
                    ? returned.Timestamp == default || returned.Timestamp.Kind != DateTimeKind.Utc
                    : returned.Timestamp.Ticks != supplied.Timestamp.Ticks || returned.Timestamp.Kind != supplied.Timestamp.Kind) ||
                returned with { Id = supplied.Id, Timestamp = supplied.Timestamp } != supplied)
                throw new InvalidOperationException("Billing audit writer returned an invalid committed batch.");
            committedSnapshots[index] = returned;
        }

        // A malformed later result must not mutate any queued event before retry.
        for (var index = 0; index < snapshots.Length; index++)
        {
            if (snapshots[index].Id == Guid.Empty) events[index].Id = committedSnapshots[index].Id;
            if (snapshots[index].Timestamp == default) events[index].Timestamp = committedSnapshots[index].Timestamp;
        }
    }

    /// <inheritdoc />
    protected override Task ScheduleDataRetentionAsync(CancellationToken cancellationToken) =>
        _runtimeWriter is null ? base.ScheduleDataRetentionAsync(cancellationToken) : Task.CompletedTask;

    #endregion

    #region IBillingAuditService Implementation (Wrapper Methods)

    /// <inheritdoc/>
    public Task LogBillingEventAsync(BillingAuditEvent auditEvent)
        => LogEventAsync(auditEvent);

    /// <inheritdoc/>
    public void LogBillingEvent(BillingAuditEvent auditEvent)
        => LogEvent(auditEvent);

    /// <inheritdoc/>
    public Task CleanupOldAuditEventsAsync()
        => CleanupOldEventsAsync();

    #endregion

}
