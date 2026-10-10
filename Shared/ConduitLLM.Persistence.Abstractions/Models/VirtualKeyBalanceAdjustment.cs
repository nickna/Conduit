namespace ConduitLLM.Persistence;

/// <summary>
/// Persisted reference category for a virtual-key group ledger entry.
/// Values match the existing PostgreSQL integer contract.
/// </summary>
public enum VirtualKeyBalanceReferenceType
{
    Manual = 1,
    VirtualKey = 2,
    System = 3,
    Initial = 4
}

/// <summary>
/// Complete request for an atomic group-balance and ledger update.
/// </summary>
public sealed record VirtualKeyBalanceAdjustment(
    int GroupId,
    decimal Amount,
    string? Description,
    string? InitiatedBy,
    VirtualKeyBalanceReferenceType ReferenceType,
    string? ReferenceId = null,
    string? IdempotencyKey = null,
    DateTime? BillingWindowStartUtc = null);

/// <summary>
/// Result of an atomic group-balance adjustment.
/// </summary>
public sealed record VirtualKeyBalanceAdjustmentResult(
    decimal NewBalance,
    decimal LifetimeSpent,
    bool Applied);

/// <summary>
/// Raised when an idempotency key is reused with different adjustment data.
/// </summary>
public sealed class VirtualKeyBalanceConflictException : InvalidOperationException
{
    public VirtualKeyBalanceConflictException(string message)
        : base(message)
    {
    }
}
