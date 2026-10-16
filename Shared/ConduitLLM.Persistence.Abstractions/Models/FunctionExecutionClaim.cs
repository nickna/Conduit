namespace ConduitLLM.Persistence;

/// <summary>The durable dispatch state of an idempotent synchronous function execution.</summary>
public enum FunctionExecutionClaimState
{
    Claimed,
    Dispatching,
    Completed,
    Uncertain
}

/// <summary>
/// A permanent idempotency claim. Billing ownership and the execution ID are fixed when claimed.
/// IsOwner applies only to the ClaimAsync call that inserted this row.
/// </summary>
public sealed record FunctionExecutionClaim(
    int VirtualKeyId,
    string KeyHash,
    string Fingerprint,
    Guid ExecutionId,
    int BillingGroupId,
    DateTime CreatedAtUtc,
    FunctionExecutionClaimState State,
    string? ResultJson,
    DateTime? BilledAtUtc,
    bool IsOwner = false);
