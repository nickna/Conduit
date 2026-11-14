using System.Text.Json;

using ConduitLLM.Core.Models;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Service implementation for refund cost calculations
/// </summary>
public partial class CostCalculationService
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This implementation calculates refunds using the following logic:
    /// </para>
    /// <list type="number">
    ///   <item><description>Validates the refund request parameters</description></item>
    ///   <item><description>Ensures refund amounts don't exceed original amounts</description></item>
    ///   <item><description>Calculates refund based on the same pricing logic as charges</description></item>
    ///   <item><description>Returns a detailed refund result with breakdown</description></item>
    /// </list>
    /// <para>
    /// The method enforces validation rules to ensure data integrity:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Refund amounts cannot exceed original amounts</description></item>
    ///   <item><description>All usage values must be non-negative</description></item>
    ///   <item><description>Partial refunds are allowed and tracked</description></item>
    /// </list>
    /// </remarks>
    public async Task<RefundResult> CalculateRefundAsync(
        string modelId,
        Usage originalUsage,
        Usage refundUsage,
        string refundReason,
        string? originalTransactionId = null,
        ProviderCostRefundContext? providerCostContext = null,
        CancellationToken cancellationToken = default)
    {
        var result = new RefundResult
        {
            ModelId = modelId,
            OriginalUsage = originalUsage,
            RefundUsage = refundUsage,
            RefundReason = refundReason,
            OriginalTransactionId = originalTransactionId,
            RefundedAt = DateTime.UtcNow
        };

        // Validate inputs
        if (string.IsNullOrEmpty(modelId))
        {
            result.ValidationMessages.Add("Model ID is required for refund calculation.");
            return result;
        }

        if (originalUsage == null || refundUsage == null)
        {
            result.ValidationMessages.Add("Both original and refund usage data are required.");
            return result;
        }

        if (string.IsNullOrEmpty(refundReason))
        {
            result.ValidationMessages.Add("Refund reason is required.");
            return result;
        }

        // Validate refund amounts don't exceed original amounts.
        // A refund that exceeds the original charge (or contains negative usage) is invalid, so we
        // reject it here rather than computing a credit from the unclamped usage. RefundAmount stays
        // at 0, which callers (RefundService) treat as a validation failure and refuse to apply.
        var validationMessages = ValidateRefundAmounts(originalUsage, refundUsage);
        if (validationMessages.Any())
        {
            result.ValidationMessages.AddRange(validationMessages);
            _logger.LogWarning(
                "Refund rejected for model {ModelId}: refund usage exceeds original or is invalid. {ValidationMessages}",
                modelId, string.Join("; ", validationMessages));
            return result;
        }

        // The original debit is the authoritative historical charge. Recomputing from ModelCost would
        // use today's rates and the Standard token fields, which is incorrect after price changes and
        // for non-Standard pricing models. Prorate the recorded charge by the applicable usage unit.
        if (providerCostContext != null)
        {
            var ratio = ComputeRefundRatio(originalUsage, refundUsage);
            result.RefundAmount = decimal.Round(providerCostContext.OriginalChargedCost * ratio, 8);
            result.IsPartialRefund = ratio < 1m;
            result.Breakdown = new RefundBreakdown();
            _logger.LogInformation(
                "Calculated refund from original charge for model {ModelId}: charged {Charged} * ratio {Ratio} = {RefundAmount}. Reason: {RefundReason}. Original Transaction: {OriginalTransactionId}",
                modelId, providerCostContext.OriginalChargedCost, ratio, result.RefundAmount, refundReason, originalTransactionId ?? "N/A");
            return result;
        }

        // Get model cost information
        var modelCost = await _modelCostService.GetCostForModelAsync(modelId, cancellationToken);
        if (modelCost == null)
        {
            _logger.LogWarning("Cost information not found for model {ModelId} during refund calculation.", modelId);
            result.ValidationMessages.Add($"Cost information not found for model {modelId}.");
            return result;
        }

        // Calculate refund amount using the same token-pricing rules as charging. Reasoning,
        // audio and TTS refunds contribute to the total but have no dedicated breakdown field.
        var components = ApplyTokenPricing(modelId, refundUsage, TokenPricingRates.FromModelCost(modelCost));
        var breakdown = new RefundBreakdown
        {
            EmbeddingRefund = components.EmbeddingCost,
            InputTokenRefund = components.InputTokenCost,
            OutputTokenRefund = components.OutputTokenCost,
            SearchUnitRefund = components.SearchUnitCost
        };
        decimal totalRefund = components.Total;

        // Image, video and inference step refunds are handled via RulesBased pricing
        // configuration using the same rules engine as the original cost.

        // Apply batch processing discount if applicable
        if (refundUsage.IsBatch == true && modelCost.SupportsBatchProcessing && modelCost.BatchProcessingMultiplier.HasValue)
        {
            var originalRefund = totalRefund;
            totalRefund *= modelCost.BatchProcessingMultiplier!.Value;
            _logger.LogDebug("Applied batch processing discount to refund for model {ModelId}. Original refund: {OriginalRefund}, Discounted refund: {DiscountedRefund}, Multiplier: {Multiplier}",
                modelId, originalRefund, totalRefund, modelCost.BatchProcessingMultiplier.Value);
        }

        result.RefundAmount = totalRefund;
        result.Breakdown = breakdown;

        _logger.LogInformation(
            "Calculated refund for model {ModelId}: {RefundAmount}. Reason: {RefundReason}. Original Transaction: {OriginalTransactionId}",
            modelId, totalRefund, refundReason, originalTransactionId ?? "N/A");

        return result;
    }

    private List<string> ValidateRefundAmounts(Usage originalUsage, Usage refundUsage)
    {
        var messages = new List<string>();

        if (refundUsage.PromptTokens > originalUsage.PromptTokens)
        {
            messages.Add($"Refund prompt tokens ({refundUsage.PromptTokens}) cannot exceed original ({originalUsage.PromptTokens}).");
        }

        if (refundUsage.CompletionTokens > originalUsage.CompletionTokens)
        {
            messages.Add($"Refund completion tokens ({refundUsage.CompletionTokens}) cannot exceed original ({originalUsage.CompletionTokens}).");
        }

        if (refundUsage.ImageCount.HasValue && originalUsage.ImageCount.HasValue &&
            refundUsage.ImageCount.Value > originalUsage.ImageCount.Value)
        {
            messages.Add($"Refund image count ({refundUsage.ImageCount.Value}) cannot exceed original ({originalUsage.ImageCount.Value}).");
        }

        if (refundUsage.VideoDurationSeconds.HasValue && originalUsage.VideoDurationSeconds.HasValue &&
            refundUsage.VideoDurationSeconds.Value > originalUsage.VideoDurationSeconds.Value)
        {
            messages.Add($"Refund video duration ({refundUsage.VideoDurationSeconds.Value}s) cannot exceed original ({originalUsage.VideoDurationSeconds.Value}s).");
        }

        // Validate all values are non-negative
        if (refundUsage.PromptTokens < 0 || refundUsage.CompletionTokens < 0)
        {
            messages.Add("Refund token counts must be non-negative.");
        }

        if (refundUsage.ImageCount.HasValue && refundUsage.ImageCount.Value < 0)
        {
            messages.Add("Refund image count must be non-negative.");
        }

        if (refundUsage.VideoDurationSeconds.HasValue && refundUsage.VideoDurationSeconds.Value < 0)
        {
            messages.Add("Refund video duration must be non-negative.");
        }

        // Validate search unit refund amounts
        if (refundUsage.SearchUnits.HasValue && originalUsage.SearchUnits.HasValue &&
            refundUsage.SearchUnits.Value > originalUsage.SearchUnits.Value)
        {
            messages.Add($"Refund search units ({refundUsage.SearchUnits.Value}) cannot exceed original ({originalUsage.SearchUnits.Value}).");
        }

        if (refundUsage.SearchUnits.HasValue && refundUsage.SearchUnits.Value < 0)
        {
            messages.Add("Refund search units must be non-negative.");
        }

        // Validate audio refund amounts
        if (refundUsage.AudioDurationSeconds.HasValue && originalUsage.AudioDurationSeconds.HasValue &&
            refundUsage.AudioDurationSeconds.Value > originalUsage.AudioDurationSeconds.Value)
        {
            messages.Add($"Refund audio duration ({refundUsage.AudioDurationSeconds.Value}s) cannot exceed original ({originalUsage.AudioDurationSeconds.Value}s).");
        }

        if (refundUsage.AudioDurationSeconds is < 0)
        {
            messages.Add("Refund audio duration must be non-negative.");
        }

        if (refundUsage.TtsCharacters.HasValue && originalUsage.TtsCharacters.HasValue &&
            refundUsage.TtsCharacters.Value > originalUsage.TtsCharacters.Value)
        {
            messages.Add($"Refund TTS characters ({refundUsage.TtsCharacters.Value}) cannot exceed original ({originalUsage.TtsCharacters.Value}).");
        }

        if (refundUsage.TtsCharacters is < 0)
        {
            messages.Add("Refund TTS characters must be non-negative.");
        }

        // Validate inference steps refund amounts
        if (refundUsage.InferenceSteps.HasValue && originalUsage.InferenceSteps.HasValue &&
            refundUsage.InferenceSteps.Value > originalUsage.InferenceSteps.Value)
        {
            messages.Add($"Refund inference steps ({refundUsage.InferenceSteps.Value}) cannot exceed original ({originalUsage.InferenceSteps.Value}).");
        }

        if (refundUsage.InferenceSteps.HasValue && refundUsage.InferenceSteps.Value < 0)
        {
            messages.Add("Refund inference steps must be non-negative.");
        }

        return messages;
    }

    /// <summary>
    /// Computes the fraction of the recorded charge to refund using the request's billable usage
    /// dimension. The ordering distinguishes request types without consulting mutable pricing data.
    /// When there is no numeric usage basis (for example, a flat rules-based request), the refund is
    /// treated as a full refund because the original charge cannot be divided further.
    /// </summary>
    private static decimal ComputeRefundRatio(Usage originalUsage, Usage refundUsage)
    {
        var originalTokens = GetTotalTokensWithoutDoubleCountingReasoning(originalUsage);

        if (originalTokens > 0)
        {
            var refundTokens = GetTotalTokensWithoutDoubleCountingReasoning(refundUsage);
            return ClampRatio(refundTokens, originalTokens);
        }

        if (originalUsage.VideoDurationSeconds is > 0)
            return ClampRatio(
                BillingDurationConversion.ToDecimal(refundUsage.VideoDurationSeconds ?? 0),
                BillingDurationConversion.ToDecimal(originalUsage.VideoDurationSeconds.Value));

        if (originalUsage.InferenceSteps is > 0)
            return ClampRatio(refundUsage.InferenceSteps ?? 0, originalUsage.InferenceSteps.Value);

        if (originalUsage.ImageCount is > 0)
            return ClampRatio(refundUsage.ImageCount ?? 0, originalUsage.ImageCount.Value);

        if (originalUsage.SearchUnits is > 0)
            return ClampRatio(refundUsage.SearchUnits ?? 0, originalUsage.SearchUnits.Value);

        if (originalUsage.AudioDurationSeconds is > 0)
            return ClampRatio(
                BillingDurationConversion.ToDecimal(refundUsage.AudioDurationSeconds ?? 0),
                BillingDurationConversion.ToDecimal(originalUsage.AudioDurationSeconds.Value));

        if (originalUsage.TtsCharacters is > 0)
            return ClampRatio(refundUsage.TtsCharacters ?? 0, originalUsage.TtsCharacters.Value);

        return 1m;
    }

    private static decimal ClampRatio(decimal refundQuantity, decimal originalQuantity) =>
        Math.Clamp(refundQuantity / originalQuantity, 0m, 1m);

    private static int GetTotalTokensWithoutDoubleCountingReasoning(Usage usage) =>
        (usage.PromptTokens ?? 0) + Math.Max(usage.CompletionTokens ?? 0, usage.ReasoningTokens ?? 0);
}
