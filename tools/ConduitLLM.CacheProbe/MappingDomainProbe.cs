using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.CacheProbe;

internal static class MappingDomainProbe
{
    internal static FusionModelProviderMappingService Service(ServiceProvider host, IModelProviderMappingService inner) => new(inner,
        host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey), host.GetRequiredService<ApplicationCacheOptions>(),
        host.GetRequiredService<ApplicationCacheGeneration>(), Invalidator(host), Options.Create(new CacheManagerOptions()),
        NullLogger<FusionModelProviderMappingService>.Instance);
    private static ModelMappingCacheInvalidator Invalidator(ServiceProvider host) => new(
        host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey), host.GetRequiredService<ApplicationCacheGeneration>());

    public static async Task RunAsync(string mode, string? redis)
    {
        var environment = Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_ENVIRONMENT") ?? $"probe-{Guid.NewGuid():N}";
        using var host = DiscoveryDomainProbe.Host(redis, environment);
        var inner = new FixtureMappingService();
        var cache = Service(host, inner);
        if (mode == "mappings-read")
        {
            if (string.IsNullOrWhiteSpace(redis)) throw new InvalidOperationException("Redis required for restart read.");
            inner.FailReads = true;
            AssertGraph((await cache.GetMappingByModelAliasAsync("probe-mapping"))!);
            if (inner.Loads != 0) throw new InvalidOperationException("Healthy L2 attempted a repair load.");
            await Invalidator(host).InvalidateAsync();
            Console.WriteLine("PASS production mappings independent complete L2 without repair and invalidation");
            return;
        }
        if (mode == "mappings-write")
        { AssertGraph((await cache.GetMappingByModelAliasAsync("probe-mapping"))!); Console.WriteLine("PASS production mapping written for restart"); return; }
        inner.Barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 32).Select(_ => cache.GetMappingByModelAliasAsync("probe-mapping")).ToArray();
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inner.Barrier.SetResult();
        var results = await Task.WhenAll(requests);
        if (inner.Loads != 1) throw new InvalidOperationException("Mapping load bound failed.");
        results[0]!.Provider.Settings!.Clear();
        results[0]!.ModelProviderTypeAssociation.Model.SupportsImageGeneration = false;
        AssertGraph(results[1]!);
        await Invalidator(host).InvalidateAsync();
        AssertGraph((await cache.GetMappingByModelAliasAsync("probe-mapping"))!);
        if (inner.Loads != 2) throw new InvalidOperationException("Mapping invalidation did not reload.");
        Console.WriteLine("PASS production mappings 32 misses/one load/complete graph/ownership/invalidation");
    }
    private static void AssertGraph(ModelProviderMapping value)
    {
        if (!value.ModelProviderTypeAssociation.Model.SupportsImageGeneration || value.Provider.Settings!["account"] != "fixture"
            || value.ModelProviderTypeAssociation.ModelCost!.InputCostPerMillionTokens != 0.25m
            || !value.ModelProviderTypeAssociation.Model.Parameters!.Contains("temperature", StringComparison.Ordinal))
            throw new InvalidOperationException("Mapping graph lost routing, pricing or inherited parameters.");
    }
    internal sealed class FixtureMappingService : IModelProviderMappingService
    {
        public int Loads;
        public bool FailReads;
        public TaskCompletionSource? Barrier;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModelProviderMapping?> GetMappingByModelAliasAsync(string alias)
        {
            Loads++; if (FailReads) throw new InvalidOperationException("L2 must not load");
            Entered.TrySetResult(); if (Barrier is not null) await Barrier.Task;
            return new() { ModelAlias = alias, Provider = new() { Settings = new() { ["account"] = "fixture" } },
                ModelProviderTypeAssociation = new() { Model = new() { SupportsImageGeneration = true, SupportsChat = true,
                    Series = new() { Parameters = """{"temperature":{"default":0.5}}""" } },
                    ModelCost = new() { InputCostPerMillionTokens = 0.25m } } };
        }
        public Task<ModelProviderMapping?> GetMappingByIdAsync(int id) => throw new NotSupportedException();
        public Task<List<ModelProviderMapping>> GetAllMappingsAsync() => throw new NotSupportedException();
        public Task<List<ModelProviderMapping>> GetMappingsByModelAliasAsync(string alias) => throw new NotSupportedException();
        public Task AddMappingAsync(ModelProviderMapping mapping) => throw new NotSupportedException();
        public Task UpdateMappingAsync(ModelProviderMapping mapping) => throw new NotSupportedException();
        public Task DeleteMappingAsync(int id) => throw new NotSupportedException();
        public Task<(bool success, string? errorMessage, ModelProviderMapping? createdMapping)> ValidateAndCreateMappingAsync(ModelProviderMapping mapping) => throw new NotSupportedException();
        public Task<(bool success, string? errorMessage)> ValidateAndUpdateMappingAsync(int id, ModelProviderMapping mapping) => throw new NotSupportedException();
        public Task<bool> ProviderExistsByIdAsync(int id) => throw new NotSupportedException();
        public Task<List<(int Id, string ProviderName)>> GetAvailableProvidersAsync() => throw new NotSupportedException();
    }
}
