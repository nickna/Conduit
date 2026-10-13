using ConduitLLM.Configuration.Utilities;
using System.Text.Json.Serialization;
namespace ConduitLLM.Configuration.DTOs.VirtualKey
{
    /// <summary>
    /// Data transfer object for refund processing results
    /// </summary>
    public class RefundResultDto : IVersionedRepresentation
    {
        /// <summary>Server-only version of the guarded group snapshot.</summary>
        [JsonIgnore]
        public string? ResourceETag { get; set; }

        /// <summary>
        /// The transaction ID created for this refund
        /// </summary>
        public long TransactionId { get; set; }

        /// <summary>
        /// The model ID for which the refund was calculated
        /// </summary>
        public string ModelId { get; set; } = string.Empty;

        /// <summary>
        /// The original usage data that was charged
        /// </summary>
        public UsageDto OriginalUsage { get; set; } = new UsageDto();

        /// <summary>
        /// The usage data that was refunded
        /// </summary>
        public UsageDto RefundUsage { get; set; } = new UsageDto();

        /// <summary>
        /// The total refund amount (always positive)
        /// </summary>
        public decimal RefundAmount { get; set; }

        /// <summary>
        /// The balance after the refund was applied
        /// </summary>
        public decimal BalanceAfter { get; set; }

        /// <summary>
        /// The original transaction ID if provided
        /// </summary>
        public string? OriginalTransactionId { get; set; }

        /// <summary>
        /// The reason for the refund
        /// </summary>
        public string RefundReason { get; set; } = string.Empty;

        /// <summary>
        /// Timestamp when the refund was processed
        /// </summary>
        public DateTime RefundedAt { get; set; }

        /// <summary>
        /// Whether the refund was partially applied due to validation constraints
        /// </summary>
        public bool IsPartialRefund { get; set; }

        /// <summary>
        /// Validation messages if any constraints were applied
        /// </summary>
        public List<string> ValidationMessages { get; set; } = new List<string>();

        /// <summary>
        /// Breakdown of the refund by component
        /// </summary>
        public RefundBreakdownDto? Breakdown { get; set; }
    }

    /// <summary>
    /// Data transfer object for refund breakdown by component.
    /// Kept as a transport-owned type so Configuration does not depend on Core.
    /// </summary>
    public class RefundBreakdownDto
    {
        /// <summary>
        /// Refund amount for input tokens
        /// </summary>
        public decimal InputTokenRefund { get; set; }

        /// <summary>
        /// Refund amount for output tokens
        /// </summary>
        public decimal OutputTokenRefund { get; set; }

        /// <summary>
        /// Refund amount for image generation
        /// </summary>
        public decimal ImageRefund { get; set; }

        /// <summary>
        /// Refund amount for video generation
        /// </summary>
        public decimal VideoRefund { get; set; }

        /// <summary>
        /// Refund amount for embeddings
        /// </summary>
        public decimal EmbeddingRefund { get; set; }

        /// <summary>
        /// Refund amount for search units (reranking operations)
        /// </summary>
        public decimal SearchUnitRefund { get; set; }

        /// <summary>
        /// Refund amount for inference steps (image generation)
        /// </summary>
        public decimal InferenceStepRefund { get; set; }
    }
}
