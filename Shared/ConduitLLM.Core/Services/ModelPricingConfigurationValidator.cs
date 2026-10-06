using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Configuration;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Validates persisted model pricing JSON before it can reach the billing path.
/// </summary>
public static class ModelPricingConfigurationValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Throws when a complex pricing model has malformed or unusable configuration.
    /// </summary>
    public static void Validate(PricingModel pricingModel, string? pricingConfiguration)
    {
        if (pricingModel == PricingModel.Standard)
            return;

        if (string.IsNullOrWhiteSpace(pricingConfiguration))
            throw new ArgumentException($"Pricing configuration is required for {pricingModel} pricing.");

        try
        {
            switch (pricingModel)
            {
                case PricingModel.PerVideo:
                    var perVideo = Deserialize<PerVideoPricingConfig>(pricingConfiguration);
                    if (perVideo.Rates == null || perVideo.Rates.Count == 0 || perVideo.Rates.Any(rate =>
                            string.IsNullOrWhiteSpace(rate.Key) || rate.Value <= 0))
                    {
                        throw new ArgumentException("Per-video pricing must contain at least one named rate greater than zero.");
                    }
                    ValidatePerVideoMonotonicity(perVideo);
                    break;

                case PricingModel.PerSecondVideo:
                    var perSecond = Deserialize<PerSecondVideoPricingConfig>(pricingConfiguration);
                    if (perSecond.BaseRate <= 0 || perSecond.ResolutionMultipliers?.Any(x => x.Value <= 0) == true)
                    {
                        throw new ArgumentException("Per-second video pricing requires a baseRate greater than zero and positive multipliers.");
                    }
                    break;

                case PricingModel.InferenceSteps:
                    var steps = Deserialize<InferenceStepsPricingConfig>(pricingConfiguration);
                    if (steps.CostPerStep <= 0 || steps.DefaultSteps <= 0)
                    {
                        throw new ArgumentException("Inference-step pricing requires costPerStep and defaultSteps greater than zero.");
                    }
                    break;

                case PricingModel.TieredTokens:
                    var tiered = Deserialize<TieredTokensPricingConfig>(pricingConfiguration);
                    if (tiered.Tiers == null || tiered.Tiers.Count == 0 || tiered.Tiers.Any(tier =>
                            tier.InputCost < 0 || tier.OutputCost < 0 || tier.MaxContext <= 0))
                    {
                        throw new ArgumentException("Tiered-token pricing requires at least one valid, non-negative tier.");
                    }
                    ValidateTierMonotonicity(tiered);
                    break;

                case PricingModel.PerImage:
                    var perImage = Deserialize<PerImagePricingConfig>(pricingConfiguration);
                    if (perImage.BaseRate <= 0 ||
                        perImage.QualityMultipliers?.Any(x => x.Value <= 0) == true ||
                        perImage.ResolutionMultipliers?.Any(x => x.Value <= 0) == true)
                    {
                        throw new ArgumentException("Per-image pricing requires a baseRate greater than zero and positive multipliers.");
                    }
                    break;

                case PricingModel.RulesBased:
                    var rules = Deserialize<PricingRulesConfig>(pricingConfiguration);
                    if (rules.DefaultRate <= 0)
                    {
                        throw new ArgumentException(
                            "Rules-based pricing requires a defaultRate greater than zero to safely price unmatched usage.");
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(pricingModel), pricingModel, "Unsupported pricing model.");
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid {pricingModel} pricing configuration JSON: {ex.Message}",
                nameof(pricingConfiguration), ex);
        }
    }

    private static T Deserialize<T>(string json) where T : class
    {
        var context = new CorePricingJsonContext(new JsonSerializerOptions(JsonOptions));
        var typeInfo = (JsonTypeInfo<T>?)context.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"Pricing contract '{typeof(T)}' is not registered.");
        return JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new ArgumentException($"Pricing configuration could not be parsed as {typeof(T).Name}.");
    }

    private static void ValidateTierMonotonicity(TieredTokensPricingConfig config)
    {
        var ordered = config.Tiers.OrderBy(tier => tier.MaxContext ?? int.MaxValue).ToList();
        if (ordered.Count(tier => tier.MaxContext == null) > 1 ||
            ordered.Take(Math.Max(0, ordered.Count - 1)).Any(tier => tier.MaxContext == null) ||
            ordered.Where(tier => tier.MaxContext.HasValue)
                .Select(tier => tier.MaxContext!.Value)
                .Distinct().Count() != ordered.Count(tier => tier.MaxContext.HasValue))
        {
            throw new ArgumentException("Tiered-token boundaries must be unique, with at most one unlimited final tier.");
        }

        for (var index = 1; index < ordered.Count; index++)
        {
            if (ordered[index].InputCost < ordered[index - 1].InputCost ||
                ordered[index].OutputCost < ordered[index - 1].OutputCost)
            {
                throw new ArgumentException("Tiered-token input and output rates must not decrease at higher context tiers.");
            }
        }
    }

    private static void ValidatePerVideoMonotonicity(PerVideoPricingConfig config)
    {
        var parsedRates = config.Rates.Select(rate =>
        {
            var separator = rate.Key.LastIndexOf('_');
            if (separator <= 0 || separator == rate.Key.Length - 1 ||
                !int.TryParse(rate.Key[(separator + 1)..], out var duration) || duration <= 0)
            {
                throw new ArgumentException(
                    $"Per-video rate key '{rate.Key}' must use '<resolution>_<positive duration seconds>'.");
            }

            return new { Resolution = rate.Key[..separator], Duration = duration, rate.Value };
        });

        foreach (var resolutionRates in parsedRates.GroupBy(rate => rate.Resolution, StringComparer.OrdinalIgnoreCase))
        {
            decimal? previous = null;
            foreach (var rate in resolutionRates.OrderBy(rate => rate.Duration))
            {
                if (previous.HasValue && rate.Value < previous.Value)
                {
                    throw new ArgumentException(
                        $"Per-video rates for resolution '{resolutionRates.Key}' must not decrease as duration increases.");
                }
                previous = rate.Value;
            }
        }
    }
}
