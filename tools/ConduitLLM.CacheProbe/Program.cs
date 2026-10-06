using System.Text.Json;
using ConduitLLM.CacheProbe;
using ConduitLLM.Core.Interfaces;

if (JsonSerializer.IsReflectionEnabledByDefault) throw new InvalidOperationException("The cache probe requires JSON reflection disabled.");
var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_REDIS");
var payload = new DiscoveryModelsResult
{
    Count = 1, CapabilityFilter = "chat",
    Data = [JsonDocument.Parse("""{"id":"probe-model","capabilities":{"supports_chat":true},"pricing":{"input_cost":0.25}}""").RootElement.Clone()]
};
var mode = args.Length == 0 ? "all" : args.Single();
if (mode == "compose") await CompositionProbe.RunAsync(redis, payload);
else if (mode.StartsWith("discovery", StringComparison.Ordinal)) await DiscoveryDomainProbe.RunAsync(mode, redis, payload);
else if (mode.StartsWith("functions", StringComparison.Ordinal)) await FunctionDomainProbe.RunAsync(mode, redis);
else if (mode.StartsWith("mappings", StringComparison.Ordinal)) await MappingDomainProbe.RunAsync(mode, redis);
else if (mode.StartsWith("pricing", StringComparison.Ordinal)) await PricingDomainProbe.RunAsync(mode, redis);
else if (mode == "all")
{
    await DiscoveryDomainProbe.RunAsync("discovery", redis, payload);
    await FunctionDomainProbe.RunAsync("functions", redis);
    await MappingDomainProbe.RunAsync("mappings", redis);
    await PricingDomainProbe.RunAsync("pricing", redis);
    await CompositionProbe.RunAsync(redis, payload);
#if !CONDUIT_NATIVE_AOT
    if (Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_POSTGRES") is { Length: > 0 } postgres)
        await DatabaseBaseline.RunAsync(postgres);
#endif
}
else throw new ArgumentException($"Unknown cache probe mode: {mode}.");
Console.WriteLine("PASS production application-cache graph with JSON reflection disabled");
