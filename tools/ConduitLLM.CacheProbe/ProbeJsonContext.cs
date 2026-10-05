using System.Text.Json.Serialization;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ZiggyCreatures.Caching.Fusion.Internals.Distributed;

namespace ConduitLLM.CacheProbe;

// FusionCache serializes the enclosing entry, not just its Value. Tag markers use long.
// All payloads here are real Conduit contracts; no reflection resolver is added.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(FusionCacheDistributedEntry<DiscoveryModelsResult>))]
[JsonSerializable(typeof(FusionCacheDistributedEntry<List<Tool>>))]
[JsonSerializable(typeof(FusionCacheDistributedEntry<ModelCost>))]
[JsonSerializable(typeof(FusionCacheDistributedEntry<List<ModelCost>>))]
[JsonSerializable(typeof(FusionCacheDistributedEntry<PricingRulesConfig>))]
[JsonSerializable(typeof(FusionCacheDistributedEntry<string>))]
[JsonSerializable(typeof(FusionCacheDistributedEntry<long>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(decimal))]
internal partial class ProbeJsonContext : JsonSerializerContext;
