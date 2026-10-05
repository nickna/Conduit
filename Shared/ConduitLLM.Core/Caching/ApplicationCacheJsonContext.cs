using System.Text.Json.Serialization;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ZiggyCreatures.Caching.Fusion.Internals.Distributed;

namespace ConduitLLM.Core.Caching;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DiscoveryModelsResult))]
[JsonSerializable(typeof(List<Tool>))]
[JsonSerializable(typeof(List<MappingCacheSnapshot>))]
[JsonSerializable(typeof(FusionCacheDistributedEntry<List<MappingCacheSnapshot>>))]
[JsonSerializable(typeof(ModelCost))]
[JsonSerializable(typeof(List<ModelCost>))]
[JsonSerializable(typeof(PricingRulesConfig))]
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
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(double))]
internal partial class ApplicationCacheJsonContext : JsonSerializerContext;
