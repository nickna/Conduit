namespace ConduitLLM.Configuration.Interfaces
{
    public enum SpendReservationSettlementStatus
    {
        Missing,
        Settled,
        AlreadySettled,
        SettledOverEstimate,
        Conflict
    }

    public sealed record SpendReservationSettlementResult(
        SpendReservationSettlementStatus Status,
        decimal ActualAmount);

    public enum SpendReconciliationDisposition
    {
        AuthoritativeProviderCost,
        ProviderCostWaived
    }

    public sealed record SpendReservationReconciliation(
        string BillingRequestId,
        int VirtualKeyId,
        decimal KnownAmount,
        decimal HeldAmount,
        DateTime BillingWindowStartUtc,
        string Reason,
        bool Resolved,
        decimal? ProviderAmount,
        string? Disposition,
        string? AuditActor,
        string? AuditNote);

    /// <summary>
    /// Interface for batch spend update service
    /// </summary>
    public interface IBatchSpendUpdateService
    {
        /// <summary>
        /// Gets whether the service is healthy and able to accept updates
        /// </summary>
        bool IsHealthy { get; }

        /// <summary>
        /// Event raised after successful batch spend updates with the key hashes that were updated
        /// </summary>
        event Action<string[]>? SpendUpdatesCompleted;

        /// <summary>
        /// Queues a spend update to Redis for batch processing.
        /// Throws on failure so the caller can fall back to alternative paths.
        /// </summary>
        Task QueueSpendUpdateAsync(int virtualKeyId, decimal cost, DateTime? billedAtUtc = null);

        /// <summary>
        /// Gets spend that has not yet been reflected in the group's database balance,
        /// including active reservations.
        /// </summary>
        Task<decimal> GetPendingSpendAsync(int virtualKeyId);

        /// <summary>
        /// Atomically reserves part of a virtual key group's available balance.
        /// </summary>
        Task<bool> TryReserveSpendAsync(int virtualKeyId, decimal amount, string reservationId);

        /// <summary>
        /// Moves a reservation into the invocation-started state. Started reservations
        /// are not eligible for automatic expiry or ordinary release.
        /// </summary>
        Task<bool> MarkSpendReservationInvocationStartedAsync(int virtualKeyId, string reservationId);

        /// <summary>
        /// Atomically replaces a reservation with actual pending spend.
        /// </summary>
        Task<SpendReservationSettlementResult> SettleSpendReservationAsync(
            int virtualKeyId,
            string reservationId,
            decimal actualAmount,
            DateTime? billedAtUtc = null);

        /// <summary>
        /// Releases a previously-created spend reservation.
        /// </summary>
        Task ReleaseSpendReservationAsync(int virtualKeyId, string reservationId);

        /// <summary>
        /// Durably marks unresolved provider accounting and atomically queues cumulative known
        /// spend once, retaining the unconsumed reservation hold. Null leaves known spend unchanged.
        /// </summary>
        Task<SpendReservationSettlementResult> RecordIndeterminateSpendReservationAsync(
            int virtualKeyId, string reservationId, decimal? knownAmount, string reason,
            DateTime? billedAtUtc = null);

        Task<SpendReservationReconciliation?> GetSpendReservationReconciliationAsync(
            int virtualKeyId, string reservationId);

        /// <summary>
        /// Resolves only the provider portion from authoritative cost or an audited waiver,
        /// queues any confirmed cost once in the original billing window, and releases the hold.
        /// </summary>
        Task<SpendReservationSettlementResult> ReconcileSpendReservationAsync(
            int virtualKeyId, string reservationId, decimal providerAmount,
            SpendReconciliationDisposition disposition, string auditActor, string auditNote);

        /// <summary>
        /// Queues a spend update to an in-memory fallback queue.
        /// Used as a last resort when both Redis and direct DB writes fail.
        /// Updates are drained on the next successful flush cycle.
        /// </summary>
        void QueueFallbackUpdate(int virtualKeyId, decimal cost, DateTime? billedAtUtc = null);
    }
}
