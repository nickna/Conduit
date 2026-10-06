using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// Entity for storing batch operation history
    /// </summary>
    [Table("BatchOperationHistory")]
    public class BatchOperationHistory : IEntity<string>
    {
        /// <summary>
        /// Unique identifier for the batch operation
        /// </summary>
        [Key]
        [StringLength(50)]
        public string OperationId { get; set; } = string.Empty;

        /// <summary>
        /// Implements <see cref="IEntity{TKey}.Id"/> by delegating to <see cref="OperationId"/>.
        /// Not mapped to the database as OperationId is the actual column.
        /// </summary>
        [NotMapped]
        public string Id
        {
            get => OperationId;
            set => OperationId = value;
        }

        /// <summary>
        /// Type of batch operation (e.g., "spend_update", "virtual_key_update", "webhook_send")
        /// </summary>
        [Required]
        [StringLength(50)]
        public string OperationType { get; set; } = string.Empty;

        /// <summary>
        /// Virtual key ID that initiated the operation
        /// </summary>
        public int VirtualKeyId { get; set; }

        /// <summary>
        /// Total number of items in the batch
        /// </summary>
        public int TotalItems { get; set; }

        /// <summary>
        /// Number of successfully processed items
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// Number of failed items
        /// </summary>
        public int FailedCount { get; set; }

        /// <summary>
        /// Final status of the operation
        /// </summary>
        [Required]
        [StringLength(20)]
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// When the operation started
        /// </summary>
        public DateTime StartedAt { get; set; }

        /// <summary>
        /// When the operation completed
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Total duration in seconds
        /// </summary>
        public double? DurationSeconds { get; set; }

        /// <summary>
        /// Average processing rate (items per second)
        /// </summary>
        public double? ItemsPerSecond { get; set; }

        /// <summary>
        /// Error message if the operation failed
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Cancellation reason if the operation was cancelled
        /// </summary>
        public string? CancellationReason { get; set; }

        /// <summary>
        /// JSON serialized error details for failed items
        /// </summary>
        [Column(TypeName = "text")]
        public string? ErrorDetails { get; set; }

        /// <summary>
        /// JSON serialized result summary
        /// </summary>
        [Column(TypeName = "text")]
        public string? ResultSummary { get; set; }

        /// <summary>
        /// JSON serialized metadata about the operation
        /// </summary>
        [Column(TypeName = "text")]
        public string? Metadata { get; set; }

        /// <summary>
        /// Checkpoint data for resumable operations
        /// </summary>
        [Column(TypeName = "text")]
        public string? CheckpointData { get; set; }

        /// <summary>
        /// Whether the operation can be resumed
        /// </summary>
        public bool CanResume { get; set; }

        /// <summary>
        /// Last item processed (for resumption)
        /// </summary>
        public int? LastProcessedIndex { get; set; }

        /// <summary>
        /// Navigation property to virtual key
        /// </summary>
        public virtual VirtualKey VirtualKey { get; set; } = null!;
    }
}
