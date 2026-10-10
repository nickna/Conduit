using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Events
{
    // ===============================
    // Video Generation Domain Events
    // ===============================

    /// <summary>
    /// Raised when a video generation request is submitted.
    /// Enables async processing across multiple service instances.
    /// </summary>
    public record VideoGenerationRequested : DomainEvent
    {
        /// <summary>
        /// Unique request identifier for tracking
        /// </summary>
        public string RequestId { get; init; } = string.Empty;

        /// <summary>
        /// The complete video generation request. Keeping the API model on the event
        /// prevents newly added or provider-specific fields from being dropped in transit.
        /// </summary>
        public VideoGenerationRequest? Request { get; init; }

        /// <summary>
        /// Virtual Key ID or hash for authorization and spend tracking
        /// </summary>
        public string VirtualKeyId { get; init; } = string.Empty;

        /// <summary>
        /// Whether this is an async generation request
        /// </summary>
        public bool IsAsync { get; init; } = false;

        /// <summary>
        /// When the request was submitted
        /// </summary>
        public DateTime RequestedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Optional webhook URL to receive notifications when video generation completes
        /// </summary>
        public string? WebhookUrl { get; init; }

        /// <summary>
        /// Optional headers to include in the webhook request
        /// </summary>
        public Dictionary<string, string>? WebhookHeaders { get; init; }

        /// <summary>
        /// Captures the model, prompt, and parameters fields written by the legacy event
        /// contract. New messages never populate this property.
        /// </summary>
        [JsonExtensionData]
        // Extension data must be assigned after construction. A source-generated
        // initializer for an init-only extension property becomes a constructor
        // parameter, which System.Text.Json rejects for extension data.
        public Dictionary<string, JsonElement>? LegacyPayload { get; set; }

        /// <summary>
        /// Partition key for ordered processing per virtual key
        /// </summary>
        public string PartitionKey => VirtualKeyId;

        /// <summary>
        /// Returns the direct request model, or reconstructs it from the pre-#1281
        /// event shape while an older queued message is being drained.
        /// </summary>
        public VideoGenerationRequest ResolveRequest()
        {
            if (Request is not null)
            {
                return Request;
            }

            var parameters = GetLegacyElement("parameters");
            var extensionData = ReadLegacyExtensionData(parameters);

            return new VideoGenerationRequest
            {
                Model = GetLegacyString("model") ?? string.Empty,
                Prompt = GetLegacyString("prompt") ?? string.Empty,
                Size = GetString(parameters, "size"),
                Duration = GetInt32(parameters, "duration"),
                Fps = GetInt32(parameters, "fps"),
                Style = GetString(parameters, "style"),
                ResponseFormat = GetString(parameters, "responseFormat", "response_format"),
                ExtensionData = extensionData
            };
        }

        private JsonElement? GetLegacyElement(string name)
        {
            if (LegacyPayload is null)
            {
                return null;
            }

            foreach (var (key, value) in LegacyPayload)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return null;
        }

        private string? GetLegacyString(string name)
            => GetLegacyElement(name) is { ValueKind: JsonValueKind.String } element
                ? element.GetString()
                : null;

        private static string? GetString(JsonElement? parent, params string[] names)
        {
            var element = GetProperty(parent, names);
            return element is { ValueKind: JsonValueKind.String } ? element.Value.GetString() : null;
        }

        private static int? GetInt32(JsonElement? parent, params string[] names)
        {
            var element = GetProperty(parent, names);
            return element is { ValueKind: JsonValueKind.Number } &&
                   element.Value.TryGetInt32(out var value)
                ? value
                : null;
        }

        private static JsonElement? GetProperty(JsonElement? parent, params string[] names)
        {
            if (parent is not { ValueKind: JsonValueKind.Object } objectElement)
            {
                return null;
            }

            foreach (var property in objectElement.EnumerateObject())
            {
                if (names.Any(name => string.Equals(
                        property.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return property.Value;
                }
            }

            return null;
        }

        private static Dictionary<string, JsonElement>? ReadLegacyExtensionData(JsonElement? parameters)
        {
            if (parameters is not { ValueKind: JsonValueKind.Object } objectElement)
            {
                return null;
            }

            var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in objectElement.EnumerateObject())
            {
                if (property.Name.Equals("providerOptions", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var option in property.Value.EnumerateObject())
                    {
                        result[option.Name] = option.Value.Clone();
                    }
                }
                else if (property.Name.Equals("startImage", StringComparison.OrdinalIgnoreCase))
                {
                    result["start_image"] = property.Value.Clone();
                }
                else if (property.Name.Equals("endImage", StringComparison.OrdinalIgnoreCase))
                {
                    result["end_image"] = property.Value.Clone();
                }
            }

            return result.Count == 0 ? null : result;
        }
    }

    /// <summary>
    /// Raised when video generation starts processing
    /// </summary>
    public record VideoGenerationStarted : DomainEvent
    {
        /// <summary>
        /// Request identifier
        /// </summary>
        public string RequestId { get; init; } = string.Empty;

        /// <summary>
        /// Provider handling the generation
        /// </summary>
        public string Provider { get; init; } = string.Empty;

        /// <summary>
        /// When processing started
        /// </summary>
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Estimated completion time in seconds
        /// </summary>
        public int? EstimatedSeconds { get; init; }
    }

    /// <summary>
    /// Raised when video generation progress updates occur
    /// </summary>
    public record VideoGenerationProgress : DomainEvent
    {
        /// <summary>
        /// Request identifier
        /// </summary>
        public string RequestId { get; init; } = string.Empty;

        /// <summary>
        /// Progress percentage (0-100)
        /// </summary>
        public int ProgressPercentage { get; init; }

        /// <summary>
        /// Current status message
        /// </summary>
        public string Status { get; init; } = string.Empty;

        /// <summary>
        /// Optional detailed message
        /// </summary>
        public string? Message { get; init; }

        /// <summary>
        /// Frames rendered (if applicable)
        /// </summary>
        public int? FramesCompleted { get; init; }

        /// <summary>
        /// Total frames to render (if applicable)
        /// </summary>
        public int? TotalFrames { get; init; }
    }

    /// <summary>
    /// Raised when video generation completes successfully
    /// </summary>
    public record VideoGenerationCompleted : DomainEvent
    {
        /// <summary>
        /// Request identifier
        /// </summary>
        public string RequestId { get; init; } = string.Empty;

        /// <summary>
        /// URL where the video can be accessed
        /// </summary>
        public string VideoUrl { get; init; } = string.Empty;

        /// <summary>
        /// Optional preview/thumbnail URL
        /// </summary>
        public string? PreviewUrl { get; init; }

        /// <summary>
        /// Video duration in seconds
        /// </summary>
        public double Duration { get; init; }

        /// <summary>
        /// Video resolution (e.g., "1280x720")
        /// </summary>
        public string Resolution { get; init; } = string.Empty;

        /// <summary>
        /// File size in bytes
        /// </summary>
        public long FileSize { get; init; }

        /// <summary>
        /// Total generation duration
        /// </summary>
        public TimeSpan GenerationDuration { get; init; }

        /// <summary>
        /// Total cost incurred
        /// </summary>
        public decimal Cost { get; init; }

        /// <summary>
        /// Provider used for generation
        /// </summary>
        public string Provider { get; init; } = string.Empty;

        /// <summary>
        /// Model used for generation
        /// </summary>
        public string Model { get; init; } = string.Empty;

        /// <summary>
        /// When generation completed
        /// </summary>
        public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Raised when video generation fails
    /// </summary>
    public record VideoGenerationFailed : DomainEvent
    {
        /// <summary>
        /// Request identifier
        /// </summary>
        public string RequestId { get; init; } = string.Empty;

        /// <summary>
        /// Error message
        /// </summary>
        public string Error { get; init; } = string.Empty;

        /// <summary>
        /// Error code (if available)
        /// </summary>
        public string? ErrorCode { get; init; }

        /// <summary>
        /// Provider that failed
        /// </summary>
        public string? Provider { get; init; }

        /// <summary>
        /// Whether the request can be retried
        /// </summary>
        public bool IsRetryable { get; init; } = true;

        /// <summary>
        /// Number of retry attempts made
        /// </summary>
        public int RetryCount { get; init; } = 0;

        /// <summary>
        /// Maximum number of retries allowed
        /// </summary>
        public int MaxRetries { get; init; } = 3;

        /// <summary>
        /// When the failure occurred
        /// </summary>
        public DateTime FailedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// When the task should be retried (if applicable)
        /// </summary>
        public DateTime? NextRetryAt { get; init; }
    }

    /// <summary>
    /// Raised when a video generation is cancelled
    /// </summary>
    public record VideoGenerationCancelled : DomainEvent
    {
        /// <summary>
        /// Request identifier
        /// </summary>
        public string RequestId { get; init; } = string.Empty;

        /// <summary>
        /// Reason for cancellation
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// When the cancellation occurred
        /// </summary>
        public DateTime CancelledAt { get; init; } = DateTime.UtcNow;
    }

}
