using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ConduitLLM.Configuration.Validation;

namespace ConduitLLM.Configuration.DTOs
{
    /// <summary>
    /// A discovered provider model submitted for bulk mapping resolution.
    /// </summary>
    public class BulkModelMappingItemDto
    {
        /// <summary>
        /// The alias clients will use when requesting this model.
        /// </summary>
        [Required(ErrorMessage = "Model Alias is required")]
        public string ModelAlias { get; set; } = string.Empty;

        /// <summary>
        /// The configured provider instance that will serve the model.
        /// </summary>
        [Required]
        [Range(1, int.MaxValue)]
        public int ProviderId { get; set; }

        /// <summary>
        /// The provider-specific model identifier used to resolve model metadata.
        /// </summary>
        [Required(ErrorMessage = "Provider Model ID is required")]
        public string ProviderModelId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request DTO for resolving associations and conflicts before bulk creation.
    /// </summary>
    public class BulkModelMappingPreviewRequest
    {
        /// <summary>The discovered provider models to resolve.</summary>
        [Required]
        [MinItems(1)]
        public List<BulkModelMappingItemDto> Mappings { get; set; } = new();
    }

    /// <summary>
    /// Request DTO for idempotent bulk model mapping creation.
    /// </summary>
    public class BulkModelMappingCreateRequest : BulkModelMappingPreviewRequest
    {
        /// <summary>The default routing priority applied to newly created mappings.</summary>
        [Range(0, int.MaxValue)]
        public int Priority { get; set; } = 50;

        /// <summary>The default routing weight applied to newly created mappings.</summary>
        [Range(0.1, 2.0)]
        public decimal Weight { get; set; } = 1.0m;

        /// <summary>Whether newly created mappings are enabled.</summary>
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// DTO for creating a single model provider mapping
    /// </summary>
    public class CreateModelProviderMappingDto
    {
        /// <summary>
        /// The model alias used in client requests
        /// </summary>
        [Required(ErrorMessage = "Model Alias is required")]
        public string ModelAlias { get; set; } = string.Empty;

        /// <summary>
        /// The provider-specific model identifier
        /// </summary>
        [Required(ErrorMessage = "Provider Model ID is required")]
        public string ProviderModelId { get; set; } = string.Empty;

        /// <summary>
        /// The provider ID
        /// </summary>
        [Required(ErrorMessage = "Provider ID is required")]
        public int ProviderId { get; set; }

        /// <summary>
        /// The ID of the ModelProviderTypeAssociation entity.
        /// Links this mapping to provider-specific model metadata including variations, quality scores, and costs.
        /// </summary>
        [Required(ErrorMessage = "Model Provider Type Association is required")]
        public int ModelProviderTypeAssociationId { get; set; }

        /// <summary>
        /// The priority of this mapping (lower values have higher priority)
        /// </summary>
        public int Priority { get; set; }

        [Range(0.1, 2.0)]
        public decimal Weight { get; set; } = 1.0m;

        /// <summary>
        /// Whether this mapping is currently enabled
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Optional provider-specific request options as a JSON object.
        /// </summary>
        public Dictionary<string, JsonElement>? ProviderOptions { get; set; }
    }

    /// <summary>
    /// Per-item association resolution and conflict details.
    /// </summary>
    public class BulkModelMappingResolutionDto
    {
        /// <summary>The item's index in the request.</summary>
        [Required]
        public int Index { get; set; }

        /// <summary>The submitted model alias.</summary>
        [Required]
        public string ModelAlias { get; set; } = string.Empty;

        /// <summary>The submitted provider instance ID.</summary>
        [Required]
        public int ProviderId { get; set; }

        /// <summary>The submitted provider-specific model identifier.</summary>
        [Required]
        public string ProviderModelId { get; set; } = string.Empty;

        /// <summary>The compatible association resolved by the server, when available.</summary>
        public int? ModelProviderTypeAssociationId { get; set; }

        /// <summary>Whether this item must not be selected for creation.</summary>
        [Required]
        public bool HasConflict { get; set; }

        /// <summary>The existing alias/provider mapping, when one caused the conflict.</summary>
        public ModelProviderMappingDto? ExistingMapping { get; set; }

        /// <summary>A machine-readable error category, when resolution failed.</summary>
        public BulkModelMappingErrorType? ErrorType { get; set; }

        /// <summary>A user-facing explanation of the conflict or resolution failure.</summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Response DTO for a bulk mapping preview.
    /// </summary>
    public class BulkModelMappingPreviewResponse
    {
        /// <summary>Resolution details in request order.</summary>
        [Required]
        public List<BulkModelMappingResolutionDto> Items { get; set; } = new();

        /// <summary>Total number of items evaluated.</summary>
        [Required]
        public int TotalProcessed { get; set; }

        /// <summary>Number of items that cannot be created.</summary>
        [Required]
        public int ConflictCount { get; set; }
    }

    /// <summary>
    /// Response DTO for idempotent bulk model mapping creation.
    /// Each item is committed independently; failures do not roll back successful items.
    /// </summary>
    public class BulkModelMappingCreateResponse
    {
        /// <summary>Mappings created by this request.</summary>
        [Required]
        public List<ModelProviderMappingDto> Created { get; set; } = new();

        /// <summary>Equivalent mappings that already existed and made the request idempotent.</summary>
        [Required]
        public List<ModelProviderMappingDto> Existing { get; set; } = new();

        /// <summary>Items that could not be resolved or created.</summary>
        [Required]
        public List<BulkModelMappingResolutionDto> Failed { get; set; } = new();

        /// <summary>Total number of submitted items.</summary>
        [Required]
        public int TotalProcessed { get; set; }

        /// <summary>Number of mappings created by this request.</summary>
        [Required]
        public int CreatedCount { get; set; }

        /// <summary>Number of submitted items already satisfied by an equivalent mapping.</summary>
        [Required]
        public int ExistingCount { get; set; }

        /// <summary>Number of successful or idempotently satisfied items.</summary>
        [Required]
        public int SuccessCount { get; set; }

        /// <summary>Number of failed items.</summary>
        [Required]
        public int FailureCount { get; set; }

        /// <summary>Whether all submitted items succeeded or were already satisfied.</summary>
        [Required]
        public bool IsSuccess { get; set; }

        /// <summary>Whether the operation committed some items and rejected others.</summary>
        [Required]
        public bool IsPartialSuccess { get; set; }
    }

    /// <summary>
    /// Categories of bulk mapping resolution and creation errors.
    /// </summary>
    public enum BulkModelMappingErrorType
    {
        Validation,
        ProviderNotFound,
        AssociationNotFound,
        AssociationProviderMismatch,
        AssociationDisabled,
        AmbiguousAssociation,
        ExistingMapping,
        ExistingMappingMismatch,
        DuplicateRequest,
        CanonicalModelMismatch,
        SystemError
    }
}
