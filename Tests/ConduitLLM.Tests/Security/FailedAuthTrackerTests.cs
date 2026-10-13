using ConduitLLM.Security.Services;
using ConduitLLM.Tests.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Security;

public sealed class FailedAuthTrackerTests : IAsyncLifetime
{
    private readonly string _prefix = RedisTestServer.NewKeyPrefix("failed-auth");
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => RedisTestServer.CleanupAsync(_prefix);
    private static string AttemptsKey(string scope, string ip) => $"security:failed_auth:v2:{{{scope}:{ip}}}:attempts";
    private static string BanKey(string scope, string ip) => $"security:failed_auth:v2:{{{scope}:{ip}}}:ban";

    [SkippableFact]
    public async Task Redis_TwoTrackers_CountEveryConcurrentFailureAndCreateOneBan()
    {
        var redis = RedisTestServer.Require();
        var first = new RedisFailedAuthTracker(redis, NullLogger.Instance);
        var second = new RedisFailedAuthTracker(redis, NullLogger.Instance);
        var ip = _prefix + "burst";
        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(index =>
            (index % 2 == 0 ? first : second).RecordAsync("api", ip, 3, TimeSpan.FromMinutes(5), "masked")));

        Assert.Equal(Enumerable.Range(1, 200).Select(value => (long)value), results.Select(result => result.Attempts).Order());
        Assert.Single(results, result => result.NewBan);
        var firstBan = await first.GetBanAsync("api", ip);
        var secondBan = await second.GetBanAsync("api", ip);
        Assert.NotNull(firstBan);
        Assert.Equal(200, firstBan.FailedAttempts);
        Assert.Equal(firstBan.BannedUntil, secondBan!.BannedUntil);
        Assert.Equal("api", firstBan.Source);
        Assert.Equal("masked", firstBan.LastAttemptedKey);
        Assert.False(await redis.GetDatabase().KeyExistsAsync(AttemptsKey("api", ip)));
    }

    [SkippableFact]
    public async Task Redis_InactivityWindowSlides_BanDeadlineNeverSlidesAndClearingPreservesBan()
    {
        var redis = RedisTestServer.Require();
        var database = redis.GetDatabase();
        var tracker = new RedisFailedAuthTracker(redis, NullLogger.Instance);
        var ip = _prefix + "expiry";
        var duration = TimeSpan.FromMinutes(5);
        await tracker.RecordAsync("api", ip, 3, duration, "first-mask");
        await database.KeyExpireAsync(AttemptsKey("api", ip), TimeSpan.FromSeconds(1));
        var second = await tracker.RecordAsync("api", ip, 3, duration, "second-mask");
        Assert.Equal(2, second.Attempts);
        Assert.True(await database.KeyTimeToLiveAsync(AttemptsKey("api", ip)) > TimeSpan.FromMinutes(4));
        Assert.Equal("second-mask", (await database.HashGetAsync(AttemptsKey("api", ip), "lastKey")).ToString());
        Assert.Equal("api", (await database.HashGetAsync(AttemptsKey("api", ip), "source")).ToString());
        Assert.True((long)await database.HashGetAsync(AttemptsKey("api", ip), "lastAttempt") > 0);
        var third = await tracker.RecordAsync("api", ip, 3, duration, "third-mask");
        var deadline = third.Ban!.BannedUntil;
        var initialTtl = await database.KeyTimeToLiveAsync(BanKey("api", ip));
        await tracker.ClearAttemptsAsync("api", ip);
        var fourth = await tracker.RecordAsync("api", ip, 3, duration, "last-mask");
        Assert.Equal(4, fourth.Attempts);
        Assert.False(fourth.NewBan);
        Assert.Equal(deadline, fourth.Ban!.BannedUntil);
        Assert.Equal("last-mask", fourth.Ban.LastAttemptedKey);
        Assert.Equal(deadline, (await tracker.GetBanAsync("api", ip))!.BannedUntil);
        Assert.True(await database.KeyTimeToLiveAsync(BanKey("api", ip)) <= initialTtl);
        Assert.Null(await tracker.GetBanAsync("other-api", ip));
        await database.KeyDeleteAsync(BanKey("api", ip));
        Assert.Equal(1, (await tracker.RecordAsync("api", ip, 3, duration, "new-window")).Attempts);
    }

    [SkippableFact]
    public async Task Redis_ClearBelowThresholdResetsAttempts_ExpiredAttemptStartsNewWindow()
    {
        var redis = RedisTestServer.Require();
        var tracker = new RedisFailedAuthTracker(redis, NullLogger.Instance);
        var ip = _prefix + "clear";
        await tracker.RecordAsync("api", ip, 3, TimeSpan.FromMinutes(5), "mask");
        await tracker.RecordAsync("api", ip, 3, TimeSpan.FromMinutes(5), "mask");
        await tracker.ClearAttemptsAsync("api", ip);
        Assert.Equal(1, (await tracker.RecordAsync("api", ip, 3, TimeSpan.FromMinutes(5), "mask")).Attempts);
        await redis.GetDatabase().KeyExpireAsync(AttemptsKey("api", ip), TimeSpan.Zero);
        Assert.Equal(1, (await tracker.RecordAsync("api", ip, 3, TimeSpan.FromMinutes(5), "mask")).Attempts);
    }

    [Fact]
    public async Task Memory_TwoTrackersSharingCache_CountEveryConcurrentFailure()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var first = new MemoryFailedAuthTracker(memory);
        var second = new MemoryFailedAuthTracker(memory);
        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).RecordAsync("api", "ip", 3, TimeSpan.FromMinutes(5), "mask"))));
        Assert.Equal(Enumerable.Range(1, 200).Select(value => (long)value), results.Select(result => result.Attempts).Order());
        Assert.Single(results, result => result.NewBan);
        Assert.Equal(200, (await first.GetBanAsync("api", "ip"))!.FailedAttempts);
        Assert.Equal((await first.GetBanAsync("api", "ip"))!.BannedUntil, (await second.GetBanAsync("api", "ip"))!.BannedUntil);
    }

    [Fact]
    public async Task Memory_SlidingAttempts_AbsoluteBan_AndSuccessClearing()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var clock = new Clock();
        var tracker = new MemoryFailedAuthTracker(memory, clock);
        var duration = TimeSpan.FromMinutes(5);
        await tracker.RecordAsync("api", "ip", 3, duration, "mask");
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(2, (await tracker.RecordAsync("api", "ip", 3, duration, "mask")).Attempts);
        clock.Advance(TimeSpan.FromMinutes(4));
        var banned = await tracker.RecordAsync("api", "ip", 3, duration, "mask");
        Assert.True(banned.NewBan);
        var deadline = banned.Ban!.BannedUntil;
        await tracker.ClearAttemptsAsync("api", "ip");
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(deadline, (await tracker.RecordAsync("api", "ip", 3, duration, "new-mask")).Ban!.BannedUntil);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await tracker.GetBanAsync("api", "ip"));
        Assert.Equal(1, (await tracker.RecordAsync("api", "ip", 3, duration, "mask")).Attempts);
        await tracker.ClearAttemptsAsync("api", "ip");
        Assert.Equal(1, (await tracker.RecordAsync("api", "ip", 3, duration, "mask")).Attempts);
        clock.Advance(duration);
        Assert.Equal(1, (await tracker.RecordAsync("api", "ip", 3, duration, "mask")).Attempts);
    }

    [Fact]
    public async Task Memory_ReturnedMetadataCannotMutateTheStoredBan()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var tracker = new MemoryFailedAuthTracker(memory);
        var first = await tracker.RecordAsync("api", "ip", 1, TimeSpan.FromMinutes(5), "mask");
        var deadline = first.Ban!.BannedUntil;
        first.Ban.BannedUntil = DateTime.MinValue;
        first.Ban.LastAttemptedKey = "modified";
        Assert.Equal(deadline, (await tracker.GetBanAsync("api", "ip"))!.BannedUntil);
        Assert.Equal("mask", (await tracker.GetBanAsync("api", "ip"))!.LastAttemptedKey);
        var second = await tracker.RecordAsync("api", "ip", 1, TimeSpan.FromMinutes(5), "second-mask");
        second.Ban!.BannedUntil = DateTime.MinValue;
        Assert.Equal(deadline, (await tracker.GetBanAsync("api", "ip"))!.BannedUntil);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentClearAndThreshold_CannotEraseCreatedBan(bool distributed)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        IFailedAuthTracker tracker = distributed
            ? new RedisFailedAuthTracker(RedisTestServer.Require(), NullLogger.Instance)
            : new MemoryFailedAuthTracker(memory);
        var ip = _prefix + "clear-race";
        await Task.WhenAll(Enumerable.Range(0, 100).SelectMany(_ => new[]
        {
            Task.Run(async () => await tracker.RecordAsync("api", ip, 1, TimeSpan.FromMinutes(5), "mask")),
            Task.Run(async () => await tracker.ClearAttemptsAsync("api", ip))
        }));
        Assert.Equal(100, (await tracker.GetBanAsync("api", ip))!.FailedAttempts);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
