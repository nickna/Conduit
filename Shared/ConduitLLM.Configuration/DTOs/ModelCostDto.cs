using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ConduitLLM.Configuration.DTOs
{
    /// <summary>
    /// Data transfer object for model cost information
    /// </summary>
    public class ModelCostDto
    {
        /// <summary>
        /// Unique identifier for the model cost entry
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// User-friendly name for this cost configuration
        /// </summary>
        /// <remarks>
        /// Examples: "GPT-4 Standard Pricing", "Llama 3 Unified Cost", "Embedding Models - Ada"
        /// </remarks>
        [Required]
        [StringLength(255)]
        public string CostName { get; set; } = string.Empty;

        /// <summary>
        /// The pricing model type that determines how costs are calculated
        /// </summary>
        [Required]
        public PricingModel PricingModel { get; set; } = PricingModel.Standard;

        /// <summary>
        /// JSON configuration for complex pricing models
        /// </summary>
        /// <remarks>
        /// Structure depends on PricingModel:
        /// - PerVideo: {"rates": {"512p_6": 0.10, "720p_10": 0.15}}
        /// - PerSecondVideo: {"baseRate": 0.09, "resolutionMultipliers": {"720p": 1.0}}
        /// - InferenceSteps: {"costPerStep": 0.00013, "defaultSteps": 30}
        /// - TieredTokens: {"tiers": [{"maxContext": 200000, "inputCost": 400}]}
        /// </remarks>
        public Dictionary<string, JsonElement>? PricingConfiguration { get; set; }

        /// <summary>
        /// List of model aliases that use this cost configuration
        /// </summary>
        /// <remarks>
        /// This is populated from the ModelCostMappings relationship.
        /// Shows which models are associated with this cost configuration.
        /// </remarks>
        public List<string> AssociatedModelAliases { get; set; } = new List<string>();

        /// <summary>IDs of the model/provider associations that use this cost.</summary>
        public List<int> ModelProviderTypeAssociationIds { get; set; } = new List<int>();

        /// <summary>
        /// Cost per million input tokens for chat/completion requests in USD
        /// </summary>
        [Range(0, double.MaxValue)]
        public decimal InputCostPerMillionTokens { get; set; } = 0;

        /// <summary>
        /// Cost per million output tokens for chat/completion requests in USD
        /// </summary>
        [Range(0, double.MaxValue)]
        public decimal OutputCostPerMillionTokens { get; set; } = 0;

        /// <summary>
        /// Cost per million reasoning tokens in USD. When omitted, billing uses the output-token rate.
        /// </summary>
        [Range(0, double.MaxValue)]
        public decimal? ReasoningCostPerMillionTokens { get; set; }

        /// <summary>
        /// Cost per million tokens for embedding requests in USD, if applicable
        /// </summary>
        public decimal? EmbeddingCostPerMillionTokens { get; set; }

        /// <summary>
        /// Creation timestamp of this cost record
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Last update timestamp of this cost record
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Model type for categorization
        /// </summary>
        /// <remarks>
        /// Indicates the type of operations this model cost applies to (chat, embedding, image, video).
        /// </remarks>
        [Required]
        [StringLength(50)]
        public string ModelType { get; set; } = "chat";

        /// <summary>
        /// Indicates whether this cost configuration is active
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Effective date for this pricing
        /// </summary>
        public DateTime EffectiveDate { get; set; }

        /// <summary>
        /// Optional expiry date for this pricing
        /// </summary>
        public DateTime? ExpiryDate { get; set; }

        /// <summary>
        /// Optional description for this model cost entry
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }

        /// <summary>
        /// Priority value for this model cost entry
        /// </summary>
        /// <remarks>
        /// Higher priority patterns are evaluated first when matching model names.
        /// </remarks>
        public int Priority { get; set; }


        /// <summary>
        /// Cost multiplier for batch processing operations, if applicable
        /// </summary>
        /// <remarks>
        /// This represents a cost reduction factor for batch API usage.
        /// Example: 0.5 means 50% discount (half price), 0.6 means 40% discount.
        /// Applied to the standard token costs when requests are processed through batch APIs.
        /// </remarks>
        public decimal? BatchProcessingMultiplier { get; set; }

        /// <summary>
        /// Indicates if this model supports batch processing
        /// </summary>
        /// <remarks>
        /// When true, requests can be processed through batch endpoints with the BatchProcessingMultiplier discount applied.
        /// </remarks>
        public bool SupportsBatchProcessing { get; set; }

        /// <summary>
        /// Cost per million cached input tokens for prompt caching in USD, if applicable
        /// </summary>
        /// <remarks>
        /// This represents the cost for processing one million cached input tokens (reading from cache).
        /// Used by providers like Anthropic Claude and Google Gemini that offer prompt caching.
        /// Typically much lower than standard input token costs (e.g., 10% of regular cost).
        /// </remarks>
        public decimal? CachedInputCostPerMillionTokens { get; set; }

        /// <summary>
        /// Cost per million tokens for writing to the prompt cache in USD, if applicable
        /// </summary>
        /// <remarks>
        /// This represents the cost for writing one million tokens to the prompt cache.
        /// Used by providers like Anthropic Claude and Google Gemini that offer prompt caching.
        /// The write cost is incurred when new content is added to the cache.
        /// </remarks>
        public decimal? CachedInputWriteCostPerMillionTokens { get; set; }

        /// <summary>
        /// Cost per search unit for reranking models in USD per 1000 units, if applicable
        /// </summary>
        /// <remarks>
        /// Used by reranking models like Cohere Rerank that charge per search unit rather than per token.
        /// A search unit typically consists of 1 query + up to 100 documents to be ranked.
        /// Documents over 500 tokens are split into chunks, each counting as a separate document.
        /// </remarks>
        public decimal? CostPerSearchUnit { get; set; }

        /// <summary>Cost per minute of transcribed audio (speech-to-text).</summary>
        public decimal? AudioCostPerMinute { get; set; }

        /// <summary>Cost per thousand input characters synthesized (text-to-speech).</summary>
        public decimal? AudioCostPerThousandCharacters { get; set; }

    }
}
