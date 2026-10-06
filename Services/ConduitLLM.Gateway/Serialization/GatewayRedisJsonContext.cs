using System.Text.Json.Serialization;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Gateway.Services;

namespace ConduitLLM.Gateway.Serialization;

/// <summary>
/// Source-generated metadata for persisted Gateway cache entries and Redis invalidations.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VirtualKey))]
[JsonSerializable(typeof(ModelCost))]
[JsonSerializable(typeof(CachedProvider))]
[JsonSerializable(typeof(CachedModelCost))]
[JsonSerializable(typeof(PerVideoPricingConfig))]
[JsonSerializable(typeof(PerSecondVideoPricingConfig))]
[JsonSerializable(typeof(InferenceStepsPricingConfig))]
[JsonSerializable(typeof(TieredTokensPricingConfig))]
[JsonSerializable(typeof(PerImagePricingConfig))]
[JsonSerializable(typeof(List<ProviderTool>))]
[JsonSerializable(typeof(RedisVirtualKeyCache.VirtualKeyBatchInvalidation))]
[JsonSerializable(typeof(RedisModelCostCache.ModelCostBatchInvalidation))]
internal partial class GatewayRedisJsonContext : JsonSerializerContext;
