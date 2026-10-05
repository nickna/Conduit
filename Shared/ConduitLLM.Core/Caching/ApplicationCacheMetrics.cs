using Prometheus;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Caching;

public static class ApplicationCacheMetrics
{
    private static readonly Counter Operations = Prometheus.Metrics.CreateCounter(
        "conduit_application_cache_operations_total", "Application cache operations and results.",
        new CounterConfiguration { LabelNames = ["domain", "operation", "result"] });

    public static void InvalidationFailed(ApplicationCacheDomain domain) =>
        Operations.WithLabels(ApplicationCacheOptions.Tag(domain), "invalidate", "error").Inc();

    public static void Bypassed(ApplicationCacheDomain domain) =>
        Operations.WithLabels(ApplicationCacheOptions.Tag(domain), "read", "bypass").Inc();

    internal static void RedisFailure(string operation) => Operations.WithLabels("shared", operation, "redis_error").Inc();

    private static string Domain(string key, string prefix)
    {
        var logical = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
        var separator = logical.IndexOf(':');
        var domain = separator < 0 ? logical : logical[..separator];
        return domain is "discovery" or "functions" or "mappings" or "costs" or "rules" ? domain : "internal";
    }

    internal static void Attach(IFusionCache cache, string prefix)
    {
        cache.Events.Hit += (_, args) => Operations.WithLabels(Domain(args.Key, prefix), "read", "hit").Inc();
        cache.Events.Miss += (_, args) => Operations.WithLabels(Domain(args.Key, prefix), "read", "miss").Inc();
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
