using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// Defines media retention policies based on account balance states.
    /// Enables flexible retention periods for different customer tiers and billing situations.
    /// </summary>
    public class MediaRetentionPolicy
    {
        /// <summary>
        /// Primary key for the retention policy.
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// Name of the retention policy (e.g., "Default", "Standard", "Enterprise").
        /// </summary>
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Description of the retention policy and its purpose.
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }

        /// <summary>
        /// Number of days to retain media when account balance is positive.
        /// Default: 60 days for standard accounts.
        /// </summary>
        public int PositiveBalanceRetentionDays { get; set; } = 60;

        /// <summary>
        /// Number of days to retain media when account balance is zero.
        /// Default: 14 days grace period.
        /// </summary>
        public int ZeroBalanceRetentionDays { get; set; } = 14;

        /// <summary>
        /// Number of days to retain media when account balance is negative.
        /// Default: 3 days for overdue accounts.
        /// </summary>
        public int NegativeBalanceRetentionDays { get; set; } = 3;

        /// <summary>
        /// Grace period in days before permanently deleting media after soft delete.
        /// Default: 7 days for recovery window.
        /// </summary>
        public int SoftDeleteGracePeriodDays { get; set; } = 7;

        /// <summary>
        /// Whether to respect recent access when determining cleanup eligibility.
        /// If true, recently accessed media won't be deleted even if retention period expired.
        /// </summary>
        public bool RespectRecentAccess { get; set; } = true;

        /// <summary>
        /// Number of days to consider as "recent" for access tracking.
        /// Only applies if RespectRecentAccess is true.
        /// </summary>
        public int RecentAccessWindowDays { get; set; } = 7;

        /// <summary>
        /// Indicates if this is the default policy for new virtual key groups.
        /// Only one policy should be marked as default.
        /// </summary>
        public bool IsDefault { get; set; } = false;

        /// <summary>
        /// Maximum storage size in bytes allowed under this policy.
        /// Null means no limit.
        /// </summary>
        public long? MaxStorageSizeBytes { get; set; }

        /// <summary>
        /// Maximum number of media files allowed under this policy.
        /// Null means no limit.
        /// </summary>
        public int? MaxFileCount { get; set; }

        /// <summary>
        /// Controls generation behavior when a configured storage quota would be exceeded.
        /// </summary>
        public MediaQuotaExceededBehavior QuotaExceededBehavior { get; set; } =
            MediaQuotaExceededBehavior.Reject;

        /// <summary>
        /// When this policy was created.
        /// </summary>
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When this policy was last updated.
        /// </summary>
        [Required]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether this policy is active and can be assigned to groups.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Navigation property to virtual key groups using this policy.
        /// </summary>
        public virtual ICollection<VirtualKeyGroup> VirtualKeyGroups { get; set; } = new HashSet<VirtualKeyGroup>();
    }
}
