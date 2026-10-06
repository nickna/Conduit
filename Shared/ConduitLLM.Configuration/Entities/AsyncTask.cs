using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// Represents an asynchronous task with persistent storage.
    /// </summary>
    public class AsyncTask : IEntity<string>, IAuditableEntity
    {
        /// <summary>
        /// Gets or sets the unique identifier for the task.
        /// </summary>
        [Key]
        [StringLength(50)]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the task type (e.g., "image_generation", "video_generation").
        /// </summary>
        [Required]
        [StringLength(100)]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the current state of the task.
        /// </summary>
        /// <remarks>
        /// Stored as integer in database. Values:
        /// 0 = Pending, 1 = Processing, 2 = Completed,
        /// 3 = Failed, 4 = Cancelled, 5 = TimedOut
        /// </remarks>
        [Required]
        public int State { get; set; } = 0; // Default to Pending

        /// <summary>
        /// Gets or sets the JSON-serialized payload/request data.
        /// </summary>
        public string? Payload { get; set; }

        /// <summary>
        /// Gets or sets the progress percentage (0-100).
        /// </summary>
        public int Progress { get; set; } = 0;

        /// <summary>
        /// Gets or sets the progress message.
        /// </summary>
        [StringLength(500)]
        public string? ProgressMessage { get; set; }

        /// <summary>
        /// Gets or sets the JSON-serialized result data.
        /// </summary>
        public string? Result { get; set; }

        /// <summary>
        /// Gets or sets the error message if the task failed.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Gets or sets when the task was created.
        /// </summary>
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets when the task was last updated.
        /// </summary>
        [Required]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets when the task was completed.
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Gets or sets the virtual key ID associated with this task.
        /// </summary>
        [Required]
        public int VirtualKeyId { get; set; }

        /// <summary>
        /// Gets or sets the JSON-serialized metadata.
        /// </summary>
        public string? Metadata { get; set; }

        /// <summary>
        /// Gets or sets whether the task has been archived.
        /// </summary>
        public bool IsArchived { get; set; } = false;

        /// <summary>
        /// Gets or sets when the task was archived.
        /// </summary>
        public DateTime? ArchivedAt { get; set; }

        /// <summary>
        /// Gets or sets the ID of the worker/instance that has leased this task.
        /// </summary>
        [StringLength(100)]
        public string? LeasedBy { get; set; }

        /// <summary>
        /// Gets or sets when the lease on this task expires.
        /// </summary>
        public DateTime? LeaseExpiryTime { get; set; }

        /// <summary>When the external provider invocation began.</summary>
        public DateTime? ProviderInvocationStartedAt { get; set; }

        /// <summary>When the external provider returned a definitive successful result.</summary>
        public DateTime? ProviderInvocationCompletedAt { get; set; }

        /// <summary>Provider operation identifier captured for reconciliation.</summary>
        [StringLength(200)]
        public string? ProviderOperationId { get; set; }

        /// <summary>
        /// Identifies the operator retry command that most recently moved an
        /// indeterminate task back to pending. Used to make command redelivery
        /// idempotent across the database update and follow-on event publish.
        /// </summary>
        [StringLength(64)]
        public string? RetryDispatchId { get; set; }

        /// <summary>
        /// Gets or sets the version number for optimistic concurrency control.
        /// </summary>
        [ConcurrencyCheck]
        public int Version { get; set; } = 0;

        /// <summary>
        /// Gets or sets the number of retry attempts made for this task.
        /// </summary>
        public int RetryCount { get; set; } = 0;

        /// <summary>
        /// Gets or sets the maximum number of retry attempts allowed for this task.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Gets or sets whether the task is retryable if it fails.
        /// </summary>
        public bool IsRetryable { get; set; } = true;

        /// <summary>
        /// Gets or sets when the task should be retried next (null if not scheduled for retry).
        /// </summary>
        public DateTime? NextRetryAt { get; set; }

        /// <summary>
        /// Navigation property to the associated virtual key.
        /// </summary>
        [ForeignKey(nameof(VirtualKeyId))]
        public virtual VirtualKey? VirtualKey { get; set; }

        /// <summary>
        /// Determines if this task is available for processing.
        /// </summary>
        [NotMapped]
        public bool IsAvailable => 
            State == 0 && // Pending
            !IsArchived &&
            (LeasedBy == null || LeaseExpiryTime == null || LeaseExpiryTime < DateTime.UtcNow);
    }
}
