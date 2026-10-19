using ConduitLLM.Core.Caching;
using Moq;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Tests.Core.Caching;

// This test deliberately blocks a synchronous publication callback. Run it apart
// from unrelated collections so CI thread-pool contention cannot cause Redis timeouts.
[CollectionDefinition("Application cache generation publication", DisableParallelization = true)]
public sealed class ApplicationCacheGenerationCollection;

[Collection("Application cache generation publication")]
public sealed class ApplicationCacheGenerationTests
{
    /// <summary>Uses the existing L1 entry and reloads only after local invalidation.</summary>
    [Fact]
    public async Task WarmL1HitSkipsFactoryAndLocalInvalidationReloads()
    {
        var options = new ApplicationCacheOptions();
        var fusionOptions = options.FusionOptions();
        fusionOptions.EnableSyncEventHandlersExecution = true;
        using var cache = new FusionCache(fusionOptions);
        using var generations = new ApplicationCacheGeneration(cache, options, null);
        var key = "generation:mappings";
        await cache.SetAsync(key, "warm", options.Entry(TimeSpan.FromSeconds(5)));
        var loads = 0;
        cache.Events.FactorySuccess += (_, args) => { if (args.Key.EndsWith(key, StringComparison.Ordinal)) loads++; };

        var hit = generations.GetAsync(ApplicationCacheDomain.Mappings);
        Assert.True(hit.IsCompletedSuccessfully);
        Assert.Equal("warm", await hit);
        Assert.Equal(0, loads);

        await generations.InvalidateAsync(ApplicationCacheDomain.Mappings);
        Assert.NotEqual("warm", await generations.GetAsync(ApplicationCacheDomain.Mappings));
        Assert.Equal(1, loads);
    }

    /// <summary>A later publication-time failure remains fenced even with a fresh old L1 token.</summary>
    [Fact]
    public async Task LocalRecoveryBypassesWarmL1AndPreservesLaterFailure()
    {
        var options = new ApplicationCacheOptions();
        var fusionOptions = options.FusionOptions();
        fusionOptions.EnableSyncEventHandlersExecution = true;
        using var cache = new FusionCache(fusionOptions);
        using var generations = new ApplicationCacheGeneration(cache, options, null);
        var key = "generation:mappings";
        await cache.SetAsync(key, "stale", options.Entry(TimeSpan.FromSeconds(5)));
        var loads = 0;
        cache.Events.FactorySuccess += (_, args) =>
        {
            if (!args.Key.EndsWith(key, StringComparison.Ordinal)) return;
            if (++loads == 1) generations.RecordStorageFailure(ApplicationCacheDomain.Mappings);
        };

        generations.RecordStorageFailure(ApplicationCacheDomain.Mappings);
        var recovered = await generations.GetAsync(ApplicationCacheDomain.Mappings);
        Assert.NotEqual("stale", recovered);
        Assert.Equal(1, loads);
        Assert.Equal(recovered, await generations.GetAsync(ApplicationCacheDomain.Mappings));
        Assert.Equal(2, loads);
    }

    /// <summary>Pre-cancellation returns a canceled operation without consulting the cache.</summary>
    [Fact]
    public async Task PreCanceledReadReturnsCanceledValueTaskWithoutSynchronousThrow()
    {
        var cache = new Mock<IFusionCache>(MockBehavior.Strict);
        using var generations = new ApplicationCacheGeneration(cache.Object, new ApplicationCacheOptions(), null);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var read = generations.GetAsync(ApplicationCacheDomain.Mappings, canceled.Token);

        Assert.True(read.IsCanceled);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.AsTask());
        Assert.Equal(canceled.Token, error.CancellationToken);
        cache.VerifyNoOtherCalls();
    }

    /// <summary>Configured-Redis disposal returns a faulted operation before starting a connection.</summary>
    [Fact]
    public async Task DisposedReadReturnsFaultedValueTaskWithoutSynchronousThrow()
    {
        var cache = new Mock<IFusionCache>(MockBehavior.Strict);
        using var generations = new ApplicationCacheGeneration(cache.Object, new ApplicationCacheOptions(), "127.0.0.1:1");
        generations.Dispose();

        var read = generations.GetAsync(ApplicationCacheDomain.Mappings);

        Assert.True(read.IsFaulted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.AsTask());
        cache.VerifyNoOtherCalls();
    }

    /// <summary>Local-only mode retains its existing cache lifetime independently of the unused connection owner.</summary>
    [Fact]
    public async Task LocalOnlyModeRetainsItsExistingPostDisposeReadBehavior()
    {
        var options = new ApplicationCacheOptions();
        using var cache = new FusionCache(options.FusionOptions());
        using var generations = new ApplicationCacheGeneration(cache, options, null);
        await cache.SetAsync("generation:mappings", "warm", options.Entry(TimeSpan.FromSeconds(5)));
        generations.Dispose();

        var read = generations.GetAsync(ApplicationCacheDomain.Mappings);

        Assert.True(read.IsCompletedSuccessfully);
        Assert.Equal("warm", await read);
    }

    /// <summary>Fast cache failures propagate once, without a cold retry, and increment the existing metric once.</summary>
    [Fact]
    public async Task FastReadFailureIsNotRetriedAndRecordsOneFailure()
    {
        var cache = new Mock<IFusionCache>(MockBehavior.Strict);
        var error = new RedisException("L1 read failed");
        cache.Setup(value => value.TryGet<string>(It.IsAny<string>(), It.IsAny<FusionCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Throws(error);
        using var generations = new ApplicationCacheGeneration(cache.Object, new ApplicationCacheOptions(), null);
        var before = await GenerationReadFailuresAsync();

        var read = generations.GetAsync(ApplicationCacheDomain.Mappings);

        Assert.True(read.IsFaulted);
        Assert.Same(error, await Assert.ThrowsAsync<RedisException>(() => read.AsTask()));
        Assert.Equal(before + 1, await GenerationReadFailuresAsync());
        cache.Verify(value => value.TryGet<string>(It.IsAny<string>(), It.IsAny<FusionCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        cache.VerifyNoOtherCalls();
    }

    /// <summary>A cache-thrown cancellation retains canceled status and does not count as storage failure.</summary>
    [Fact]
    public async Task FastReadCancellationIsNotRetriedOrRecordedAsStorageFailure()
    {
        var cache = new Mock<IFusionCache>(MockBehavior.Strict);
        var error = new OperationCanceledException();
        cache.Setup(value => value.TryGet<string>(It.IsAny<string>(), It.IsAny<FusionCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Throws(error);
        using var generations = new ApplicationCacheGeneration(cache.Object, new ApplicationCacheOptions(), null);
        var before = await GenerationReadFailuresAsync();

        var read = generations.GetAsync(ApplicationCacheDomain.Mappings);

        Assert.True(read.IsCanceled);
        Assert.Same(error, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.AsTask()));
        Assert.Equal(before, await GenerationReadFailuresAsync());
        cache.Verify(value => value.TryGet<string>(It.IsAny<string>(), It.IsAny<FusionCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        cache.VerifyNoOtherCalls();
    }

    /// <summary>Reads the public metric without adding a production test seam.</summary>
    private static async Task<double> GenerationReadFailuresAsync()
    {
        using var output = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(output);
        var line = System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n').SingleOrDefault(value =>
            value.StartsWith("conduit_application_cache_operations_total{domain=\"shared\",operation=\"generation_read\",result=\"redis_error\"}", StringComparison.Ordinal));
        return line is null ? 0 : double.Parse(line[(line.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

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
