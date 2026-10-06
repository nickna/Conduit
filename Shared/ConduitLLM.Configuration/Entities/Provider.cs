using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// Represents an external LLM provider.
    /// This is the main entity for managing provider configurations and serves as the parent
    /// for multiple API keys through the ProviderKeyCredentials collection.
    /// </summary>
    public class Provider : IEntity<int>, IAuditableEntity
    {
        /// <summary>
        /// Gets or sets the unique identifier for this provider.
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the provider type enum. This is the preferred way to identify providers.
        /// </summary>
        [Required]
        public ProviderType ProviderType { get; set; } = ProviderType.OpenAI;

        /// <summary>
        /// Gets or sets the user-friendly name for this provider instance.
        /// For example: "Production OpenAI", "Dev Azure OpenAI", "Nick's Ollama Server"
        /// </summary>
        [Required]
        [StringLength(100)]
        public string ProviderName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the base URL for the provider API. Optional - uses provider default if not specified.
        /// </summary>
        // Optional: Base URL if different from default
        public string? BaseUrl { get; set; }

        /// <summary>
        /// Gets or sets structured, provider-scoped settings the operator supplies in addition to the
        /// API key (for example a Cloudflare account ID). Keys correspond to the setting definitions
        /// declared for the provider type in the provider configuration registry. Stored as JSONB
        /// (mapped in <see cref="EntityConfigurations.ProviderEntityConfiguration"/>). Non-secret values only.
        /// </summary>
        public Dictionary<string, string>? Settings { get; set; }


        /// <summary>
        /// Gets or sets a value indicating whether this provider is enabled and available for use.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// When true, the cost the provider reports for each request (e.g. OpenRouter's usage.cost)
        /// is authoritative for billing virtual keys; ModelCost calculation is only a fallback when
        /// no cost is reported. Defaults to false (bill from ModelCost, the historical behavior).
        /// </summary>
        /// <remarks>
        /// Only enable for providers that return a trustworthy per-request charge. Note that for BYOK
        /// keys some providers report only their own fee, not the full upstream cost.
        /// </remarks>
        public bool TrustProviderReportedCosts { get; set; } = false;

        /// <summary>
        /// Multiplier applied to the provider-reported cost when billing (1.0 = pass-through markup).
        /// Only consulted when <see cref="TrustProviderReportedCosts"/> is true.
        /// </summary>
        public decimal ProviderCostMarkupMultiplier { get; set; } = 1.0m;

        /// <summary>
        /// Gets or sets the UTC timestamp when this provider was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Gets or sets the UTC timestamp when this provider was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the collection of API keys associated with this provider.
        /// This enables multi-key support for load balancing and failover scenarios.
        /// </summary>
        // Navigation property for the one-to-many relationship
        public ICollection<ProviderKeyCredential> ProviderKeyCredentials { get; set; } = new List<ProviderKeyCredential>();
    }
}
