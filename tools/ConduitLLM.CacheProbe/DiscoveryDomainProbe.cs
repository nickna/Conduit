using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.CacheProbe;

internal static class DiscoveryDomainProbe
{
    internal static ServiceProvider Host(string? redis, string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationCache:Environment"] = environment,
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddConduitApplicationCache(configuration, "probe", redis ?? "");
        services.AddDiscoveryCache(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public static async Task RunAsync(string mode, string? redis, DiscoveryModelsResult payload)
    {
        var environment = Environment.GetEnvironmentVariable("CONDUIT_CACHE_PROBE_ENVIRONMENT") ?? $"probe-{Guid.NewGuid():N}";
        using var writer = Host(redis, environment);
        var service = writer.GetRequiredService<IDiscoveryCacheService>();
        if (mode == "discovery-write")
        {
            await service.SetDiscoveryResultsAsync("all:with_pricing", payload);
            Console.WriteLine("PASS production discovery written for process restart");
            return;
        }
        if (mode == "discovery-read")
        {
            if (string.IsNullOrEmpty(redis)) throw new InvalidOperationException("Restart mode requires Redis.");
            var value = await service.GetOrLoadAsync("all:with_pricing", _ => throw new InvalidOperationException("A healthy L2 hit must not invoke the loader."));
            if (value.Data[0].GetProperty("pricing").GetProperty("input_cost").GetDecimal() != 0.25m)
                throw new InvalidOperationException("Discovery pricing did not survive restart.");
            await service.InvalidateAllDiscoveryAsync();
            using var restart = Host(redis, environment);
            if (await restart.GetRequiredService<IDiscoveryCacheService>().GetDiscoveryResultsAsync("all:with_pricing") is not null)
                throw new InvalidOperationException("Invalidated L2 entry resurrected after restart.");
            Console.WriteLine("PASS production discovery L2/restart read, no load, and persistent invalidation");
            return;
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        async Task<DiscoveryModelsResult> Load(CancellationToken token)
        {
            Interlocked.Increment(ref loads);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return payload;
        }
        var requests = Enumerable.Range(0, 32).Select(_ => service.GetOrLoadAsync("all:with_pricing", Load)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await Task.WhenAll(requests);
        if (loads != 1) throw new InvalidOperationException($"Discovery load bound failed: {loads}.");
        if (await service.GetDiscoveryResultsAsync("all") is not null) throw new InvalidOperationException("Priced data crossed cache variants.");
        await service.InvalidateAllDiscoveryAsync();
        if (await service.GetDiscoveryResultsAsync("all:with_pricing") is not null) throw new InvalidOperationException("Discovery dependency was not invalidated.");
        Console.WriteLine("PASS production discovery 32 misses/one load, pricing variants, and invalidation");
    }
}
