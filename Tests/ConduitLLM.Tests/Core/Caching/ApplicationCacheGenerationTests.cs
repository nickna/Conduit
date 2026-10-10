using ConduitLLM.Core.Caching;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Tests.Core.Caching;

public sealed class ApplicationCacheGenerationTests
{
    [SkippableTheory]
    [InlineData(ApplicationCacheDomain.Mappings)]
    [InlineData(ApplicationCacheDomain.Costs)]
    [InlineData(ApplicationCacheDomain.PricingRules)]
    public async Task RecoveryRemainsFencedUntilPublicationAndPreservesLaterFailure(ApplicationCacheDomain domain)
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrEmpty(redis), "Set CONDUIT_CACHE_TEST_REDIS for Redis contracts.");
        var options = new ApplicationCacheOptions
        {
            Environment = $"generation-{Guid.NewGuid():N}",
            DistributedReadTimeout = TimeSpan.Parse(RedisCacheTestReadiness.HealthyReadTimeout)
        };
        var fusionOptions = options.FusionOptions();
        fusionOptions.EnableSyncEventHandlersExecution = true;
        using var cache = new FusionCache(fusionOptions);
        cache.SetupSerializer(new ApplicationCacheSerializer());
        using var generations = new ApplicationCacheGeneration(cache, options, redis);
        var key = $"generation:{ApplicationCacheOptions.Tag(domain)}";
        var original = await RedisCacheTestReadiness.WarmAsync(generations, domain);
        // Keep the old local token fresh while the recovery factory is deliberately held before publication.
        var local = options.Entry(TimeSpan.FromSeconds(5));
        local.SkipDistributedCacheRead = local.SkipDistributedCacheWrite = true;
        local.EnableAutoClone = false;
        await cache.SetAsync(key, original, local);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 1;
        cache.Events.FactorySuccess += (_, args) =>
        {
            if (args.Key.EndsWith(key, StringComparison.Ordinal) && Interlocked.Exchange(ref armed, 0) == 1)
            {
                entered.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(10));
            }
        };
        generations.RecordStorageFailure(domain);
        var recovery = Task.Run(async () => await generations.GetAsync(domain));
        Task<string>? concurrent = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(original, cache.TryGet<string>(key).Value);
            concurrent = generations.GetAsync(domain).AsTask();
            Assert.False(concurrent.IsCompleted, "A caller must not accept the old L1 token before recovery publishes.");
            // The finishing recovery must not acknowledge a failure observed after it began.
            generations.RecordStorageFailure(domain);
        }
        finally { release.TrySetResult(); }
        var published = await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(original, published);
        if (concurrent is not null)
        {
            var concurrentPublished = await concurrent.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(concurrentPublished, await generations.GetAsync(domain));
        }
    }
}
