namespace ConduitLLM.Core.Models
{
    /// <summary>
    /// Represents the result of a refund calculation operation.
    /// </summary>
    public class RefundResult
    {
        /// <summary>Server-only version of the guarded group snapshot.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? ResourceETag { get; set; }

        /// <summary>
        /// Gets or sets the model ID for which the refund was calculated.
        /// </summary>
        public string ModelId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the original usage data that was charged.
        /// </summary>
        public Usage OriginalUsage { get; set; } = new Usage { PromptTokens = 0, CompletionTokens = 0, TotalTokens = 0 };

        /// <summary>
        /// Gets or sets the usage data being refunded.
        /// </summary>
        public Usage RefundUsage { get; set; } = new Usage { PromptTokens = 0, CompletionTokens = 0, TotalTokens = 0 };

        /// <summary>
        /// Gets or sets the total refund amount (always positive).
        /// </summary>
        public decimal RefundAmount { get; set; }

        /// <summary>
        /// Gets or sets the original transaction ID if provided. This preserves the caller-supplied
        /// linkage to the charge being refunded and is never overwritten with the refund's own id.
        /// </summary>
        public string? OriginalTransactionId { get; set; }

        /// <summary>
        /// Gets or sets the id of the transaction record created for this refund (0 until persisted).
        /// </summary>
        public long RefundTransactionId { get; set; }

        /// <summary>Balance immediately after the refund was committed.</summary>
        public decimal BalanceAfter { get; set; }

        /// <summary>
        /// Gets or sets the reason for the refund.
        /// </summary>
        public string RefundReason { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp when the refund was calculated.
        /// </summary>
        public DateTime RefundedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets whether the refund was partially applied due to validation constraints.
        /// </summary>
        public bool IsPartialRefund { get; set; }

        /// <summary>
        /// Gets or sets the validation messages if any constraints were applied.
        /// </summary>
        public List<string> ValidationMessages { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the breakdown of the refund by component.
        /// </summary>
        public RefundBreakdown? Breakdown { get; set; }
    }

    /// <summary>
    /// Represents the breakdown of a refund by component.
    /// </summary>
    public class RefundBreakdown
    {
        /// <summary>
        /// Gets or sets the refund amount for input tokens.
        /// </summary>
        public decimal InputTokenRefund { get; set; }

        /// <summary>
        /// Gets or sets the refund amount for output tokens.
        /// </summary>
        public decimal OutputTokenRefund { get; set; }

        /// <summary>
        /// Gets or sets the refund amount for image generation.
        /// </summary>
        public decimal ImageRefund { get; set; }

        /// <summary>
        /// Gets or sets the refund amount for video generation.
        /// </summary>
        public decimal VideoRefund { get; set; }

        /// <summary>
        /// Gets or sets the refund amount for embeddings.
        /// </summary>
        public decimal EmbeddingRefund { get; set; }

        /// <summary>
        /// Gets or sets the refund amount for search units (reranking operations).
        /// </summary>
        public decimal SearchUnitRefund { get; set; }

        /// <summary>
        /// Gets or sets the refund amount for inference steps (image generation).
        /// </summary>
        public decimal InferenceStepRefund { get; set; }
    }
}
