using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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
internal partial class GatewayRedisJsonContext : JsonSerializerContext
{
    public static JsonTypeInfo<VirtualKey> CachedVirtualKey => CacheContract.TypeInfo;

    private static class CacheContract
    {
        internal static readonly JsonTypeInfo<VirtualKey> TypeInfo = CreateCachedVirtualKey();
    }

    private static JsonTypeInfo<VirtualKey> CreateCachedVirtualKey()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = Default.WithAddedModifier(info =>
            {
                // EF fixup creates cycles and attaches unrelated keys/logs. Persist only
                // authentication state and group policy, without traversing those graphs.
                foreach (var property in info.Properties)
                {
                    if ((info.Type == typeof(VirtualKeyGroup) && property.Name is
                            nameof(global::ConduitLLM.Configuration.Entities.VirtualKeyGroup.VirtualKeys) or nameof(global::ConduitLLM.Configuration.Entities.VirtualKeyGroup.Transactions) or nameof(global::ConduitLLM.Configuration.Entities.VirtualKeyGroup.MediaRetentionPolicy)) ||
                        (info.Type == typeof(VirtualKey) && property.Name is
                            nameof(global::ConduitLLM.Configuration.Entities.VirtualKey.RequestLogs) or nameof(global::ConduitLLM.Configuration.Entities.VirtualKey.SpendHistory) or nameof(global::ConduitLLM.Configuration.Entities.VirtualKey.Notifications) or nameof(global::ConduitLLM.Configuration.Entities.VirtualKey.IpFilters)))
                        property.ShouldSerialize = (_, _) => false;
                }
            })
        };
        options.MakeReadOnly();
        return (JsonTypeInfo<VirtualKey>)options.GetTypeInfo(typeof(VirtualKey));
    }
}
