using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Admin.DTOs;

public sealed class ResolveIndeterminateTaskDto
{
    /// <summary>Operator disposition: retry, failed_no_charge, or completed_no_charge.</summary>
    [Required]
    public string Resolution { get; set; } = string.Empty;

    [Required, StringLength(500)]
    public string Reason { get; set; } = string.Empty;

    [StringLength(200)]
    public string? ProviderOperationId { get; set; }

    /// <summary>Stable idempotency ID supplied by the operator for completed_no_charge.</summary>
    public Guid? DispatchId { get; set; }
    /// <summary>Owner of the original task; required for completed_no_charge.</summary>
    public int? VirtualKeyId { get; set; }
    /// <summary>Original provider ID, verified against captured context when available.</summary>
    public int? ProviderId { get; set; }
    /// <summary>Original model alias, verified against the persisted generation request.</summary>
    [StringLength(200)]
    public string? Model { get; set; }
    /// <summary>Original provider model identifier, verified against captured context when available.</summary>
    [StringLength(200)]
    public string? ProviderModelId { get; set; }
    /// <summary>Incident or provider evidence reference confirming success and ownership of the output.</summary>
    [StringLength(500)]
    public string? Evidence { get; set; }
    /// <summary>One to ten verified public HTTPS URLs for the original output; recovery validates and stores each item.</summary>
    public List<string>? ResultUrls { get; set; }
    /// <summary>Known provider cost written off without creating a customer debit or refund.</summary>
    [Range(0d, 1000000000d)]
    public decimal? ProviderCost { get; set; }
}

public sealed record IndeterminateTaskDto(
    string TaskId,
    string TaskType,
    string State,
    int VirtualKeyId,
    string? Model,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    string? Error,
    int RetryCount,
    int MaxRetries,
    DateTime? ProviderInvocationStartedAt,
    DateTime? ProviderInvocationCompletedAt,
    string? ProviderOperationId,
    int? ProviderId = null,
    string? ProviderModelId = null,
    string? ReconciliationDispatchId = null,
    string? ReconciliationOutcome = null,
    string? BillingDisposition = null,
    decimal? ProviderCost = null);

public sealed record TaskResolutionAcceptedDto(
    string TaskId,
    string Resolution,
    string DispatchId,
    DateTime AcceptedAt);
