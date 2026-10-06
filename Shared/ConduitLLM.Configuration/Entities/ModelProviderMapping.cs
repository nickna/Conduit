using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// Maps a generic model alias (e.g., "gpt-4-turbo") to a specific provider's model name
    /// and associates it with provider credentials. This entity enables routing requests to
    /// specific provider models regardless of the model name used in the request.
    /// </summary>
    public class ModelProviderMapping : IEntity<int>, IAuditableEntity
    {
        /// <summary>
        /// Unique identifier for the model-provider mapping.
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// User-friendly model alias used in client requests.
        /// This is the name that clients will use in their API calls (e.g., "gpt-4").
        /// </summary>
        [Required]
        [StringLength(100)]
        public string ModelAlias { get; set; } = string.Empty;

        /// <summary>
        /// The actual model identifier expected by the provider.
        /// This is the provider-specific model name (e.g., "gpt-4-turbo-preview", "claude-3-opus-20240229").
        /// </summary>
        [Required]
        [StringLength(100)]
        public string ProviderModelId { get; set; } = string.Empty;

        /// <summary>
        /// Foreign key to the provider entity.
        /// Links this mapping to the provider used to authenticate with the provider.
        /// </summary>
        public int ProviderId { get; set; }

        /// <summary>
        /// Navigation property to the associated provider.
        /// Contains authentication details for connecting to the provider.
        /// </summary>
        [ForeignKey("ProviderId")]
        public virtual Provider Provider { get; set; } = null!;

        /// <summary>
        /// Indicates whether this mapping is currently active.
        /// When false, the router will not use this mapping for routing requests.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Deterministic tie-break priority; lower values win.</summary>
        [Range(0, int.MaxValue)]
        public int RoutingPriority { get; set; } = 100;

        /// <summary>Multiplier applied to the balanced route score.</summary>
        [Range(0.1, 2.0)]
        [Column(TypeName = "decimal(4, 2)")]
        public decimal RoutingWeight { get; set; } = 1.0m;

        /// <summary>
        /// Optional provider-specific request options as a JSON object. For OpenRouter mappings the
        /// top-level keys (provider, plugins, transforms, models, route) are merged into every
        /// outgoing request routed through this mapping. Caller-supplied ExtensionData wins on key
        /// conflict; standard parameters always win.
        /// </summary>
        [Column(TypeName = "text")]
        public string? ProviderOptions { get; set; }

        /// <summary>
        /// The UTC timestamp when this mapping was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The UTC timestamp when this mapping was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;


        /// <summary>
        /// Required foreign key to the ModelProviderTypeAssociation entity.
        /// Links this mapping to provider-specific model metadata including variations, quality scores, and costs.
        /// This association also provides the link to the canonical Model entity.
        /// </summary>
        [Required]
        public int ModelProviderTypeAssociationId { get; set; }

        /// <summary>
        /// Navigation property to the associated ModelProviderTypeAssociation.
        /// Contains provider-specific model metadata, variations, and cost information.
        /// Access the Model through ModelProviderTypeAssociation.Model.
        /// </summary>
        [ForeignKey("ModelProviderTypeAssociationId")]
        public virtual ModelProviderTypeAssociation ModelProviderTypeAssociation { get; set; } = null!;



    }
}
