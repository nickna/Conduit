using System.Text.Json;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Utilities;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Service implementation for various pricing model cost calculations
/// </summary>
public partial class CostCalculationService
{
    private Task<decimal> CalculatePerVideoCostAsync(string modelId, ModelCost modelCost, Usage usage)
    {
        if (!usage.VideoDurationSeconds.HasValue || string.IsNullOrEmpty(usage.VideoResolution))
        {
            _logger.LogDebug("No video usage data for per-video pricing model {ModelId}", modelId);
            return Task.FromResult(0m);
        }

        // Parse configuration from JSON
        PerVideoPricingConfig? config = null;
        if (!string.IsNullOrEmpty(modelCost.PricingConfiguration))
        {
            try
            {
                config = JsonSerializer.Deserialize(
                    modelCost.PricingConfiguration,
                    CorePricingJsonContext.Default.PerVideoPricingConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse per-video pricing configuration for model {ModelId}", modelId);
                throw new InvalidOperationException($"Invalid per-video pricing configuration for model {modelId}");
            }
        }

        if (config == null || config.Rates == null || !config.Rates.Any())
        {
            _logger.LogError("No per-video pricing rates configured for model {ModelId}", modelId);
            throw new InvalidOperationException($"No per-video pricing rates configured for model {modelId}");
        }

        // Build lookup key (e.g., "720p_6" for 720p resolution, 6 seconds).
        // Providers sometimes return measured durations (for example 6.2s) that do not exactly
        // match their discrete billable duration. Prefer the exact key, but never make a delivered
        // generation free merely because the measured value was slightly different.
        var duration = (int)Math.Round(usage.VideoDurationSeconds.Value);
        var resolution = VideoUtils.NormalizeResolution(usage.VideoResolution);
        var lookupKey = $"{resolution}_{duration}";

        if (!config.Rates.TryGetValue(lookupKey, out var flatRate))
        {
            // Use the highest rate for the requested resolution so an imprecise duration cannot
            // undercharge without unexpectedly applying another resolution's premium. If the
            // resolution itself is unknown, fall back to the highest configured rate overall.
            // The usage marker is consumed by the Gateway to produce a reconciliation/audit event.
            var resolutionPrefix = $"{resolution}_";
            var resolutionRates = config.Rates
                .Where(rate => rate.Key.StartsWith(resolutionPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            IEnumerable<KeyValuePair<string, decimal>> fallbackRates =
                resolutionRates.Count > 0 ? resolutionRates : config.Rates;
            var fallback = fallbackRates
                .OrderByDescending(rate => rate.Value)
                .First();
            flatRate = fallback.Value;
            usage.PricingFallbackReason =
                $"Missing per-video rate '{lookupKey}'; used conservative rate '{fallback.Key}' (${fallback.Value:F6})";

            _logger.LogError(
                "BILLING ALERT: No exact pricing found for video {Resolution} {Duration}s for model {ModelId}. " +
                "Using conservative fallback {FallbackKey} at ${FallbackRate:F6}",
                resolution, duration, modelId, fallback.Key, fallback.Value);
        }

        _logger.LogInformation("Video generation cost calculated: Model={ModelId}, Resolution={Resolution}, Duration={Duration}s, Cost=${Cost:F4}",
            modelId, usage.VideoResolution, duration, flatRate);

        return Task.FromResult(flatRate);
    }

    private Task<decimal> CalculatePerSecondVideoCostAsync(string modelId, ModelCost modelCost, Usage usage)
    {
        if (!usage.VideoDurationSeconds.HasValue)
        {
            _logger.LogDebug("No video duration for per-second video pricing model {ModelId}", modelId);
            return Task.FromResult(0m);
        }

        // Parse configuration from JSON
        PerSecondVideoPricingConfig? config = null;
        if (!string.IsNullOrEmpty(modelCost.PricingConfiguration))
        {
            try
            {
                config = JsonSerializer.Deserialize(
                    modelCost.PricingConfiguration,
                    CorePricingJsonContext.Default.PerSecondVideoPricingConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse per-second video pricing configuration for model {ModelId}", modelId);
                throw new InvalidOperationException($"Invalid per-second video pricing configuration for model {modelId}");
            }
        }

        if (config == null)
        {
            _logger.LogError("No per-second video pricing configuration for model {ModelId}", modelId);
            throw new InvalidOperationException($"No per-second video pricing configuration for model {modelId}");
        }

        var baseCost = (decimal)usage.VideoDurationSeconds.Value * config.BaseRate;

        // Apply resolution multiplier if configured. Unknown supplied resolutions use the
        // highest multiplier so a provider response cannot silently bypass the premium.
        if (!string.IsNullOrEmpty(usage.VideoResolution) &&
            config.ResolutionMultipliers is { Count: > 0 })
        {
            var resolution = VideoUtils.NormalizeResolution(usage.VideoResolution);
            if (!config.ResolutionMultipliers.TryGetValue(resolution, out var multiplier))
            {
                var fallback = config.ResolutionMultipliers.MaxBy(entry => entry.Value);
                multiplier = fallback.Value;
                usage.PricingFallbackReason =
                    $"Unknown per-second video resolution '{resolution}'; used conservative multiplier '{fallback.Key}' ({fallback.Value})";

                _logger.LogError(
                    "BILLING ALERT: Unknown per-second video resolution {Resolution} for model {ModelId}. " +
                    "Using conservative multiplier {FallbackResolution} ({Multiplier})",
                    resolution, modelId, fallback.Key, multiplier);
            }

            baseCost *= multiplier;
            _logger.LogDebug("Applied video resolution multiplier {Multiplier} for {Resolution}", multiplier, resolution);
        }

        _logger.LogInformation("Video generation cost calculated (per-second): Model={ModelId}, Duration={Duration}s, BaseRate=${BaseRate:F4}, Resolution={Resolution}, TotalCost=${Cost:F4}",
            modelId, usage.VideoDurationSeconds.Value, config.BaseRate, usage.VideoResolution ?? "default", baseCost);

        return Task.FromResult(baseCost);
    }

    private Task<decimal> CalculateInferenceStepsCostAsync(string modelId, ModelCost modelCost, Usage usage)
    {
        // Parse configuration or use pre-parsed
        InferenceStepsPricingConfig? config = null;
        if (!string.IsNullOrEmpty(modelCost.PricingConfiguration))
        {
            try
            {
                config = JsonSerializer.Deserialize(
                    modelCost.PricingConfiguration,
                    CorePricingJsonContext.Default.InferenceStepsPricingConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse inference steps pricing configuration for model {ModelId}", modelId);
                throw new InvalidOperationException($"Invalid inference steps pricing configuration for model {modelId}");
            }
        }

        if (config == null)
        {
            _logger.LogError("No inference steps pricing configuration for model {ModelId}", modelId);
            throw new InvalidOperationException($"No inference steps pricing configuration for model {modelId}");
        }

        // Use provided steps or default
        var steps = usage.InferenceSteps ?? config.DefaultSteps;
        if (steps <= 0)
        {
            _logger.LogDebug("No inference steps for model {ModelId}", modelId);
            return Task.FromResult(0m);
        }

        var cost = steps * config.CostPerStep;

        _logger.LogDebug("Inference steps cost for model {ModelId}: {Steps} steps × ${CostPerStep} = ${Cost}",
            modelId, steps, config.CostPerStep, cost);

        return Task.FromResult(cost);
    }

    private Task<decimal> CalculateTieredTokensCostAsync(string modelId, ModelCost modelCost, Usage usage)
    {
        // Parse configuration or use pre-parsed
        TieredTokensPricingConfig? config = null;
        if (!string.IsNullOrEmpty(modelCost.PricingConfiguration))
        {
            try
            {
                config = JsonSerializer.Deserialize(
                    modelCost.PricingConfiguration,
                    CorePricingJsonContext.Default.TieredTokensPricingConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse tiered tokens pricing configuration for model {ModelId}", modelId);
                throw new InvalidOperationException($"Invalid tiered tokens pricing configuration for model {modelId}");
            }
        }

        if (config == null || config.Tiers == null || !config.Tiers.Any())
        {
            _logger.LogError("No tiered tokens pricing configuration for model {ModelId}", modelId);
            throw new InvalidOperationException($"No tiered tokens pricing configuration for model {modelId}");
        }

        var inputTokens = usage.PromptTokens.GetValueOrDefault();
        var orderedTiers = config.Tiers.OrderBy(t => t.MaxContext ?? int.MaxValue).ToList();
        TokenPricingTier? tier = null;

        // Context-priced providers select the tier from the prompt/context presented to
        // the model. Generated output does not move a request into a higher input tier.
        foreach (var t in orderedTiers)
        {
            if (!t.MaxContext.HasValue || inputTokens <= t.MaxContext.Value)
            {
                tier = t;
                break;
            }
        }

        if (tier == null)
        {
            tier = orderedTiers.Last(); // Use the highest configured tier if none match
        }

        // Reuse the shared token-pricing math with the tier's input/output rates substituted.
        // Embedding, audio and TTS rates are intentionally omitted: tiered pricing does not
        // cover those modalities, so the helper skips them.
        var tierRates = new TokenPricingRates
        {
            InputCostPerMillion = tier.InputCost,
            OutputCostPerMillion = tier.OutputCost,
            CachedInputCostPerMillion = modelCost.CachedInputCostPerMillionTokens,
            CachedWriteCostPerMillion = modelCost.CachedInputWriteCostPerMillionTokens,
            ReasoningCostPerMillion = modelCost.ReasoningCostPerMillionTokens,
            CostPerThousandSearchUnits = modelCost.CostPerSearchUnit
        };
        var calculatedCost = ApplyTokenPricing(modelId, usage, tierRates).Total;

        _logger.LogDebug("Tiered tokens cost for model {ModelId}: Input context {InputTokens}, Tier ≤{MaxContext}, " +
            "Input rate ${InputRate}, Output rate ${OutputRate}, Total cost ${TotalCost}",
            modelId, inputTokens, tier.MaxContext, tier.InputCost, tier.OutputCost, calculatedCost);

        return Task.FromResult(calculatedCost);
    }

    private Task<decimal> CalculatePerImageCostAsync(string modelId, ModelCost modelCost, Usage usage)
    {
        if (!usage.ImageCount.HasValue || usage.ImageCount.Value <= 0)
        {
            _logger.LogDebug("No image count for per-image pricing model {ModelId}", modelId);
            return Task.FromResult(0m);
        }

        // Parse configuration or use pre-parsed
        PerImagePricingConfig? config = null;
        if (!string.IsNullOrEmpty(modelCost.PricingConfiguration))
        {
            try
            {
                config = JsonSerializer.Deserialize(
                    modelCost.PricingConfiguration,
                    CorePricingJsonContext.Default.PerImagePricingConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse per-image pricing configuration for model {ModelId}", modelId);
                throw new InvalidOperationException($"Invalid per-image pricing configuration for model {modelId}");
            }
        }

        if (config == null)
        {
            _logger.LogError("No per-image pricing configuration for model {ModelId}", modelId);
            throw new InvalidOperationException($"No per-image pricing configuration for model {modelId}");
        }

        var cost = usage.ImageCount.Value * config.BaseRate;

        // Apply quality multiplier
        if (!string.IsNullOrEmpty(usage.ImageQuality) && 
            config.QualityMultipliers != null &&
            config.QualityMultipliers.TryGetValue(usage.ImageQuality.ToLowerInvariant(), out var qualityMultiplier))
        {
            cost *= qualityMultiplier;
            _logger.LogDebug("Applied image quality multiplier {Multiplier} for {Quality}", qualityMultiplier, usage.ImageQuality);
        }

        // Apply resolution multiplier
        if (!string.IsNullOrEmpty(usage.ImageResolution) && 
            config.ResolutionMultipliers != null &&
            config.ResolutionMultipliers.TryGetValue(usage.ImageResolution, out var resolutionMultiplier))
        {
            cost *= resolutionMultiplier;
            _logger.LogDebug("Applied image resolution multiplier {Multiplier} for {Resolution}", resolutionMultiplier, usage.ImageResolution);
        }

        _logger.LogInformation("Image generation cost calculated: Model={ModelId}, Count={Count}, BaseRate=${BaseRate:F4}, Quality={Quality}, Resolution={Resolution}, TotalCost=${Cost:F4}",
            modelId, usage.ImageCount.Value, config.BaseRate, usage.ImageQuality ?? "standard", usage.ImageResolution ?? "default", cost);

        return Task.FromResult(cost);
    }

    private async Task<decimal> CalculateRulesBasedCostAsync(string modelId, ModelCost modelCost, Usage usage)
    {
        if (_pricingRulesEvaluator == null)
        {
            _logger.LogError("Rules-based pricing requested for model {ModelId} but no PricingRulesEvaluator is configured", modelId);
            throw new InvalidOperationException($"Rules-based pricing is not configured for model {modelId}");
        }

        // Get pricing rules configuration (preferably from cache)
        PricingRulesConfig? config = null;

        // Try to get from cache first
        if (_cachedPricingRulesService != null && !string.IsNullOrEmpty(modelCost.PricingConfiguration))
        {
            config = await _cachedPricingRulesService.GetConfigAsync(modelCost.Id, modelCost.PricingConfiguration);
            if (config != null)
            {
                _logger.LogDebug("Using cached pricing rules configuration for model {ModelId}", modelId);
            }
        }

        // Fallback to direct parsing if cache is not available or returned null
        if (config == null && !string.IsNullOrEmpty(modelCost.PricingConfiguration))
        {
            try
            {
                config = JsonSerializer.Deserialize(
                    modelCost.PricingConfiguration,
                    CorePricingJsonContext.Default.PricingRulesConfig);
                _logger.LogDebug("Parsed pricing rules configuration directly for model {ModelId}", modelId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse rules-based pricing configuration for model {ModelId}", modelId);
                throw new InvalidOperationException($"Invalid rules-based pricing configuration for model {modelId}");
            }
        }

        if (config == null)
        {
            _logger.LogError("No rules-based pricing configuration for model {ModelId}", modelId);
            throw new InvalidOperationException($"No rules-based pricing configuration for model {modelId}");
        }

        // Get pricing parameters from Usage (fallback to empty dictionary)
        var parameters = usage.PricingParameters ?? new Dictionary<string, object>();

        // Add any missing parameters from standard usage fields
        // This allows using rules-based pricing even when providers don't set PricingParameters
        if (!parameters.ContainsKey("resolution") && !string.IsNullOrEmpty(usage.VideoResolution))
        {
            parameters["resolution"] = VideoUtils.NormalizeResolution(usage.VideoResolution);
        }
        if (!parameters.ContainsKey("image_resolution") && !string.IsNullOrEmpty(usage.ImageResolution))
        {
            parameters["image_resolution"] = usage.ImageResolution;
        }
        if (!parameters.ContainsKey("image_quality") && !string.IsNullOrEmpty(usage.ImageQuality))
        {
            parameters["image_quality"] = usage.ImageQuality;
        }

        // Evaluate rules synchronously (audit logging is handled in UsageTrackingMiddleware if needed)
        var result = _pricingRulesEvaluator.Evaluate(config, parameters, usage);

        _logger.LogInformation(
            "Rules-based pricing evaluated: Model={ModelId}, Rate={Rate}, Qty={Quantity}, Cost=${Cost:F6}, Rule={Rule}, UsedDefault={UsedDefault}",
            modelId, result.Rate, result.Quantity, result.Cost,
            result.MatchedRule?.Description ?? "none", result.UsedDefaultRate);

        return result.Cost;
    }
}
