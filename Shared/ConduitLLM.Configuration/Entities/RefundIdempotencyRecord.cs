using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Durable request/response record for an administrative refund operation.
/// </summary>
public sealed class RefundIdempotencyRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int VirtualKeyGroupId { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;

    public long RefundTransactionId { get; set; }

    public string ResponseJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
