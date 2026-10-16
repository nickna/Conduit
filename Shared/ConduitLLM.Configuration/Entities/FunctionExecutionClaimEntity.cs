namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Schema ownership for permanent function idempotency claims. Deliberately has no foreign
/// keys: deleting a key, configuration, or execution must not erase its replay protection.
/// Gateway access uses the typed Npgsql store rather than EF queries.
/// </summary>
public sealed class FunctionExecutionClaimEntity
{
    public int VirtualKeyId { get; set; }
    public string KeyHash { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public Guid ExecutionId { get; set; }
    public int BillingGroupId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string State { get; set; } = "claimed";
    public string? ResultJson { get; set; }
    public DateTime? BilledAtUtc { get; set; }
}
