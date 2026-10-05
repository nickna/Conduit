#if !CONDUIT_NATIVE_AOT
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.CacheProbe;

// Temporary gate harness: archived in Git before legacy retirement. All workloads use real domain services.
internal static class RolloutBenchmark
{
    public static async Task RunAsync(string redis, DiscoveryModelsResult payload)
    {
        var environment = $"rollout-{Guid.NewGuid():N}";
        var observerOptions = ConfigurationOptions.Parse(redis); observerOptions.AllowAdmin = true;
        using var observer = await ConnectionMultiplexer.ConnectAsync(observerOptions);
        var server = observer.GetServer(observer.GetEndPoints()[0]);
        var clientsBefore = Clients(server);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var storage = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = redis, InstanceName = $"conduit:legacy-gate:{environment}:" }));
        using var manager = new CacheManager(memory, storage, NullLogger<CacheManager>.Instance);
        var legacyDiscovery = new DiscoveryCacheService(Options.Create(new DiscoveryCacheOptions()), manager, NullLogger<DiscoveryCacheService>.Instance);
        var legacyInnerCost = new PricingDomainProbe.FixtureCostService();
        var legacyCosts = new CachedModelCostService(legacyInnerCost, manager, NullLogger<CachedModelCostService>.Instance);
        var legacyRules = new CachedPricingRulesService(manager, NullLogger<CachedPricingRulesService>.Instance);
        var legacyInnerMapping = new MappingDomainProbe.FixtureMappingService();
        var legacyMappings = new CachedModelProviderMappingService(legacyInnerMapping, manager, NullLogger<CachedModelProviderMappingService>.Instance);
        using var host = DiscoveryDomainProbe.Host(redis, environment);
        var fusion = host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey);
        var generation = host.GetRequiredService<ApplicationCacheGeneration>();
        var options = host.GetRequiredService<ApplicationCacheOptions>();
        var policy = Options.Create(new CacheManagerOptions());
        var discovery = host.GetRequiredService<IDiscoveryCacheService>();
        var costInner = new PricingDomainProbe.FixtureCostService();
        var costs = new FusionModelCostService(costInner, fusion, options, generation, TimeProvider.System, policy, NullLogger<FusionModelCostService>.Instance);
        var rules = new FusionPricingRulesService(fusion, options, generation, policy, NullLogger<FusionPricingRulesService>.Instance);
        var mappingInner = new MappingDomainProbe.FixtureMappingService(); var mappings = MappingDomainProbe.Service(host, mappingInner);
        var settings = new FunctionDomainProbe.EnabledSetting();
        IFunctionDiscoveryCacheService legacyFunctions = new FunctionDiscoveryCacheService(manager, new UnusedFunctions(), settings,
            NullLogger<FunctionDiscoveryCacheService>.Instance);
        var functions = new FusionFunctionDiscoveryCacheService(fusion, options, generation, null!, settings, policy, NullLogger<FusionFunctionDiscoveryCacheService>.Instance);
        List<Tool> tools = [new() { Function = new() { Name = "mcp__search", Parameters = JsonNode.Parse("""{"type":"object","required":["query"]}""")!.AsObject() } }];
        const string json = """{"defaultRate":0.25,"rules":[{"rate":0.5,"conditions":{"resolution":"1024x1024"}}]}""";
        var loads = 0;
        Task<DiscoveryModelsResult> Load(CancellationToken _) { loads++; return Task.FromResult(payload); }
        Func<Task> legacyD = async () => { if (await legacyDiscovery.GetDiscoveryResultsAsync("all:with_pricing") is null) await legacyDiscovery.SetDiscoveryResultsAsync("all:with_pricing", await Load(default)); };
        Func<Task> fusionD = async () => { await discovery.GetOrLoadAsync("all:with_pricing", Load); };
        Func<Task> legacyF = async () => { await legacyFunctions.GetOrLoadAsync([1, 2], _ => Task.FromResult(new FunctionDiscoveryLoad(tools, 2))); };
        Func<Task> fusionF = async () => { await functions.GetOrLoadAsync([1, 2], _ => Task.FromResult(new FunctionDiscoveryLoad(tools, 2))); };
        var workloads = new (string Domain, CacheRegion Region, string LegacyKey, ApplicationCacheDomain Kind, string Suffix, Func<Task> Old, Func<Task> New)[]
        {
            ("discovery", CacheRegion.ModelDiscovery, "all:with_pricing", ApplicationCacheDomain.Discovery, "all:with_pricing", legacyD, fusionD),
            ("functions", CacheRegion.FunctionDiscovery, "configs:1,2", ApplicationCacheDomain.Functions, "configs:1,2", legacyF, fusionF),
            ("mappings", CacheRegion.ModelMetadata, CacheKeys.ModelMapping.ByAlias("probe-mapping"), ApplicationCacheDomain.Mappings, CacheKeys.ModelMapping.ByAlias("probe-mapping"), async () => { await legacyMappings.GetMappingByModelAliasAsync("probe-mapping"); }, async () => { await mappings.GetMappingByModelAliasAsync("probe-mapping"); }),
            ("costs", CacheRegion.ModelCosts, CacheKeys.ModelCost.ById(42), ApplicationCacheDomain.Costs, CacheKeys.ModelCost.ById(42), async () => { await legacyCosts.GetCostByIdAsync(42); }, async () => { await costs.GetCostByIdAsync(42); }),
            ("rules", CacheRegion.PricingRules, CacheKeys.PricingRules.ById(42), ApplicationCacheDomain.PricingRules, $"42:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))}", async () => { await legacyRules.GetConfigAsync(42, json); }, async () => { await rules.GetConfigAsync(42, json); })
        };
        var localOnly = options.Entry(TimeSpan.FromMinutes(15));
        localOnly.SkipDistributedCacheWrite = true; localOnly.SkipBackplaneNotifications = true;
        foreach (var workload in workloads)
        {
            await Measure($"legacy {workload.Domain} cold", workload.Old, server, 1, 0);
            await Measure($"fusion {workload.Domain} cold", workload.New, server, 1, 0);
            await Measure($"legacy {workload.Domain} L1", workload.Old, server);
            await Measure($"fusion {workload.Domain} L1", workload.New, server);
            var prefix = workload.Kind == ApplicationCacheDomain.PricingRules ? "rules" : workload.Domain;
            var key = $"{prefix}:{await generation.GetAsync(workload.Kind)}:{workload.Suffix}";
            await Measure($"legacy {workload.Domain} L2", async () => { memory.Remove($"{workload.Region}:{workload.LegacyKey}"); await workload.Old(); }, server);
            await Measure($"fusion {workload.Domain} L2", async () => { await fusion.RemoveAsync(key, localOnly); await workload.New(); }, server);
        }
        Console.WriteLine($"LOADS discovery={loads} legacy_costs={legacyInnerCost.Loads} fusion_costs={costInner.Loads} legacy_mappings={legacyInnerMapping.Loads} fusion_mappings={mappingInner.Loads}");
        var legacyBytes = await Memory(server, observer, $"*conduit:legacy-gate:{environment}:*");
        var fusionBytes = await Memory(server, observer, $"*conduit:app-cache:{environment}:v1:*");
        Console.WriteLine($"RESOURCE legacy_entries={legacyBytes.Count} legacy_bytes={legacyBytes.Bytes} fusion_entries={fusionBytes.Count} fusion_bytes={fusionBytes.Bytes} added_clients={Clients(server) - clientsBefore} (includes legacy storage sockets; observer existed before count)");
        await discovery.InvalidateAllDiscoveryAsync(); await functions.InvalidateAllFunctionDiscoveryAsync(); await costs.ClearCacheAsync();
        await rules.InvalidateAllAsync();
        Console.WriteLine("PASS five-domain gate workloads, versioned ownership paths and required expiration; no shared Redis flush");
    }
    private static int Clients(IServer server) => int.Parse(server.Info("clients").SelectMany(group => group).Single(value => value.Key == "connected_clients").Value);
    private sealed class UnusedFunctions : IFunctionConfigurationRepository
    {
        public Task<FunctionConfiguration?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<FunctionConfiguration>> GetByIdsAsync(List<int> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FunctionConfiguration?> GetByNameAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<FunctionConfiguration>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<FunctionConfiguration>> GetAllUnboundedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<FunctionConfiguration> Items, int TotalCount)> GetPaginatedAsync(int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<FunctionConfiguration>> GetAllEnabledAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<FunctionConfiguration>> GetByProviderTypeAsync(FunctionProviderType type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<FunctionConfiguration>> GetByPurposeAsync(FunctionPurpose purpose, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CreateAsync(FunctionConfiguration value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(FunctionConfiguration value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> NameExistsAsync(string name, int? excludeId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private static async Task<(int Count, long Bytes)> Memory(IServer server, IConnectionMultiplexer observer, string pattern)
    {
        var count = 0; long bytes = 0;
        foreach (var key in server.Keys(pattern: pattern)) { count++; bytes += (long)await observer.GetDatabase().ExecuteAsync("MEMORY", "USAGE", key); }
        return (count, bytes);
    }
    private static async Task Measure(string label, Func<Task> operation, IServer server, int samples = 200, int warmup = 20)
    {
        for (var i = 0; i < warmup; i++) await operation();
        long Commands() => long.Parse(server.Info("stats").SelectMany(group => group).Single(value => value.Key == "total_commands_processed").Value);
        var before = Commands(); var allocation = GC.GetTotalAllocatedBytes(true); var times = new double[samples];
        for (var i = 0; i < samples; i++) { var start = Stopwatch.GetTimestamp(); await operation(); times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds; }
        var bytes = GC.GetTotalAllocatedBytes(true) - allocation; Array.Sort(times);
        Console.WriteLine($"MEASURE {label}: median_us={times[samples / 2]:F2} p95_us={times[(int)(samples * 0.95)]:F2} allocated_bytes_per_call={bytes / samples} redis_commands={Commands() - before} samples={samples} (includes INFO observer)");
    }
}
#endif
