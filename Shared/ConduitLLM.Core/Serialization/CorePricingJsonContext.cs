using System.Text.Json.Serialization;

using ConduitLLM.Core.Models.Pricing;

namespace ConduitLLM.Core.Serialization;

/// <summary>
/// Source-generated metadata for the closed pricing-configuration shapes persisted
/// in <c>ModelCost.PricingConfiguration</c>.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PerVideoPricingConfig))]
[JsonSerializable(typeof(PerSecondVideoPricingConfig))]
[JsonSerializable(typeof(InferenceStepsPricingConfig))]
[JsonSerializable(typeof(TieredTokensPricingConfig))]
[JsonSerializable(typeof(PerImagePricingConfig))]
[JsonSerializable(typeof(PricingRulesConfig))]
[JsonSerializable(typeof(PricingRule))]
[JsonSerializable(typeof(Dictionary<string, ParameterDefinition>))]
internal partial class CorePricingJsonContext : JsonSerializerContext;
