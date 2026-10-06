using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using ConduitLLM.Configuration.Entities.Interfaces;
using ConduitLLM.Configuration.Enums;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// A single detected drift between a provider's published model metadata and Conduit's configured
    /// values for one model mapping, awaiting (or resolved by) admin review.
    /// </summary>
    public class ProviderMetadataDriftItem : IEntity<int>
    {
        /// <summary>Unique identifier for the drift item.</summary>
        [Key]
        public int Id { get; set; }

        /// <summary>The model mapping this drift applies to.</summary>
        public int ModelProviderMappingId { get; set; }

        /// <summary>Navigation to the model mapping.</summary>
        [ForeignKey(nameof(ModelProviderMappingId))]
        [JsonIgnore]
        public virtual ModelProviderMapping? Mapping { get; set; }

        /// <summary>Denormalized provider id for cheap filtering.</summary>
        public int ProviderId { get; set; }

        /// <summary>The provider (OpenRouter) model id, e.g. <c>anthropic/claude-3.5-sonnet</c>.</summary>
        [Required]
        [StringLength(200)]
        public string OpenRouterModelId { get; set; } = string.Empty;

        /// <summary>The kind of drift. Stored as a string via a value conversion.</summary>
        public DriftType DriftType { get; set; }

        /// <summary>The review status. Stored as a string via a value conversion.</summary>
        public DriftStatus Status { get; set; }

        /// <summary>Snapshot (JSON) of Conduit's current values at detection time.</summary>
        [Column(TypeName = "text")]
        public string CurrentValuesJson { get; set; } = "{}";

        /// <summary>The provider's proposed values (JSON) at detection time.</summary>
        [Column(TypeName = "text")]
        public string ProposedValuesJson { get; set; } = "{}";

        /// <summary>When this drift was first detected (UTC).</summary>
        public DateTime FirstDetectedAt { get; set; } = DateTime.UtcNow;

        /// <summary>When this drift was most recently detected (UTC).</summary>
        public DateTime LastDetectedAt { get; set; } = DateTime.UtcNow;

        /// <summary>The most recent sync run that touched this item.</summary>
        public int? LastSyncRunId { get; set; }

        /// <summary>When the item was resolved (applied/dismissed/auto-resolved), if it has been.</summary>
        public DateTime? ResolvedAt { get; set; }

        /// <summary>Who/what resolved the item ("admin" or "system:auto-resolve").</summary>
        [StringLength(100)]
        public string? ResolvedBy { get; set; }
    }
}
