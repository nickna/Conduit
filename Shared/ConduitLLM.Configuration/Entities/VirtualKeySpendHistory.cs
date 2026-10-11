using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using ConduitLLM.Configuration.Entities.Interfaces;

namespace ConduitLLM.Configuration.Entities
{
    /// <summary>
    /// Represents the spending history for a virtual key
    /// </summary>
    public class VirtualKeySpendHistory : IEntity<int>
    {
        /// <summary>
        /// Unique identifier for the spend history record
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// ID of the virtual key
        /// </summary>
        public int VirtualKeyId { get; set; }

        /// <summary>
        /// Foreign key relationship to the virtual key
        /// </summary>
        [ForeignKey("VirtualKeyId")]
        public virtual VirtualKey? VirtualKey { get; set; }

        /// <summary>
        /// The amount spent during this period
        /// </summary>
        [Column(TypeName = "decimal(12, 8)")]
        public decimal Amount { get; set; }

        /// <summary>
        /// The date this record is associated with
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// Timestamp for when this record was created (alias for backward compatibility)
        /// </summary>
        public DateTime Timestamp { get; set; }
    }
}
