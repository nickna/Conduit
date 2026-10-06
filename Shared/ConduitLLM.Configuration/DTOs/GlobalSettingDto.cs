using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.DTOs
{
    /// <summary>
    /// Data transfer object for global settings
    /// </summary>
    public class GlobalSettingDto
    {
        /// <summary>
        /// Unique identifier for the setting
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Setting key
        /// </summary>
        [Required]
        [StringLength(100)]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Setting value
        /// </summary>
        [Required]
        [StringLength(2000)]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Optional description of the setting
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }

        /// <summary>
        /// Date when the setting was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Date when the setting was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Data transfer object for creating a global setting
    /// </summary>
    public class CreateGlobalSettingDto
    {
        /// <summary>
        /// Setting key
        /// </summary>
        [Required]
        [StringLength(100)]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Setting value
        /// </summary>
        [Required]
        [StringLength(2000)]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Optional description of the setting
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }
    }

    /// <summary>
    /// Data transfer object for updating a global setting
    /// </summary>
    public class UpdateGlobalSettingDto
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public int Id { get; set; }
        /// <summary>
        /// Unique identifier for the setting
        /// </summary>
        /// <summary>
        /// Setting value
        /// </summary>
        [StringLength(2000)]
        public string? Value { get; set; }

        /// <summary>
        /// Optional description of the setting
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }
    }

    /// <summary>
    /// Data transfer object for updating a global setting by key
    /// </summary>
    public class UpdateGlobalSettingByKeyDto
    {
        /// <summary>
        /// Setting key
        /// </summary>
        [Required]
        [StringLength(100)]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Setting value
        /// </summary>
        [Required]
        [StringLength(2000)]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Optional description of the setting
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }
    }

}
