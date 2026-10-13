using ConduitLLM.Configuration.Utilities;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.DTOs.VirtualKey
{
    /// <summary>
    /// Data Transfer Object representing a Virtual Key Group
    /// </summary>
    public class VirtualKeyGroupDto : IVersionedRepresentation
    {
        /// <summary>Version of the database snapshot represented by this response.</summary>
        [JsonIgnore]
        public string? ResourceETag { get; set; }

        /// <summary>
        /// Unique identifier for the virtual key group
        /// </summary>
        [Required] public int Id { get; set; }

        /// <summary>
        /// External identifier for integration with external systems
        /// </summary>
        public string? ExternalGroupId { get; set; }

        /// <summary>
        /// Human-readable name for the group
        /// </summary>
        [Required] public string GroupName { get; set; } = string.Empty;

        /// <summary>
        /// Current balance available in the group (in USD)
        /// </summary>
        [Required] public decimal Balance { get; set; }

        /// <summary>
        /// Total credits ever added to this group
        /// </summary>
        [Required] public decimal LifetimeCreditsAdded { get; set; }

        /// <summary>
        /// Total amount spent from this group
        /// </summary>
        [Required] public decimal LifetimeSpent { get; set; }

        /// <summary>
        /// Date and time when the group was created
        /// </summary>
        [Required] public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Date and time when the group was last updated
        /// </summary>
        [Required] public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Number of virtual keys in this group
        /// </summary>
        [Required] public int VirtualKeyCount { get; set; }

        /// <summary>
        /// Requests per minute shared by every key in this group, or null for no group ceiling.
        /// </summary>
        public int? RateLimitRpm { get; set; }

        /// <summary>
        /// Requests per day shared by every key in this group, or null for no group ceiling.
        /// </summary>
        public int? RateLimitRpd { get; set; }

        /// <summary>
        /// Tokens per minute shared by every key in this group, or null for no group ceiling.
        /// </summary>
        public int? RateLimitTpm { get; set; }

        /// <summary>
        /// Requests in flight at once across this group, or null for no group ceiling.
        /// </summary>
        public int? MaxParallelRequests { get; set; }

        /// <summary>
        /// Maps the entity to its DTO. Single mapping site so every endpoint returns the
        /// same shape — the rate-limit fields were previously set only by the update
        /// endpoint, so a PUT reported ceilings that the following GET omitted.
        /// </summary>
        public static VirtualKeyGroupDto FromEntity(Entities.VirtualKeyGroup group) => new()
        {
            Id = group.Id,
            ExternalGroupId = group.ExternalGroupId,
            GroupName = group.GroupName,
            Balance = group.Balance,
            LifetimeCreditsAdded = group.LifetimeCreditsAdded,
            LifetimeSpent = group.LifetimeSpent,
            CreatedAt = group.CreatedAt,
            UpdatedAt = group.UpdatedAt,
            ResourceETag = new ResourceVersion(group.RowVersion, group.UpdatedAt).ETag,
            VirtualKeyCount = group.VirtualKeys?.Count ?? 0,
            RateLimitRpm = group.RateLimitRpm,
            RateLimitRpd = group.RateLimitRpd,
            RateLimitTpm = group.RateLimitTpm,
            MaxParallelRequests = group.MaxParallelRequests
        };
    }
}
