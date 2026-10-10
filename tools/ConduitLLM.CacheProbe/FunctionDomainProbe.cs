using System.Text.Json.Nodes;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.CacheProbe;

internal static class FunctionDomainProbe
{
    public static async Task RunAsync(string mode, string? redis)
    {
        var environment = Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_ENVIRONMENT") ?? $"probe-{Guid.NewGuid():N}";
        using var host = DiscoveryDomainProbe.Host(redis, environment);
        var cache = new FusionFunctionDiscoveryCacheService(host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey),
            host.GetRequiredService<ApplicationCacheOptions>(), host.GetRequiredService<ApplicationCacheGeneration>(),
            new EnabledSetting(),  NullLogger<FusionFunctionDiscoveryCacheService>.Instance);
        Task<FunctionDiscoveryLoad> Load(CancellationToken _) => Task.FromResult(new FunctionDiscoveryLoad(
            [new Tool { Function = new FunctionDefinition { Name = "mcp__search", Parameters = JsonNode.Parse("""{"type":"object","required":["query"]}""")!.AsObject() } }], 2));
        if (mode == "functions-read")
        {
            if (string.IsNullOrWhiteSpace(redis)) throw new InvalidOperationException("Redis required for restart read.");
            var tools = await cache.GetOrLoadAsync([1, 2], _ => throw new InvalidOperationException("L2 must not load"));
            if (tools[0].Function.Parameters!["required"]![0]!.GetValue<string>() != "query") throw new InvalidOperationException("Schema lost after restart.");
            await cache.InvalidateFunctionConfigurationAsync(2);
            if (await cache.GetCachedToolsAsync([1, 2]) is not null) throw new InvalidOperationException("Combination remained current.");
            Console.WriteLine("PASS production functions independent L2/schema/invalidation");
            return;
        }
        if (mode == "functions-write")
        {
            await cache.GetOrLoadAsync([2, 1, 2], Load);
            Console.WriteLine("PASS production functions written for restart");
            return;
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        async Task<FunctionDiscoveryLoad> Factory(CancellationToken token)
        { Interlocked.Increment(ref loads); entered.TrySetResult(); await release.Task.WaitAsync(token); return await Load(token); }
        var requests = Enumerable.Range(0, 32).Select(_ => cache.GetOrLoadAsync([2, 1, 2], Factory)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        var results = await Task.WhenAll(requests);
        results[0][0].Function.Parameters!.Clear();
        if (loads != 1 || results[1][0].Function.Parameters!["type"]!.GetValue<string>() != "object") throw new InvalidOperationException("Load/ownership bound failed.");
        await cache.InvalidateAllFunctionDiscoveryAsync();
        if (await cache.GetCachedToolsAsync([1, 2]) is not null) throw new InvalidOperationException("Function invalidation failed.");
        Console.WriteLine("PASS production functions 32 misses/one load/schema ownership/invalidation");
    }

    // Native fixture: only the enable lookup is exercised. Repository business operations are never emulated.
    internal sealed class EnabledSetting : IGlobalSettingRepository
    {
        public Task<GlobalSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<GlobalSetting?>(new() { Key = key, Value = "true" });
        public Task<GlobalSetting?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CreateAsync(GlobalSetting entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(GlobalSetting entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GlobalSetting>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpsertAsync(string key, string value, string? description = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteByKeyAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
