namespace ConduitLLM.Persistence.Interfaces;

/// <summary>Persists complete scalar billing audit batches without modifying input records.</summary>
public interface IBillingAuditRuntimeWriter
{
    /// <summary>
    /// Inserts the entire batch atomically and returns committed snapshots in input order.
    /// An empty ID and a default timestamp request the configured database-generated values.
    /// Only those generated values are filled in the returned copies; all supplied values,
    /// including timestamp precision, are preserved. Database storage conversions are not
    /// reloaded into other fields. Unknown event-type integers are accepted, and JSON strings
    /// are passed through to PostgreSQL without serialization.
    /// </summary>
    /// <remarks>
    /// A null batch or null entry is rejected before cancellation is checked. Cancellation
    /// is honored even for an empty batch; otherwise an empty batch requires no database
    /// access. Database failures and cancellation propagate without modifying inputs.
    /// This writer does not retry or suppress duplicate-key failures.
    /// </remarks>
    Task<IReadOnlyList<BillingAuditRuntimeRecord>> WriteBatchAsync(
        IReadOnlyList<BillingAuditRuntimeRecord> records,
        CancellationToken cancellationToken = default);
}
