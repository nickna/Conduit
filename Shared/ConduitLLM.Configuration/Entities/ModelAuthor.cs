using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities
{
    public class ModelAuthor : IEntity<int>
    {
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// The name of the model creator (e.g., OpenAI, Anthropic, etc.)
        /// </summary>
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Optional description of the model author/organization.
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }

        /// <summary>
        /// URL to the author's website or documentation.
        /// </summary>
        [StringLength(500)]
        public string? WebsiteUrl { get; set; }

        /// <summary>
        /// Navigation property for all model series by this author.
        /// </summary>
        /// <remarks>
        /// JsonIgnore is applied to prevent circular reference during serialization.
        /// The cycle is: ModelAuthor → ModelSeries → Author → ModelAuthor
        /// </remarks>
        [JsonIgnore]
        public virtual ICollection<ModelSeries> ModelSeries { get; set; } = new List<ModelSeries>();
    }
}