using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ConduitLLM.Configuration.Entities
{
    public class ProviderTool
    {
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// Whether the tool is active and available for use.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Date the tool was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The provider type that uses this identifier.
        /// Maps to the ProviderType enum (OpenAI = 1, Groq = 2, etc.)
        /// Null indicates a universal identifier.
        /// </summary>
        [Required]
        public ProviderType? Provider { get; set; }

        /// <summary>
        /// The name of the tool being used.
        /// </summary>
        /// <example>browser_use</example>
        [Required]
        public required string ToolName { get; set; }

        /// <summary>
        /// Optional parameters or settings for the tool.
        /// </summary>
        public string? ToolParameters { get; set; }

        /// <summary>
        /// Cost per usage unit (requests, hours, searches, etc.)
        /// </summary>
        [Column(TypeName = "decimal(10, 6)")]
        public decimal? CostPerUnit { get; set; }

        /// <summary>
        /// Unit type for billing (e.g., "requests", "hours", "searches")
        /// </summary>
        [StringLength(50)]
        public string? BillingUnit { get; set; }

        /// <summary>
        /// Optional cost description for admin reference
        /// </summary>
        [StringLength(200)]
        public string? CostDescription { get; set; }

    }
}