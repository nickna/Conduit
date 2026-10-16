using ConduitLLM.Persistence;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>
/// Durable claims shared by every Gateway instance and deployment mode. Claims are never
/// expired or automatically reassigned: an abandoned dispatch cannot safely be repeated.
/// </summary>
public interface IFunctionExecutionIdempotencyStore
{
    Task<FunctionExecutionClaim> ClaimAsync(
        int virtualKeyId, string keyHash, string fingerprint,
        CancellationToken cancellationToken = default);

    Task<FunctionExecutionClaim?> FindAsync(
        int virtualKeyId, string keyHash, CancellationToken cancellationToken = default);

    Task<bool> MarkDispatchedAsync(
        FunctionExecutionClaim claim, CancellationToken cancellationToken = default);

    /// <summary>Stores the complete serialized execution before any billing settlement.</summary>
    Task<bool> CompleteAsync(
        FunctionExecutionClaim claim, string resultJson, CancellationToken cancellationToken = default);

    /// <summary>Preserves a permanent tombstone without replacing a completed result.</summary>
    Task<bool> MarkUncertainAsync(
        FunctionExecutionClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles a completed claim against its original billing group. Safe to retry after any
    /// failure, including a failure after the ledger accepted the debit.
    /// </summary>
    Task SettleBillingAsync(
        FunctionExecutionClaim claim, decimal cost, CancellationToken cancellationToken = default);
}
