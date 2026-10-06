using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// Represents a media file (image or video) generated through Conduit.
    /// </summary>
    [Table("MediaRecords")]
    public class MediaRecord : IEntity<Guid>
    {
        /// <summary>
        /// Gets or sets the unique identifier for the media record.
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the storage key used to identify the file in the storage system.
        /// </summary>
        [Required]
        [StringLength(500)]
        public string StorageKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the ID of the virtual key that owns this media.
        /// </summary>
        [Required]
        public int VirtualKeyId { get; set; }

        /// <summary>
        /// Gets or sets the virtual key that owns this media.
        /// </summary>
        [ForeignKey(nameof(VirtualKeyId))]
        public virtual VirtualKey VirtualKey { get; set; } = null!;

        /// <summary>
        /// Gets or sets the type of media (image or video).
        /// </summary>
        [Required]
        [StringLength(50)]
        public string MediaType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MIME content type of the media.
        /// </summary>
        [StringLength(100)]
        public string? ContentType { get; set; }

        /// <summary>
        /// Gets or sets the size of the media file in bytes.
        /// </summary>
        public long? SizeBytes { get; set; }

        /// <summary>
        /// Gets or sets the hash of the media content for integrity verification.
        /// </summary>
        [StringLength(64)]
        public string? ContentHash { get; set; }

        /// <summary>
        /// Gets or sets the provider used to generate the media (e.g., OpenAI, MiniMax, Replicate).
        /// </summary>
        [StringLength(50)]
        public string? Provider { get; set; }

        /// <summary>
        /// Gets or sets the model used to generate the media (e.g., dall-e-3, minimax-image).
        /// </summary>
        [StringLength(100)]
        public string? Model { get; set; }

        /// <summary>
        /// Gets or sets the prompt used to generate the media.
        /// </summary>
        public string? Prompt { get; set; }

        /// <summary>
        /// Gets or sets the storage URL for direct access to the media file.
        /// </summary>
        public string? StorageUrl { get; set; }

        /// <summary>
        /// Gets or sets the public CDN URL for serving the media to end users.
        /// </summary>
        public string? PublicUrl { get; set; }

        /// <summary>
        /// Gets or sets when the media expires and should be deleted.
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// Gets or sets when the media record was created.
        /// </summary>
        [Required]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets when the media was last accessed.
        /// </summary>
        public DateTime? LastAccessedAt { get; set; }

        /// <summary>
        /// Gets or sets the number of times the media has been accessed.
        /// </summary>
        public int AccessCount { get; set; }

        /// <summary>
        /// Gets or sets when the media was soft deleted. Null means the media is active.
        /// Storage remains allocated until the purge phase removes this record.
        /// </summary>
        public DateTime? DeletedAt { get; set; }
    }
}
