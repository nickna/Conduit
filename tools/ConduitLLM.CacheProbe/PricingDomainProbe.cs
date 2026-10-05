using System.Text.Json;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.CacheProbe;

internal static class PricingDomainProbe
{
    public static async Task RunAsync(string mode, string? redis)
    {
        var environment = Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_ENVIRONMENT") ?? $"probe-{Guid.NewGuid():N}";
        using var host = DiscoveryDomainProbe.Host(redis, environment);
        var fusion = host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey);
        var options = host.GetRequiredService<ApplicationCacheOptions>();
        var generation = host.GetRequiredService<ApplicationCacheGeneration>();
        var policy = Options.Create(new CacheManagerOptions());
        var rules = new FusionPricingRulesService(fusion, options, generation, policy, NullLogger<FusionPricingRulesService>.Instance);
        var inner = new FixtureCostService();
        var costs = new FusionModelCostService(inner, fusion, options, generation, TimeProvider.System, policy,
            NullLogger<FusionModelCostService>.Instance, rules: rules);
        const string json = """{"defaultRate":0.25,"rules":[{"rate":0.5,"conditions":{"resolution":"1024x1024"}}]}""";
        if (mode == "pricing-read")
        {
            if (string.IsNullOrWhiteSpace(redis)) throw new InvalidOperationException("Restart read requires Redis.");
            inner.FailReads = true;
            var value = (await costs.GetCostByIdAsync(42))!;
            if (value.InputCostPerMillionTokens != 0.25m || value.ModelProviderTypeAssociations.Single().Model.Name != "probe")
                throw new InvalidOperationException("Cost snapshot failed native L2 graph read.");
            if ((await costs.ListModelCostsAsync()).Count != 1 || await costs.GetCostForModelAsync("missing") is not null || inner.Loads != 0)
                throw new InvalidOperationException("Cost positive/list/negative L2 contract failed.");
            var rule = (await rules.GetConfigAsync(42, json))!.Rules.Single();
            if (((JsonElement)rule.Conditions["resolution"]).GetString() != "1024x1024") throw new InvalidOperationException("Rule condition native L2 failed.");
            await costs.ClearCacheAsync(); await rules.InvalidateAllAsync();
            Console.WriteLine("PASS production pricing independent native L2 costs/list/missing/rules/graph/invalidation");
            return;
        }
        if (mode == "pricing-write")
        {
            await costs.GetCostByIdAsync(42); await costs.ListModelCostsAsync(); await costs.GetCostForModelAsync("missing");
            await rules.GetConfigAsync(42, json);
            Console.WriteLine("PASS production costs/rules written for native restart"); return;
        }
        inner.Barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 32).Select(_ => costs.GetCostByIdAsync(42)).ToArray();
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); inner.Barrier.SetResult();
        var values = await Task.WhenAll(requests);
        if (inner.Loads != 1) throw new InvalidOperationException("Cost factory bound failed.");
        values[0]!.InputCostPerMillionTokens = 99;
        if (values[1]!.InputCostPerMillionTokens != 0.25m) throw new InvalidOperationException("Cost ownership failed.");
        var parsed = (await rules.GetConfigAsync(42, json))!; parsed.Rules[0].Conditions.Clear();
        if ((await rules.GetConfigAsync(42, json))!.Rules[0].Conditions.Count != 1) throw new InvalidOperationException("Rule ownership failed.");
        if ((await rules.GetConfigAsync(42, json.Replace("0.25", "0.75")))!.DefaultRate != 0.75m)
            throw new InvalidOperationException("Rule content isolation failed.");
        await costs.UpdateModelCostAsync(new());
        if ((await costs.GetCostByIdAsync(42))!.InputCostPerMillionTokens != 0.75m) throw new InvalidOperationException("Repricing failed.");
        Console.WriteLine("PASS production pricing native 32 misses/one load/ownership/config variants/repricing");
    }

    private sealed class FixtureCostService : IModelCostService
    {
        public int Loads; public bool FailReads; public decimal Rate = 0.25m;
        public TaskCompletionSource? Barrier;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ModelCost Value() => new() { Id = 42, IsActive = true, EffectiveDate = DateTime.UtcNow.AddDays(-1), InputCostPerMillionTokens = Rate,
            ModelProviderTypeAssociations = [new() { Id = 1, ModelId = 2, Model = new() { Id = 2, Name = "probe" } }] };
        public async Task<ModelCost?> GetCostByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            Loads++; if (FailReads) throw new InvalidOperationException("Healthy L2 must not load.");
            Entered.TrySetResult(); if (Barrier is not null) await Barrier.Task.WaitAsync(cancellationToken);
            return Value();
        }
        public Task<ModelCost?> GetCostForModelAsync(string id, CancellationToken cancellationToken = default)
        { Loads++; if (FailReads) throw new InvalidOperationException("Healthy L2 must not load."); return Task.FromResult<ModelCost?>(null); }
        public Task<List<ModelCost>> ListModelCostsAsync(CancellationToken cancellationToken = default)
        { Loads++; if (FailReads) throw new InvalidOperationException("Healthy L2 must not load."); return Task.FromResult(new List<ModelCost> { Value() }); }
        public Task AddModelCostAsync(ModelCost value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> UpdateModelCostAsync(ModelCost value, CancellationToken cancellationToken = default) { Rate = 0.75m; return Task.FromResult(true); }
        public Task<bool> DeleteModelCostAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ClearCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
