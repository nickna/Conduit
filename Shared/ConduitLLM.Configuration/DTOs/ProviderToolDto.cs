using System.ComponentModel.DataAnnotations;
using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Configuration.DTOs
{
    /// <summary>
    /// Data transfer object for provider tool information.
    /// </summary>
    public class ProviderToolDto
    {
        /// <summary>
        /// Unique identifier for the tool.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Whether the tool is active and available for use.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Date the tool was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// The provider type that uses this tool.
        /// </summary>
        [Required]
        public ProviderType Provider { get; set; }

        /// <summary>
        /// The name of the tool being used.
        /// </summary>
        [Required]
        [StringLength(100)]
        public string ToolName { get; set; } = string.Empty;

        /// <summary>
        /// Optional parameters or settings for the tool.
        /// </summary>
        public string? ToolParameters { get; set; }

        /// <summary>
        /// Cost per usage unit (requests, hours, searches, etc.)
        /// </summary>
        [Range(0, 1000)]
        public decimal? CostPerUnit { get; set; }

        /// <summary>
        /// Unit type for billing (e.g., "requests", "hours", "searches")
        /// </summary>
        [StringLength(50)]
        public string? BillingUnit { get; set; }

        /// <summary>
        /// Optional cost description for admin reference.
        /// </summary>
        [StringLength(200)]
        public string? CostDescription { get; set; }

        /// <summary>
        /// Provider name for display purposes.
        /// </summary>
        public string? ProviderName { get; set; }

        /// <summary>
        /// Creates a DTO from a ProviderTool entity.
        /// </summary>
        public static ProviderToolDto FromEntity(ProviderTool entity)
        {
            return new ProviderToolDto
            {
                Id = entity.Id,
                IsActive = entity.IsActive,
                UpdatedAt = entity.UpdatedAt,
                Provider = entity.Provider ?? ProviderType.OpenAI,
                ToolName = entity.ToolName,
                ToolParameters = entity.ToolParameters,
                CostPerUnit = entity.CostPerUnit,
                BillingUnit = entity.BillingUnit,
                CostDescription = entity.CostDescription,
                ProviderName = entity.Provider?.ToString()
            };
        }

        /// <summary>
        /// Converts this DTO to a ProviderTool entity.
        /// </summary>
        public ProviderTool ToEntity()
        {
            return new ProviderTool
            {
                Id = Id,
                IsActive = IsActive,
                UpdatedAt = UpdatedAt,
                Provider = Provider,
                ToolName = ToolName,
                ToolParameters = ToolParameters,
                CostPerUnit = CostPerUnit,
                BillingUnit = BillingUnit,
                CostDescription = CostDescription
            };
        }
    }

    /// <summary>
    /// DTO for creating a new provider tool.
    /// </summary>
    public class CreateProviderToolDto
    {
        /// <summary>
        /// The provider type that uses this tool.
        /// </summary>
        [Required]
        public ProviderType Provider { get; set; }

        /// <summary>
        /// The name of the tool being used.
        /// </summary>
        [Required]
        [StringLength(100)]
        public string ToolName { get; set; } = string.Empty;

        /// <summary>
        /// Optional parameters or settings for the tool.
        /// </summary>
        public string? ToolParameters { get; set; }

        /// <summary>
        /// Cost per usage unit (requests, hours, searches, etc.)
        /// </summary>
        [Range(0, 1000)]
        public decimal? CostPerUnit { get; set; }

        /// <summary>
        /// Unit type for billing (e.g., "requests", "hours", "searches")
        /// </summary>
        [StringLength(50)]
        public string? BillingUnit { get; set; } = "requests";

        /// <summary>
        /// Optional cost description for admin reference.
        /// </summary>
        [StringLength(200)]
        public string? CostDescription { get; set; }

        /// <summary>
        /// Whether the tool is active and available for use.
        /// </summary>
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// DTO for updating an existing provider tool.
    /// </summary>
    public class UpdateProviderToolDto
    {
        /// <summary>
        /// Whether the tool is active and available for use.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Optional parameters or settings for the tool.
        /// </summary>
        public string? ToolParameters { get; set; }

        /// <summary>
        /// Cost per usage unit (requests, hours, searches, etc.)
        /// </summary>
        [Range(0, 1000)]
        public decimal? CostPerUnit { get; set; }

        /// <summary>
        /// Unit type for billing (e.g., "requests", "hours", "searches")
        /// </summary>
        [StringLength(50)]
        public string? BillingUnit { get; set; }

        /// <summary>
        /// Optional cost description for admin reference.
        /// </summary>
        [StringLength(200)]
        public string? CostDescription { get; set; }
    }
}