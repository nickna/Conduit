using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Shared token-based pricing math used by the standard charge path, the refund path and the
/// tiered-tokens pricing model. Any change to a token rate rule must be made here so charging
/// and refunding can never diverge.
/// </summary>
public partial class CostCalculationService
{
    /// <summary>
    /// The effective per-unit rates for one calculation. The standard and refund paths build this
    /// directly from <see cref="ModelCost"/>; the tiered path substitutes the selected tier's
    /// input/output rates and omits the modalities it does not price (embedding, audio, TTS).
    /// </summary>
    private sealed record TokenPricingRates
    {
        public required decimal InputCostPerMillion { get; init; }
        public required decimal OutputCostPerMillion { get; init; }
        public decimal? EmbeddingCostPerMillion { get; init; }
        public decimal? CachedInputCostPerMillion { get; init; }
        public decimal? CachedWriteCostPerMillion { get; init; }
        public decimal? ReasoningCostPerMillion { get; init; }
        public decimal? CostPerThousandSearchUnits { get; init; }
        public decimal? AudioCostPerMinute { get; init; }
        public decimal? AudioCostPerThousandCharacters { get; init; }

        public static TokenPricingRates FromModelCost(ModelCost modelCost) => new()
        {
            InputCostPerMillion = modelCost.InputCostPerMillionTokens,
            OutputCostPerMillion = modelCost.OutputCostPerMillionTokens,
            EmbeddingCostPerMillion = modelCost.EmbeddingCostPerMillionTokens,
            CachedInputCostPerMillion = modelCost.CachedInputCostPerMillionTokens,
            CachedWriteCostPerMillion = modelCost.CachedInputWriteCostPerMillionTokens,
            ReasoningCostPerMillion = modelCost.ReasoningCostPerMillionTokens,
            CostPerThousandSearchUnits = modelCost.CostPerSearchUnit,
            AudioCostPerMinute = modelCost.AudioCostPerMinute,
            AudioCostPerThousandCharacters = modelCost.AudioCostPerThousandCharacters
        };
    }

    /// <summary>
    /// Per-component result of <see cref="ApplyTokenPricing"/>. Components map 1:1 onto the
    /// refund breakdown fields; <see cref="Total"/> is the charge (or refund) amount before any
    /// batch-processing discount.
    /// </summary>
    private sealed class TokenCostComponents
    {
        /// <summary>Embedding cost when the specialized embedding rate applies.</summary>
        public decimal EmbeddingCost { get; set; }

        /// <summary>Combined input cost: cached reads, cache writes and regular prompt tokens.</summary>
        public decimal InputTokenCost { get; set; }

        /// <summary>Non-reasoning completion token cost.</summary>
        public decimal OutputTokenCost { get; set; }

        /// <summary>Reasoning token cost (a subset of completion tokens, priced separately).</summary>
        public decimal ReasoningCost { get; set; }

        /// <summary>Search unit cost (rates are configured per 1K units).</summary>
        public decimal SearchUnitCost { get; set; }

        /// <summary>Audio transcription (speech-to-text) cost, billed per minute.</summary>
        public decimal AudioTranscriptionCost { get; set; }

        /// <summary>Text-to-speech cost, billed per thousand input characters.</summary>
        public decimal TtsCost { get; set; }

        public decimal Total =>
            EmbeddingCost + InputTokenCost + OutputTokenCost + ReasoningCost +
            SearchUnitCost + AudioTranscriptionCost + TtsCost;
    }

    /// <summary>
    /// Applies token-based pricing rules to <paramref name="usage"/> at the supplied rates.
    /// This is the single source of truth for the embedding-vs-token branch, cached read/write
    /// token accounting, the reasoning-subset convention (reasoning tokens are included in
    /// completion tokens and must not be billed twice), and the search/audio/TTS unit math.
    /// </summary>
    private TokenCostComponents ApplyTokenPricing(string modelId, Usage usage, TokenPricingRates rates)
    {
        var components = new TokenCostComponents();

        // For embeddings: prioritize embedding cost when available and no completion tokens
        if (rates.EmbeddingCostPerMillion.HasValue && usage.CompletionTokens.GetValueOrDefault() == 0 && usage.PromptTokens.HasValue)
        {
            // Use specialized embedding cost for prompt tokens (cost is per million tokens)
            components.EmbeddingCost = (usage.PromptTokens.Value * rates.EmbeddingCostPerMillion.Value) / 1_000_000m;
        }
        else
        {
            // Calculate input token costs, accounting for cached tokens
            var regularInputTokens = usage.PromptTokens.GetValueOrDefault();

            // Handle cached input tokens (read from cache)
            if (usage.CachedInputTokens is > 0 && rates.CachedInputCostPerMillion.HasValue)
            {
                // OpenAI includes cached tokens in prompt_tokens; Anthropic input_tokens excludes them.
                if (usage.CachedInputTokensIncludedInPrompt)
                    regularInputTokens -= usage.CachedInputTokens.Value;

                components.InputTokenCost += (usage.CachedInputTokens.Value * rates.CachedInputCostPerMillion.Value) / 1_000_000m;

                _logger.LogDebug("Applied cached input token pricing for {CachedTokens} tokens at rate {CachedRate}",
                    usage.CachedInputTokens.Value, rates.CachedInputCostPerMillion.Value);
            }

            // Handle cache write tokens
            if (usage.CachedWriteTokens is > 0 && rates.CachedWriteCostPerMillion.HasValue)
            {
                if (usage.CachedWriteTokensIncludedInPrompt)
                    regularInputTokens -= usage.CachedWriteTokens.Value;

                // Apply the full cache-write rate; included writes were removed from regular input above.
                components.InputTokenCost += (usage.CachedWriteTokens.Value * rates.CachedWriteCostPerMillion.Value) / 1_000_000m;

                _logger.LogDebug("Applied cache write token pricing for {WriteTokens} tokens at rate {WriteRate}",
                    usage.CachedWriteTokens.Value, rates.CachedWriteCostPerMillion.Value);
            }

            // Add cost for remaining regular input tokens (cost is per million tokens)
            if (regularInputTokens > 0)
            {
                components.InputTokenCost += (regularInputTokens * rates.InputCostPerMillion) / 1_000_000m;
            }
        }

        // Reasoning tokens are a subset of completion tokens (the OpenAI convention).
        // Price only the non-reasoning completion tokens here so the subset is not billed twice.
        var reasoningTokens = usage.ReasoningTokens.GetValueOrDefault();
        var regularCompletionTokens = Math.Max(0, usage.CompletionTokens.GetValueOrDefault() - reasoningTokens);
        if (regularCompletionTokens > 0)
        {
            components.OutputTokenCost = (regularCompletionTokens * rates.OutputCostPerMillion) / 1_000_000m;
        }

        // Reasoning token cost (per million tokens). This also handles providers that report
        // reasoning tokens without a completion total.
        if (reasoningTokens > 0)
        {
            // Use specific reasoning rate if available, otherwise fall back to output rate
            var reasoningRate = rates.ReasoningCostPerMillion ?? rates.OutputCostPerMillion;
            components.ReasoningCost = (reasoningTokens * reasoningRate) / 1_000_000m;

            _logger.LogDebug("Applied reasoning token pricing for {ReasoningTokens} tokens at rate {ReasoningRate}",
                reasoningTokens, reasoningRate);
        }

        // Search units (rates are configured per 1K units)
        if (usage.SearchUnits is > 0 && rates.CostPerThousandSearchUnits.HasValue)
        {
            var costPerUnit = rates.CostPerThousandSearchUnits.Value / 1000m;
            components.SearchUnitCost = usage.SearchUnits.Value * costPerUnit;

            _logger.LogDebug(
                "Search cost calculation for model {ModelId}: {Units} units × ${CostPerUnit} = ${Total}",
                modelId, usage.SearchUnits.Value, costPerUnit, components.SearchUnitCost);
        }

        // Audio transcription (speech-to-text), billed per minute of audio.
        if (usage.AudioDurationSeconds is > 0 && rates.AudioCostPerMinute.HasValue)
        {
            components.AudioTranscriptionCost = (BillingDurationConversion.ToDecimal(usage.AudioDurationSeconds.Value) / 60m) * rates.AudioCostPerMinute.Value;

            _logger.LogDebug(
                "Audio transcription cost for model {ModelId}: {Seconds}s = ${Total}",
                modelId, usage.AudioDurationSeconds.Value, components.AudioTranscriptionCost);
        }

        // Text-to-speech, billed per thousand input characters.
        if (usage.TtsCharacters is > 0 && rates.AudioCostPerThousandCharacters.HasValue)
        {
            components.TtsCost = (usage.TtsCharacters.Value / 1000m) * rates.AudioCostPerThousandCharacters.Value;

            _logger.LogDebug(
                "Text-to-speech cost for model {ModelId}: {Chars} chars = ${Total}",
                modelId, usage.TtsCharacters.Value, components.TtsCost);
        }

        return components;
    }
}
