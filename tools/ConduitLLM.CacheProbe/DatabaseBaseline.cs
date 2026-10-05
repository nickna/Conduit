#if !CONDUIT_NATIVE_AOT
using System.Data.Common;
using System.Diagnostics;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.CacheProbe;

// Uses a dedicated empty fixture database. No production data or schema is touched.
internal static class DatabaseBaseline
{
    public static async Task RunAsync(string connectionString)
    {
        var queries = new QueryCounter();
        var options = new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql(connectionString).AddInterceptors(queries).Options;
        IDbContextFactory<ConduitDbContext> factory = new ProbeContextFactory(options);
        await using (var seed = await factory.CreateDbContextAsync())
        {
            // This file is excluded entirely from a native build; schema creation is a JIT fixture operation.
#pragma warning disable IL3050
            await seed.Database.EnsureCreatedAsync();
#pragma warning restore IL3050
            if (!await seed.ModelProviderMappings.AnyAsync(mapping => mapping.ModelAlias == "cache-probe"))
            {
                var model = new Model
                {
                    Name = "Cache Probe", SupportsChat = true, SupportsImageGeneration = true,
                    Series = new ModelSeries { Name = "Cache Probe Series", Author = new ModelAuthor { Name = "Cache Probe Author" } }
                };
                seed.ModelProviderMappings.Add(new ModelProviderMapping
                {
                    ModelAlias = "cache-probe", ProviderModelId = "cache-probe", IsEnabled = true,
                    Provider = new Provider { ProviderName = "Cache Probe", ProviderType = ProviderType.OpenAI, IsEnabled = true },
                    ModelProviderTypeAssociation = new ModelProviderTypeAssociation
                    {
                        Model = model, Identifier = "cache-probe", Provider = ProviderType.OpenAI,
                        ModelCost = new ModelCost { CostName = "Cache Probe", IsActive = true, EffectiveDate = DateTime.UtcNow.AddDays(-1), InputCostPerMillionTokens = 0.25m }
                    }
                });
                await seed.SaveChangesAsync();
            }
        }

        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_REDIS");
        var environment = $"probe-{Guid.NewGuid():N}";
        using var fusionHost = DiscoveryDomainProbe.Host(redis, environment);
        var fusion = fusionHost.GetRequiredService<IDiscoveryCacheService>();
        async Task<DiscoveryModelsResult> FusionDiscoveryAsync() => await fusion.GetOrLoadAsync("all:with_pricing", async token =>
        {
            await using var context = await factory.CreateDbContextAsync(token);
            var projected = await DiscoveryModelProjector.ProjectAsync(context, "chat", true, NullLogger.Instance, token);
            if (projected.Count != 1) throw new InvalidOperationException("Unexpected discovery fixture shape.");
            return new DiscoveryModelsResult { Count = projected.Count, Data = [] };
        });
        await MeasureAsync("FusionCache discovery cold", FusionDiscoveryAsync, queries, expectedQueries: 1);
        await MeasureAsync("FusionCache discovery L1", FusionDiscoveryAsync, queries, expectedQueries: 0);
        var mappings = new ModelProviderMappingRepository(factory, NullLogger<ModelProviderMappingRepository>.Instance);
        var providers = new ProviderRepository(factory, NullLogger<ProviderRepository>.Instance);
        var inner = new ModelProviderMappingService(NullLogger<ModelProviderMappingService>.Instance, mappings, providers);
        var fusionMapping = MappingDomainProbe.Service(fusionHost, inner);
        async Task<ModelProviderMapping?> FusionMappingAsync()
        {
            var result = await fusionMapping.GetMappingByModelAliasAsync("cache-probe");
            if (result?.ModelProviderTypeAssociation.Model?.SupportsImageGeneration != true)
                throw new InvalidOperationException("Fusion mapping capability contract failed.");
            return result;
        }
        await MeasureAsync("FusionCache mapping cold", FusionMappingAsync, queries, expectedQueries: 1);
        await MeasureAsync("FusionCache mapping L1", FusionMappingAsync, queries, expectedQueries: 0);
        var costRepository = new ModelCostRepository(factory, NullLogger<ModelCostRepository>.Instance);
        var costInner = new ConduitLLM.Configuration.Services.ModelCostService(costRepository, mappings,
            NullLogger<ConduitLLM.Configuration.Services.ModelCostService>.Instance);
        var fusionCosts = PricingDomainProbe.Service(fusionHost, costInner);
        await MeasureAsync("FusionCache billing cold", () => fusionCosts.GetCostForModelAsync("cache-probe"), queries, expectedQueries: 2);
        await MeasureAsync("FusionCache billing L1", () => fusionCosts.GetCostForModelAsync("cache-probe"), queries, expectedQueries: 0);
        if (!string.IsNullOrEmpty(redis))
        {
            using var restartedHost = DiscoveryDomainProbe.Host(redis, environment);
            var restarted = restartedHost.GetRequiredService<IDiscoveryCacheService>();
            var restartedMapping = MappingDomainProbe.Service(restartedHost, inner);
            await MeasureAsync("FusionCache mapping restarted complete L2", async () =>
            {
                var result = await restartedMapping.GetMappingByModelAliasAsync("cache-probe");
                if (result?.ModelProviderTypeAssociation.Model?.SupportsImageGeneration != true)
                    throw new InvalidOperationException("Restarted mapping capability contract failed.");
                return result;
            }, queries, expectedQueries: 0);
            await MeasureAsync("FusionCache discovery restarted L2", () => restarted.GetOrLoadAsync("all:with_pricing",
                _ => throw new InvalidOperationException("Healthy L2 must not query the database.")), queries, expectedQueries: 0);
            var restartedCosts = PricingDomainProbe.Service(restartedHost, costInner);
            await MeasureAsync("FusionCache billing restarted L2", () => restartedCosts.GetCostForModelAsync("cache-probe"), queries, expectedQueries: 0);
        }
    }

    private static async Task MeasureAsync<T>(string label, Func<Task<T>> action, QueryCounter queries, int expectedQueries)
    {
        var before = queries.Count;
        var allocations = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        await action();
        var duration = Stopwatch.GetElapsedTime(start);
        var count = queries.Count - before;
        if (count != expectedQueries) throw new InvalidOperationException($"{label}: expected {expectedQueries} queries, got {count}.");
        Console.WriteLine($"BASELINE {label}: queries={count} elapsed_us={duration.TotalMicroseconds:F2} allocated_bytes={GC.GetTotalAllocatedBytes(true) - allocations}");
    }

    private sealed class ProbeContextFactory(DbContextOptions<ConduitDbContext> options) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(options);
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public long Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return ValueTask.FromResult(result);
        }
    }
}
#endif
