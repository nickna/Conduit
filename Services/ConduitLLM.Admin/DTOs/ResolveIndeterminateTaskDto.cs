using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Admin.DTOs;

public sealed class ResolveIndeterminateTaskDto
{
    [Required]
    public string Resolution { get; set; } = string.Empty;

    [Required, StringLength(500)]
    public string Reason { get; set; } = string.Empty;

    [StringLength(200)]
    public string? ProviderOperationId { get; set; }
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
    string? ProviderOperationId);

public sealed record TaskResolutionAcceptedDto(
    string TaskId,
    string Resolution,
    string DispatchId,
    DateTime AcceptedAt);
