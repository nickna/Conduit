using ConduitLLM.Core.Caching;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Tests.Core.Caching;

internal static class RedisCacheTestReadiness
{
    internal const string HealthyReadTimeout = "00:00:01";

    // Call only after asserting automatic fencing without an explicit invalidation retry.
    internal static async Task RetryRecoveredInvalidationAsync(Func<CancellationToken, Task> invalidate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (;;)
        {
            try { await invalidate(deadline.Token).WaitAsync(deadline.Token); return; }
            catch (ApplicationCacheInvalidationException exception) when (exception.InnerException is RedisException or TimeoutException
                or FusionCacheDistributedCacheException or FusionCacheBackplaneException)
            { await Task.Delay(25, deadline.Token); }
        }
    }

    // Healthy-L2 assertions start after lazy connections, serialization and tag metadata are ready.
    // Retry infrastructure probes only; business loads and their call-count assertions are never retried.
    internal static async Task WarmAsync(IServiceProvider host, params ApplicationCacheDomain[] domains)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cache = host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey);
        var generation = host.GetRequiredService<ApplicationCacheGeneration>();
        var options = host.GetRequiredService<ApplicationCacheOptions>().Entry(TimeSpan.FromMinutes(1));
        var read = options.Duplicate();
        read.SkipMemoryCacheRead = true;
        var key = $"test-readiness:{Guid.NewGuid():N}";
        for (;;)
        {
            try
            {
                foreach (var domain in domains)
                    await generation.GetAsync(domain, deadline.Token);
                await cache.SetAsync(key, "ready", options, domains.Select(ApplicationCacheOptions.Tag), deadline.Token);
                var result = await cache.TryGetAsync<string>(key, read, deadline.Token);
                Assert.True(result.HasValue, "Redis readiness probe must round-trip through L2.");
                Assert.Equal("ready", result.Value);
                await cache.RemoveAsync(key, options, deadline.Token);
                return;
            }
            catch (Exception exception) when (exception is RedisException or TimeoutException
                or FusionCacheDistributedCacheException or FusionCacheBackplaneException)
            {
                await Task.Delay(25, deadline.Token);
            }
        }
    }
}
