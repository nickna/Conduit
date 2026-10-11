namespace ConduitLLM.Core.Models
{
    /// <summary>
    /// Strongly typed metadata for async tasks.
    /// Contains all common metadata fields used across different task types.
    /// </summary>
    public class TaskMetadata
    {
        /// <summary>Original provider context, including its accepted operation ID when available.</summary>
        public MediaProviderOperation? MediaProvider { get; set; }
        /// <summary>Worker that recorded the original provider invocation.</summary>
        public string? ProviderInvocationWorkerId { get; set; }
        /// <summary>Current operator recovery decision; payloads are excluded from operator listings.</summary>
        public MediaTaskReconciliation? Reconciliation { get; set; }
        /// <summary>Earlier recovery decisions retained for audit after an explicitly blocked attempt.</summary>
        public List<MediaTaskReconciliation>? ReconciliationHistory { get; set; }
        /// <summary>
        /// The ID of the virtual key that owns this task.
        /// Required for authorization checks.
        /// </summary>
        public int VirtualKeyId { get; set; }

        /// <summary>
        /// The model being used for this task (e.g., "dall-e-3", "gpt-4").
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// The prompt or input text for generation tasks.
        /// </summary>
        public string? Prompt { get; set; }

        /// <summary>
        /// Correlation ID for tracking related operations.
        /// </summary>
        public string? CorrelationId { get; set; }

        /// <summary>
        /// Serialized payload of the original request event.
        /// Used for event sourcing and replay.
        /// </summary>
        public string? Payload { get; set; }

        /// <summary>
        /// For video generation tasks: the video ID being generated.
        /// </summary>
        public string? VideoId { get; set; }

        /// <summary>
        /// For webhook tasks: the URL to notify.
        /// </summary>
        public string? WebhookUrl { get; set; }

        /// <summary>
        /// For webhook tasks: custom headers to include.
        /// </summary>
        public Dictionary<string, string>? WebhookHeaders { get; set; }

        /// <summary>
        /// Task priority for queue processing.
        /// </summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Additional custom properties for extensibility.
        /// </summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, object>? ExtensionData { get; set; }

        /// <summary>
        /// Creates a new TaskMetadata instance with the required virtual key ID.
        /// </summary>
        /// <param name="virtualKeyId">The ID of the virtual key that owns this task.</param>
        public TaskMetadata(int virtualKeyId)
        {
            VirtualKeyId = virtualKeyId;
        }

        /// <summary>
        /// Parameterless constructor for deserialization.
        /// </summary>
        public TaskMetadata()
        {
        }
    }
}
