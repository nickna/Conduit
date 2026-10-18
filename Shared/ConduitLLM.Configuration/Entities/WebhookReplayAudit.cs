using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Entities;

[Index(nameof(DeliveryId), nameof(Cycle), IsUnique = true)]
[Index(nameof(RetainUntil))]
public sealed class WebhookReplayAudit
{
    [Key] public Guid OperationId { get; set; }
    public string DeliveryId { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    public DateTime RetainUntil { get; set; }
    public int Cycle { get; set; }
    public int PreviousAttempts { get; set; }
    public Guid? DeadLetterId { get; set; }
}
