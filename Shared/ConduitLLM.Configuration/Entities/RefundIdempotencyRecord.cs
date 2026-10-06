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

    [Required, StringLength(100)]
    public string OperationId { get; set; } = string.Empty;

    [Required, StringLength(64)]
    public string RequestHash { get; set; } = string.Empty;

    public long RefundTransactionId { get; set; }

    [Required]
    public string ResponseJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
