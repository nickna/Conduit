using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.DTOs.VirtualKey
{
    /// <summary>
    /// DTO for requesting the creation of a new virtual key group
    /// </summary>
    public class CreateVirtualKeyGroupRequestDto
    {
        /// <summary>
        /// Human-readable name for the group
        /// </summary>
        [Required(ErrorMessage = "Group name is required.")]
        [StringLength(200, ErrorMessage = "Group name cannot exceed 200 characters.")]
        public string GroupName { get; set; } = string.Empty;

        /// <summary>
        /// External identifier for integration with external systems
        /// </summary>
        [StringLength(100, ErrorMessage = "External group ID cannot exceed 100 characters.")]
        public string? ExternalGroupId { get; set; }

        /// <summary>
        /// Initial balance to add to the group (in USD)
        /// </summary>
        [Range(0, 1000000000, ErrorMessage = "Initial balance must be between 0 and 1,000,000,000.")]
        public decimal? InitialBalance { get; set; }
    }
}