using Prometheus;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Caching;

public static class ApplicationCacheMetrics
{
    private static readonly Counter Operations = Prometheus.Metrics.CreateCounter(
        "conduit_application_cache_operations_total", "Application cache operations and results.",
        new CounterConfiguration { LabelNames = ["domain", "operation", "result"] });
    private static readonly string[] Domains = ["discovery", "functions", "mappings", "costs", "rules", "internal"];
    private static readonly Dictionary<string, Counter.Child> Hits = Domains.ToDictionary(domain => domain, domain => Operations.WithLabels(domain, "read", "hit"));
    private static readonly Dictionary<string, Counter.Child> Misses = Domains.ToDictionary(domain => domain, domain => Operations.WithLabels(domain, "read", "miss"));

    public static void InvalidationFailed(ApplicationCacheDomain domain) =>
        Operations.WithLabels(ApplicationCacheOptions.Tag(domain), "invalidate", "error").Inc();

    public static void Bypassed(ApplicationCacheDomain domain) =>
        Operations.WithLabels(ApplicationCacheOptions.Tag(domain), "read", "bypass").Inc();

    internal static void RedisFailure(string operation) => Operations.WithLabels("shared", operation, "redis_error").Inc();

    private static string Domain(string key, string prefix)
    {
        var logical = key.AsSpan(key.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length : 0);
        var separator = logical.IndexOf(':');
        var domain = separator < 0 ? logical : logical[..separator];
        return domain switch { "discovery" => "discovery", "functions" => "functions", "mappings" => "mappings", "costs" => "costs", "rules" => "rules", _ => "internal" };
    }

    internal static void Attach(IFusionCache cache, string prefix)
    {
        cache.Events.Hit += (_, args) => Hits[Domain(args.Key, prefix)].Inc();
        cache.Events.Miss += (_, args) => Misses[Domain(args.Key, prefix)].Inc();
        cache.Events.FactorySuccess += (_, args) => Operations.WithLabels(Domain(args.Key, prefix), "load", "success").Inc();
        cache.Events.FactoryError += (_, args) =>
        {
            var domain = Domain(args.Key, prefix);
            Operations.WithLabels(domain, "load", domain == "internal" ? "metadata_error" : "business_error").Inc();
        };
        cache.Events.RemoveByTag += (_, args) => Operations.WithLabels(Domain(args.Tag, ""), "invalidate", "success").Inc();
        cache.Events.Distributed.SerializationError += (_, args) => Operations.WithLabels(Domain(args.Key, prefix), "serialize", "error").Inc();
        cache.Events.Distributed.DeserializationError += (_, args) => Operations.WithLabels(Domain(args.Key, prefix), "deserialize", "error").Inc();
        cache.Events.Backplane.MessagePublished += (_, _) => Operations.WithLabels("shared", "backplane_publish", "success").Inc();
        cache.Events.Backplane.MessageReceived += (_, _) => Operations.WithLabels("shared", "backplane_receive", "success").Inc();
    }
}
