using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConduitLLM.CacheProbe;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("This probe must run with JSON reflection disabled.");

var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_REDIS");
var prefix = Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_PREFIX")
    ?? $"conduit:cache-probe:{Guid.NewGuid():N}:";
var serializer = new ApplicationCacheSerializer(ProbeJsonContext.Default);

FusionCacheEntryOptions EntryOptions() => new()
{
    Duration = TimeSpan.FromMinutes(1),
    DistributedCacheDuration = TimeSpan.FromMinutes(5),
    IsFailSafeEnabled = false,
    AllowTimedOutFactoryBackgroundCompletion = false,
    AllowBackgroundDistributedCacheOperations = false,
    AllowBackgroundBackplaneOperations = false,
    ReThrowSerializationExceptions = true,
    ReThrowDistributedCacheExceptions = true,
    ReThrowBackplaneExceptions = true
};

async Task<ProbeNode> NodeAsync()
{
    var cache = new FusionCache(new FusionCacheOptions
    {
        CacheName = "ConduitCacheProbe",
        CacheKeyPrefix = prefix,
        BackplaneChannelPrefix = prefix,
        WaitForInitialBackplaneSubscribe = true,
        EnableAutoRecovery = false,
        DistributedCacheCircuitBreakerDuration = TimeSpan.Zero,
        BackplaneCircuitBreakerDuration = TimeSpan.Zero,
        DefaultEntryOptions = EntryOptions(),
        // Markers must outlive every tagged entry. Explicitly turn their fail-safe off, too.
        TagsDefaultEntryOptions = new FusionCacheEntryOptions
        {
            Duration = TimeSpan.FromSeconds(1),
            DistributedCacheDuration = TimeSpan.FromMinutes(10),
            IsFailSafeEnabled = false,
            AllowBackgroundDistributedCacheOperations = false,
            AllowBackgroundBackplaneOperations = false,
            ReThrowSerializationExceptions = true,
            ReThrowDistributedCacheExceptions = true,
            ReThrowBackplaneExceptions = true
        }
    });
    if (string.IsNullOrEmpty(redis)) return new ProbeNode(cache, null);

    // Upstream RedisCache and RedisBackplane both dispose even a factory-supplied multiplexer.
    // Give each adapter its own connection; never hand either the host's auth/task connection.
    var distributed = new CountingCache(new RedisCache(Options.Create(new RedisCacheOptions
    {
        Configuration = redis,
        InstanceName = prefix
    })));
    cache.SetupDistributedCache(distributed, serializer);
    cache.SetupBackplane(new RedisBackplane(new RedisBackplaneOptions
    {
        Configuration = redis
    }));
    return new ProbeNode(cache, distributed);
}

void Check(bool condition, string contract)
{
    if (!condition) throw new InvalidOperationException(contract);
    Console.WriteLine($"PASS {contract}");
}

var payload = new DiscoveryModelsResult
{
    Data = [JsonDocument.Parse("""{"id":"probe-model","capabilities":{"supports_chat":true},"pricing":{"input_cost":0.25}}""").RootElement.Clone()],
    Count = 1,
    CachedAt = DateTime.UtcNow,
    CapabilityFilter = "chat"
};

if (args is ["discovery"] or ["discovery-write"] or ["discovery-read"])
{
    await DiscoveryDomainProbe.RunAsync(args[0], redis, payload);
    return;
}

if (args is ["functions"] or ["functions-write"] or ["functions-read"])
{
    await FunctionDomainProbe.RunAsync(args[0], redis);
    return;
}

if (args is ["mappings"] or ["mappings-write"] or ["mappings-read"])
{
    await MappingDomainProbe.RunAsync(args[0], redis);
    return;
}

if (args is ["compose"])
{
    await CompositionProbe.RunAsync(redis, payload);
    return;
}

if (args is ["pricing"] or ["pricing-write"] or ["pricing-read"])
{
    await PricingDomainProbe.RunAsync(args[0], redis);
    return;
}

if (args is ["write"] or ["read"])
{
    if (string.IsNullOrEmpty(redis)) throw new InvalidOperationException("Process round trips require Redis.");
    using var node = await NodeAsync();
    if (args[0] == "write")
        await node.Cache.SetAsync("restart", payload, tags: ["restart"]);
    else
    {
        var value = await node.Cache.TryGetAsync<DiscoveryModelsResult>("restart");
        Check(value.HasValue && value.Value.Data[0].GetProperty("pricing").GetProperty("input_cost").GetDecimal() == 0.25m,
            "new process reads complete discovery/pricing from L2");
    }
    return;
}

using var first = await NodeAsync();
Check(!JsonSerializer.IsReflectionEnabledByDefault, "reflection-disabled runtime");
try
{
    serializer.Serialize(new Uri("https://example.invalid/unregistered"));
    throw new InvalidOperationException("Unknown JSON contract was accepted.");
}
catch (NotSupportedException) { Check(true, "unknown JSON contracts fail without reflection fallback"); }
await first.Cache.SetAsync("discovery", payload, tags: ["discovery"]);
await first.Cache.SetAsync<string?>("missing", null, tags: ["costs"]);
await first.Cache.SetAsync("cost", new ModelCost { Id = 7, CostName = "probe", IsActive = true, InputCostPerMillionTokens = 0.25m }, tags: ["costs"]);
await first.Cache.SetAsync("cost-list", new List<ModelCost> { new() { Id = 7, CostName = "probe" } }, tags: ["costs"]);
await first.Cache.SetAsync("tools", new List<Tool>
{
    new() { Function = new FunctionDefinition { Name = "lookup", Parameters = JsonNode.Parse("""{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}""")!.AsObject() } }
}, tags: ["functions"]);
await first.Cache.SetAsync("rules", new PricingRulesConfig
{
    DefaultRate = 0.5m,
    Rules = [new PricingRule { Conditions = new() { ["resolution"] = "1080p", ["audio"] = true, ["steps"] = 12 }, Rate = 0.75m }]
}, tags: ["pricing"]);

using (var reader = await NodeAsync())
{
    // Independent L1 and independent Redis connection; an in-memory run deliberately populates this node.
    if (string.IsNullOrEmpty(redis))
    {
        await reader.Cache.SetAsync("discovery", payload, tags: ["discovery"]);
        await reader.Cache.SetAsync<string?>("missing", null, tags: ["costs"]);
    }
    var discovery = await reader.Cache.TryGetAsync<DiscoveryModelsResult>("discovery");
    Check(discovery.HasValue && discovery.Value.Data[0].GetProperty("capabilities").GetProperty("supports_chat").GetBoolean(), "discovery capability payload");
    var missing = await reader.Cache.TryGetAsync<string?>("missing");
    Check(missing.HasValue && missing.Value is null, "null is a cached result distinct from a miss");
    if (!string.IsNullOrEmpty(redis))
    {
        var tools = await reader.Cache.TryGetAsync<List<Tool>>("tools");
        Check(tools.HasValue && tools.Value[0].Function.Parameters!["required"]![0]!.GetValue<string>() == "id", "tool JsonObject schema survives L2");
        var rules = await reader.Cache.TryGetAsync<PricingRulesConfig>("rules");
        Check(rules.HasValue && ((JsonElement)rules.Value.Rules[0].Conditions["audio"]).GetBoolean(), "polymorphic pricing conditions survive L2");
        var cost = await reader.Cache.TryGetAsync<ModelCost>("cost");
        Check(cost.HasValue && cost.Value.InputCostPerMillionTokens == 0.25m, "model cost decimal survives L2");
        var list = await reader.Cache.TryGetAsync<List<ModelCost>>("cost-list");
        Check(list.HasValue && list.Value.Single().Id == 7, "model cost list survives L2");

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Cache.Events.Backplane.MessageReceived += (_, _) => received.TrySetResult();
        await first.Cache.RemoveByTagAsync("discovery");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = Stopwatch.StartNew();
        while ((await reader.Cache.TryGetAsync<DiscoveryModelsResult>("discovery")).HasValue && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        Check(!(await reader.Cache.TryGetAsync<DiscoveryModelsResult>("discovery")).HasValue, "independent warmed L1 invalidated by tag/backplane within five seconds");
        using var restarted = await NodeAsync();
        Check(!(await restarted.Cache.TryGetAsync<DiscoveryModelsResult>("discovery")).HasValue, "tag marker rejects obsolete L2 after restart");
        Check((await first.Cache.TryGetAsync<ModelCost>("cost")).HasValue, "disposing other cache nodes preserves independent connections");
    }
}

var loads = 0;
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var tasks = Enumerable.Range(0, 32).Select(_ => first.Cache.GetOrSetAsync<string>("coalesced", async (_, token) =>
{
    Interlocked.Increment(ref loads);
    entered.TrySetResult();
    await release.Task.WaitAsync(token);
    return "loaded";
}).AsTask()).ToArray();
await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
release.TrySetResult();
await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
Check(loads == 1 && tasks.All(task => task.Result == "loaded"), "32 simultaneous healthy misses execute one factory per cache instance");

using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await first.Cache.GetOrSetAsync<string>("cancelled", (_, token) => Task.FromResult("wrong"), token: cancellation.Token);
        throw new InvalidOperationException("Cancellation was swallowed.");
    }
    catch (OperationCanceledException) { Check(true, "caller cancellation propagates"); }
}

await first.Cache.SetAsync("measure", payload);
var observerOptions = string.IsNullOrEmpty(redis) ? null : ConfigurationOptions.Parse(redis);
// Read-only INFO/MEMORY instrumentation requires the client's AllowAdmin switch.
if (observerOptions is not null) observerOptions.AllowAdmin = true;
using var observer = observerOptions is null ? null : await ConnectionMultiplexer.ConnectAsync(observerOptions);
var measurementServer = observer?.GetServer(observer.GetEndPoints().Single());
await MeasureAsync("fusion L1", () => first.Cache.TryGetAsync<DiscoveryModelsResult>("measure").AsTask(), first.Distributed, measurementServer);
if (!string.IsNullOrEmpty(redis))
{
    var l2Options = EntryOptions();
    l2Options.SkipMemoryCacheRead = true;
    l2Options.SkipMemoryCacheWrite = true;
    await MeasureAsync("fusion L2", () => first.Cache.TryGetAsync<DiscoveryModelsResult>("measure", l2Options).AsTask(), first.Distributed, measurementServer);
#if !CONDUIT_NATIVE_AOT
    using var memory = new MemoryCache(new MemoryCacheOptions());
    using var legacy = new CacheManager(memory, first.Distributed, NullLogger<CacheManager>.Instance);
    await legacy.SetAsync("measure", payload, CacheRegion.ModelDiscovery);
    await MeasureAsync("legacy L1", () => legacy.GetAsync<DiscoveryModelsResult>("measure", CacheRegion.ModelDiscovery), first.Distributed, measurementServer);
    await MeasureAsync("legacy L2", async () =>
    {
        memory.Remove("ModelDiscovery:measure");
        return await legacy.GetAsync<DiscoveryModelsResult>("measure", CacheRegion.ModelDiscovery);
    }, first.Distributed, measurementServer);
    {
        var server = measurementServer!;
        var keys = server.Keys(pattern: $"{prefix}*measure*").ToArray();
        foreach (var key in keys)
        {
            var size = (long)await observer!.GetDatabase().ExecuteAsync("MEMORY", "USAGE", key.ToString());
            Console.WriteLine($"MEMORY {(key.ToString().Contains("ModelDiscovery:", StringComparison.Ordinal) ? "legacy" : "fusion")} discovery_entry_bytes={size}");
        }
        var clients = server.Info("clients").SelectMany(group => group).Single(value => value.Key == "connected_clients").Value;
        Console.WriteLine($"CONNECTIONS cache_node_multiplexers=2 redis_client_sockets={clients} (includes probe observer)");
    }
    await legacy.RemoveAsync("measure", CacheRegion.ModelDiscovery);
#endif
}
await first.Cache.RemoveByTagAsync(["costs", "pricing", "functions", "restart"]);
await first.Cache.RemoveAsync("measure");
await first.Cache.RemoveAsync("coalesced");
Console.WriteLine(string.IsNullOrEmpty(redis) ? "PASS cache-local composition; Redis checks not requested" : "PASS real Redis compatibility probe");
#if !CONDUIT_NATIVE_AOT
var postgres = Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_POSTGRES");
if (!string.IsNullOrEmpty(postgres))
{
    if (first.Distributed is null) throw new InvalidOperationException("Database baseline requires Redis.");
    await DatabaseBaseline.RunAsync(postgres, first.Distributed);
}
#endif

static async Task MeasureAsync<T>(string label, Func<Task<T>> operation, CountingCache? distributed, IServer? server)
{
    for (var i = 0; i < 20; i++) await operation();
    long Commands() => server is null ? 0 : long.Parse(server.Info("stats").SelectMany(group => group).Single(value => value.Key == "total_commands_processed").Value);
    var commands = Commands();
    var ops = distributed?.Operations ?? 0;
    var allocation = GC.GetTotalAllocatedBytes(true);
    var timings = new double[200];
    for (var i = 0; i < timings.Length; i++)
    {
        var start = Stopwatch.GetTimestamp();
        await operation();
        timings[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
    }
    var bytes = GC.GetTotalAllocatedBytes(true) - allocation;
    Array.Sort(timings);
    Console.WriteLine($"MEASURE {label}: median_us={timings[100]:F2} p95_us={timings[190]:F2} allocated_bytes_per_call={bytes / 200} distributed_calls={(distributed?.Operations ?? 0) - ops} redis_commands={Commands() - commands} samples=200 (Redis delta includes INFO observer)");
}

sealed record ProbeNode(FusionCache Cache, CountingCache? Distributed) : IDisposable
{
    public void Dispose()
    {
        Cache.Dispose();
        Distributed?.Dispose();
    }
}

sealed class CountingCache(IDistributedCache inner) : IDistributedCache, IDisposable
{
    public long Operations;
    public byte[]? Get(string key) { Interlocked.Increment(ref Operations); return inner.Get(key); }
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) { Interlocked.Increment(ref Operations); return inner.GetAsync(key, token); }
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { Interlocked.Increment(ref Operations); inner.Set(key, value, options); }
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) { Interlocked.Increment(ref Operations); return inner.SetAsync(key, value, options, token); }
    public void Remove(string key) { Interlocked.Increment(ref Operations); inner.Remove(key); }
    public Task RemoveAsync(string key, CancellationToken token = default) { Interlocked.Increment(ref Operations); return inner.RemoveAsync(key, token); }
    public void Refresh(string key) { Interlocked.Increment(ref Operations); inner.Refresh(key); }
    public Task RefreshAsync(string key, CancellationToken token = default) { Interlocked.Increment(ref Operations); return inner.RefreshAsync(key, token); }
    public void Dispose() => (inner as IDisposable)?.Dispose();
}
