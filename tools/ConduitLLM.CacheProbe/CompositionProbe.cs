using System.Text;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.CacheProbe;

internal static class CompositionProbe
{
    public static async Task RunAsync(string? redis, DiscoveryModelsResult payload)
    {
        var environment = $"probe-{Guid.NewGuid():N}";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationCache:Environment"] = environment,
        }).Build();
        ServiceProvider Host(string host)
        {
            var services = new ServiceCollection().AddLogging().AddDistributedMemoryCache();
            services.AddConduitApplicationCache(configuration, host, redis ?? "");
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }

        using var admin = Host("Admin");
        using var gateway = Host("Gateway");
        var writer = admin.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey);
        var reader = gateway.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey);
        Check(writer.HasDistributedCache == !string.IsNullOrEmpty(redis), "registered Redis/local composition resolves");
        Check(admin.GetRequiredService<IDistributedCache>() is MemoryDistributedCache, "host distributed store remains unchanged");
        Check(admin.GetRequiredService<ApplicationCacheOptions>().Prefix == gateway.GetRequiredService<ApplicationCacheOptions>().Prefix,
            "Admin/Gateway share environment/version namespace");
        await writer.SetAsync("discovery:composed", payload, tags: ["discovery"]);
        if (string.IsNullOrEmpty(redis)) await reader.SetAsync("discovery:composed", payload, tags: ["discovery"]);
        var result = await reader.TryGetAsync<DiscoveryModelsResult>("discovery:composed");
        Check(result.HasValue && result.Value.Data[0].GetProperty("pricing").GetProperty("input_cost").GetDecimal() == 0.25m,
            "registered generated serializer reads complete discovery payload");
        result.Value.Count = 999;
        Check((await reader.TryGetAsync<DiscoveryModelsResult>("discovery:composed")).Value.Count == 1, "auto-cloned results preserve cached ownership");
        if (!string.IsNullOrEmpty(redis))
        {
            var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.Events.Backplane.MessageReceived += (_, _) => notification.TrySetResult();
            await writer.RemoveByTagAsync("discovery");
            await notification.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while ((await reader.TryGetAsync<DiscoveryModelsResult>("discovery:composed")).HasValue && deadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20);
            Check(!(await reader.TryGetAsync<DiscoveryModelsResult>("discovery:composed")).HasValue, "registered graph propagates cross-host tag invalidation");
        }

        try
        {
            await writer.GetOrSetAsync<string>("discovery:failed-load", (_, _) => Task.FromException<string>(new InvalidOperationException("business failure")));
            throw new InvalidOperationException("Factory failure was swallowed.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "business failure") { }
        using var output = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(output);
        var metrics = Encoding.UTF8.GetString(output.ToArray());
        Check(metrics.Contains("domain=\"discovery\",operation=\"load\",result=\"business_error\"", StringComparison.Ordinal), "business-load failure telemetry reaches existing metrics registry");
        Check(!metrics.Contains("failed-load", StringComparison.Ordinal) && !metrics.Contains(environment, StringComparison.Ordinal), "metrics labels exclude individual cache keys and environment IDs");
        Console.WriteLine("PASS registered application cache composition probe");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS {name}");
    }
}
