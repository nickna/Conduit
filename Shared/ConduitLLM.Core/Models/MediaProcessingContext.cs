using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Core.Models
{
    /// <summary>
    /// Context information for media processing operations.
    /// </summary>
    public class MediaProcessingContext
    {
        /// <summary>Recovery must validate and store output; provider-URL fallback is forbidden.</summary>
        public bool RequireStoredMedia { get; set; }
        /// <summary>
        /// Gets or sets the type of media being processed.
        /// </summary>
        public MediaType MediaType { get; set; }

        /// <summary>
        /// Gets or sets the index of the media item in a batch.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Gets or sets the model information used for generation.
        /// </summary>
        public GenerationModelInfo? ModelInfo { get; set; }

        /// <summary>
        /// Gets or sets the original prompt used for generation.
        /// </summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the virtual key ID for tracking.
        /// </summary>
        public int VirtualKeyId { get; set; }

        /// <summary>Optional caller identifier to persist as storage metadata.</summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// Gets or sets the request ID for tracking.
        /// </summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the correlation ID for distributed tracing.
        /// </summary>
        public string? CorrelationId { get; set; }
    }

    /// <summary>
    /// Model information for media generation.
    /// </summary>
    public class GenerationModelInfo
    {
        /// <summary>
        /// Gets or sets the model identifier.
        /// </summary>
        public string ModelId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the model alias.
        /// </summary>
        public string ModelAlias { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the provider ID.
        /// </summary>
        public int ProviderId { get; set; }

        /// <summary>
        /// Gets or sets the provider entity.
        /// </summary>
        public Provider? Provider { get; set; }

        /// <summary>
        /// Gets or sets the ID of the ModelCost record linked to the resolved model association, if any.
        /// When set, cost calculation uses a direct lookup instead of string matching on model ids.
        /// </summary>
        public int? ModelCostId { get; set; }

        /// <summary>
        /// Gets or sets the canonical model identifier from the model's provider-type association.
        /// Cost records are matched against this identifier during string-based cost lookup, so it is
        /// preferred over <see cref="ModelId"/> (which may hold a stale legacy ProviderModelId value).
        /// </summary>
        public string? CostIdentifier { get; set; }

        /// <summary>
        /// Gets the provider name.
        /// </summary>
        public string ProviderName => Provider?.ProviderName ?? Provider?.ProviderType.ToString() ?? "unknown";

        /// <summary>
        /// Gets the provider type.
        /// </summary>
        public ProviderType ProviderType => Provider?.ProviderType ?? ProviderType.OpenAI;
    }
}
