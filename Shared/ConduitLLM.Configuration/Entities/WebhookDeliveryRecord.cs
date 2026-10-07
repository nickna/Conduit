using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Entities;

/// <summary>Delivery ownership and receipts; Wolverine owns queued and scheduled work.</summary>
[Index(nameof(State), nameof(NextAttemptAt))]
[Index(nameof(TaskId))]
[Index(nameof(RetainUntil))]
public sealed class WebhookDeliveryRecord
{
    [Key, StringLength(64)] public string Id { get; set; } = string.Empty;
    [StringLength(128)] public string EventId { get; set; } = string.Empty;
    [StringLength(50)] public string TaskId { get; set; } = string.Empty;
    public int VirtualKeyId { get; set; }
    [StringLength(16)] public string State { get; set; } = "Pending";
    public string RequestJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime Deadline { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime RetainUntil { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTime? ClaimExpiresAt { get; set; }
    public int Attempts { get; set; }
    public int Cycle { get; set; }
    public int? LastStatusCode { get; set; }
    [StringLength(512)] public string? LastError { get; set; }
}
