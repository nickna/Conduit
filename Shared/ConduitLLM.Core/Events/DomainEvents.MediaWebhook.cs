namespace ConduitLLM.Core.Events
{
    // ===============================
    // Media Lifecycle Domain Events
    // ===============================

    /// <summary>
    /// Raised when any media (image or video) is successfully generated and stored to CDN.
    /// Enables tracking of media lifecycle for future cleanup and cost management.
    /// </summary>
    public record MediaGenerationCompleted : DomainEvent
    {
        /// <summary>
        /// Type of media generated (Image or Video)
        /// </summary>
        public Models.MediaType MediaType { get; init; }

        /// <summary>
        /// Virtual Key ID that owns this media
        /// </summary>
        public int VirtualKeyId { get; init; }

        /// <summary>
        /// Public URL where the media is stored
        /// </summary>
        public string MediaUrl { get; init; } = string.Empty;

        /// <summary>
        /// Storage key/path for the media file
        /// </summary>
        public string StorageKey { get; init; } = string.Empty;

        /// <summary>
        /// Size of the media file in bytes
        /// </summary>
        public long FileSizeBytes { get; init; }

        /// <summary>
        /// MIME type of the media file
        /// </summary>
        public string ContentType { get; init; } = string.Empty;

        /// <summary>
        /// Model used to generate this media
        /// </summary>
        public string GeneratedByModel { get; init; } = string.Empty;

        /// <summary>
        /// Provider used to generate this media.
        /// </summary>
        public string Provider { get; init; } = string.Empty;

        /// <summary>
        /// Prompt used to generate this media
        /// </summary>
        public string GenerationPrompt { get; init; } = string.Empty;

        /// <summary>
        /// When the media was generated
        /// </summary>
        public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// When the media should expire (if applicable)
        /// </summary>
        public DateTime? ExpiresAt { get; init; }

        /// <summary>
        /// Additional metadata specific to the media type
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// Partition key for ordered processing per virtual key
        /// </summary>
        public string PartitionKey => VirtualKeyId.ToString();
    }

    // ===============================
    // Webhook Delivery Domain Events
    // ===============================

    /// <summary>
    /// Event types for webhook notifications
    /// </summary>
    public enum WebhookEventType
    {
        /// <summary>
        /// Task has started processing
        /// </summary>
        TaskStarted,
        
        /// <summary>
        /// Task progress update
        /// </summary>
        TaskProgress,
        
        /// <summary>
        /// Task completed successfully
        /// </summary>
        TaskCompleted,
        
        /// <summary>
        /// Task failed with error
        /// </summary>
        TaskFailed,
        
        /// <summary>
        /// Task was cancelled
        /// </summary>
        TaskCancelled
    }

    /// <summary>
    /// Raised when a webhook needs to be delivered
    /// Enables scalable webhook delivery with deduplication and retry logic
    /// </summary>
    public record WebhookDeliveryRequested : DomainEvent
    {
        /// <summary>Owner captured by the producer; zero denotes a legacy event without ownership metadata.</summary>
        public int VirtualKeyId { get; init; }
        /// <summary>Internal retry cycle. Operator replay increments this without changing EventId.</summary>
        public int DeliveryCycle { get; init; }
        /// <summary>Original start of this retry cycle. Null uses the legacy event Timestamp.</summary>
        public DateTime? DeliveryStartedAt { get; init; }
        /// <summary>
        /// Unique task identifier (e.g., video/image generation request ID)
        /// </summary>
        public string TaskId { get; init; } = string.Empty;
        
        /// <summary>
        /// Type of task (e.g., "video", "image")
        /// </summary>
        public string TaskType { get; init; } = string.Empty;
        
        /// <summary>
        /// Webhook URL to deliver the payload to
        /// </summary>
        public string WebhookUrl { get; init; } = string.Empty;
        
        /// <summary>
        /// Type of webhook event
        /// </summary>
        public WebhookEventType EventType { get; init; }
        
        /// <summary>
        /// Webhook payload as JSON string (pre-serialized for size control)
        /// Limited to 1MB to prevent memory issues
        /// </summary>
        public string PayloadJson { get; init; } = "{}";
        
        /// <summary>
        /// Optional custom headers to include in the webhook request
        /// </summary>
        public Dictionary<string, string>? Headers { get; init; }
        
        /// <summary>
        /// Current retry count (used for retry logic)
        /// </summary>
        public int RetryCount { get; init; } = 0;
        
        /// <summary>
        /// When to retry next (if retry is needed)
        /// </summary>
        public DateTime? NextRetryAt { get; init; }
        
        /// <summary>
        /// Partition key for ordered processing per task
        /// Ensures single webhook delivery per task at a time
        /// </summary>
        public string PartitionKey => TaskId;
    }
}
