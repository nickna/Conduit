using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.DTOs.VirtualKey
{
    /// <summary>
    /// DTO for requesting updates to an existing virtual key group
    /// </summary>
    public class UpdateVirtualKeyGroupRequestDto
    {
        /// <summary>
        /// Human-readable name for the group
        /// </summary>
        [StringLength(200, ErrorMessage = "Group name cannot exceed 200 characters.")]
        public string? GroupName { get; set; }

        /// <summary>
        /// External identifier for integration with external systems
        /// </summary>
        [StringLength(100, ErrorMessage = "External group ID cannot exceed 100 characters.")]
        public string? ExternalGroupId { get; set; }

        /// <summary>
        /// Requests per minute shared by every key in the group. Null leaves it unchanged.
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "Requests per minute must be positive.")]
        public int? RateLimitRpm { get; set; }

        /// <summary>
        /// Requests per day shared by every key in the group. Null leaves it unchanged.
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "Requests per day must be positive.")]
        public int? RateLimitRpd { get; set; }

        /// <summary>
        /// Tokens per minute shared by every key in the group. Null leaves it unchanged.
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "Tokens per minute must be positive.")]
        public int? RateLimitTpm { get; set; }

        /// <summary>
        /// Requests in flight at once across the group. Null leaves it unchanged.
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "Max parallel requests must be positive.")]
        public int? MaxParallelRequests { get; set; }
    }
}