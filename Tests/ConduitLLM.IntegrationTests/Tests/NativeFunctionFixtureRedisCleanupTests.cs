using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using ConduitLLM.Configuration.Constants;
using ConduitLLM.Core.Constants;

using StackExchange.Redis;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed class NativeFunctionFixtureRedisCleanupTests(FunctionCredentialRedisFixture fixture)
    : IClassFixture<FunctionCredentialRedisFixture>
{
    [Theory]
    [InlineData(0, (int)NativeFunctionFixtureProfile.Exa)]
    [InlineData(5, (int)NativeFunctionFixtureProfile.Exa)]
    [InlineData(0, (int)NativeFunctionFixtureProfile.Mcp)]
    [InlineData(5, (int)NativeFunctionFixtureProfile.Mcp)]
    public async Task CleanupRemovesOwnedStateOnlyFromTheSelectedDatabaseAndClosesItsConnection(
        int databaseNumber, int profileValue)
    {
        var profile = (NativeFunctionFixtureProfile)profileValue;
        var options = ConfigurationOptions.Parse(await fixture.GetConnectionStringAsync());
        options.DefaultDatabase = databaseNumber;
        options.ConnectTimeout = options.SyncTimeout = options.AsyncTimeout = 5000;
        options.ConnectRetry = 0;
        options.AbortOnConnectFail = true;
        using var redis = ConnectionMultiplexer.Connect(options);
        var database = redis.GetDatabase();
        var otherDatabase = redis.GetDatabase(databaseNumber == 0 ? 5 : 0);
        var snapshot = Snapshot(profile);
        var marker = $"owned-cleanup-test:{snapshot.Owner:N}";
        var owned = OwnedKeys(snapshot).ToArray();
        var sentinels = SentinelKeys(snapshot).ToArray();
        var shared = new[] { "Conduit-DataProtection-Keys", "conduit-tasks:FunctionCost:All",
            "conduit-tasks:FunctionCost:All_Active", RedisKeys.Spend.HistoryStream, CacheKeys.IpFilter.GlobalFilters };
        var beforeShared = new List<byte[]?>();
        foreach (var key in shared)
            beforeShared.Add((byte[]?)await database.ExecuteAsync("DUMP", key).WaitAsync(TimeSpan.FromSeconds(5)));
        var seeded = new List<(IDatabase Database, RedisKey Key)>();
        try
        {
            foreach (var key in owned.Concat(sentinels))
                await SeedAsync(database, key, marker, seeded);
            foreach (var key in owned)
                await SeedAsync(otherDatabase, key, marker, seeded);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await NativeFunctionFixtureRedisCleanup.DeleteAsync(options.ToString(includePassword: true), snapshot, deadline.Token);
            // A retry is safe while SQL ownership remains available to the parent.
            await NativeFunctionFixtureRedisCleanup.DeleteAsync(options.ToString(includePassword: true), snapshot, deadline.Token);
            foreach (var key in owned)
            {
                Assert.False(await database.KeyExistsAsync(key).WaitAsync(deadline.Token), key);
                Assert.Equal(marker, (string?)await otherDatabase.StringGetAsync(key).WaitAsync(deadline.Token));
            }
            foreach (var key in sentinels)
                Assert.Equal(marker, (string?)await database.StringGetAsync(key).WaitAsync(deadline.Token));
            for (var index = 0; index < shared.Length; index++)
                Assert.Equal(beforeShared[index], (byte[]?)await database.ExecuteAsync("DUMP", shared[index]).WaitAsync(deadline.Token));
            await AssertCleanupClientClosedAsync(database, snapshot.Owner);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // Compare-and-delete only test-created values; never flush a namespace
            // or remove a preexisting shared ring/catalog/history key.
            foreach (var (target, key) in seeded)
                await target.ScriptEvaluateAsync("""
                    if redis.call('GET', KEYS[1]) == ARGV[1] then
                        return redis.call('DEL', KEYS[1])
                    end
                    return 0
                    """, [key], [marker]).WaitAsync(cleanup.Token);
        }
    }

    [Theory]
    [InlineData("empty-owner")]
    [InlineData("invalid-id")]
    [InlineData("foreign-key")]
    [InlineData("foreign-hash")]
    [InlineData("foreign-name")]
    [InlineData("wrong-profile")]
    [InlineData("undefined-profile")]
    public async Task UnverifiedSnapshotsAreRejectedBeforeRedisConnectionOrDeletion(string corruption)
    {
        var snapshot = Snapshot();
        snapshot = corruption switch
        {
            "empty-owner" => snapshot with { Owner = Guid.Empty },
            "invalid-id" => snapshot with { GroupId = -1 },
            "foreign-key" => snapshot with { VirtualKey = "condt_unrelated_key" },
            "foreign-hash" => snapshot with { VirtualKeyHash = new string('0', 64) },
            "foreign-name" => snapshot with { ConfigurationName = "unrelated configuration" },
            "wrong-profile" => snapshot with { Profile = NativeFunctionFixtureProfile.Mcp },
            "undefined-profile" => snapshot with { Profile = (NativeFunctionFixtureProfile)999 },
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => NativeFunctionFixtureRedisCleanup.DeleteAsync(
            "unreachable.invalid:1", snapshot, CancellationToken.None));
        Assert.Equal("snapshot", error.ParamName);
    }

    [Fact]
    public async Task MissingInputsFailBeforeConnecting()
    {
        Assert.Equal("snapshot", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NativeFunctionFixtureRedisCleanup.DeleteAsync("unreachable.invalid:1", null!, CancellationToken.None))).ParamName);
        Assert.Equal("redisConnectionString", (await Assert.ThrowsAsync<ArgumentException>(() =>
            NativeFunctionFixtureRedisCleanup.DeleteAsync(" \t", Snapshot(), CancellationToken.None))).ParamName);
    }

    [Fact]
    public async Task PreCancelledCleanupCannotConnectOrDelete()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeFunctionFixtureRedisCleanup.DeleteAsync(
            "unreachable.invalid:1", Snapshot(), cancelled.Token));
    }

    private static NativeFunctionFixtureSnapshot Snapshot(NativeFunctionFixtureProfile profile = NativeFunctionFixtureProfile.Exa)
    {
        var owner = Guid.NewGuid();
        var id = RandomNumberGenerator.GetInt32(1_000_000, 99_000_000);
        var key = $"condt_native_function_{owner:N}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return new NativeFunctionFixtureSnapshot(owner, id, id + 1, id + 2, id + 3, id + 4, id + 5,
            key, hash, $"native_function_{owner:N}:{(profile == NativeFunctionFixtureProfile.Mcp ? "mcp" : "exa")}", profile);
    }

    private static IEnumerable<string> OwnedKeys(NativeFunctionFixtureSnapshot snapshot)
    {
        var group = snapshot.GroupId;
        var key = snapshot.KeyId;
        var hash = snapshot.VirtualKeyHash;
        yield return CacheKeys.VirtualKey.ByHash(hash);
        yield return CacheKeys.IpFilter.ByVirtualKey(key);
        yield return CacheKeys.IpFilter.CheckResult("127.0.0.1", key);
        yield return CacheKeys.IpFilter.CheckResult("::1", key);
        foreach (var window in new[]
                 {
                     RedisKeys.RateLimit.VirtualKeyRpm(hash), RedisKeys.RateLimit.VirtualKeyRpd(hash),
                     RedisKeys.RateLimit.VirtualKeyTpm(hash), RedisKeys.RateLimit.VirtualKeyConcurrency(hash),
                     RedisKeys.RateLimit.GroupRpm(group), RedisKeys.RateLimit.GroupRpd(group),
                     RedisKeys.RateLimit.GroupTpm(group), RedisKeys.RateLimit.GroupConcurrency(group),
                     RedisKeys.RateLimit.VirtualKeyModelRpm(hash, "fixture"), RedisKeys.RateLimit.VirtualKeyModelTpm(hash, "fixture")
                 })
        {
            yield return window;
            yield return RedisKeys.RateLimit.WindowSum(window);
        }
        yield return $"conduit-tasks:FunctionCost:Config:{snapshot.ConfigurationId}";
        yield return RedisKeys.Spend.Patterns(key.ToString(CultureInfo.InvariantCulture));
        yield return RedisKeys.Spend.SentAlert(key.ToString(CultureInfo.InvariantCulture), "80");
        yield return RedisKeys.Spend.Cooldown(key.ToString(CultureInfo.InvariantCulture), "budget");
        yield return RedisKeys.Lock.AlertThreshold(key.ToString(CultureInfo.InvariantCulture), "80");
        foreach (var prefix in new[] { "pending_spend", "pending_spend_units", "processing_spend",
                     "processing_spend_units", "pending_spend_window_total_units", "reserved_spend",
                     "spend_reservations", "spend_reservation_expiry", "spend_reservations_started", "spend_reservations_settled" })
            yield return $"{prefix}:group:{group}";
        yield return $"key_usage:group:{group}:key:{key}";
        yield return $"key_usage_units:group:{group}:key:{key}";
        yield return $"processing_spend:group:{group}:claim:fixture-claim";
        yield return $"processing_spend_units:group:{group}:claim:fixture-claim";
        yield return $"processing_key_usage:group:{group}:key:{key}:claim:fixture-claim";
        yield return $"processing_key_usage_units:group:{group}:key:{key}:claim:fixture-claim";
        yield return $"processing_spend_window_units:group:{group}:window:2026101107:claim:fixture-claim";
        yield return $"key_usage_window_units:group:{group}:window:2026101107:key:{key}";
        yield return $"processing_key_usage_window_units:group:{group}:window:2026101107:key:{key}:claim:fixture-claim";
        // More than one SCAN page and deletion batch; the known group/window
        // namespace is still independent of unrelated numeric suffixes.
        for (var index = 0; index < 256; index++)
            yield return $"pending_spend_window_units:group:{group}:window:{index:D10}";
    }

    private static IEnumerable<string> SentinelKeys(NativeFunctionFixtureSnapshot snapshot)
    {
        var group = snapshot.GroupId;
        var key = snapshot.KeyId;
        var hash = snapshot.VirtualKeyHash;
        yield return $"vkey:{hash}3";
        yield return $"vkey:{hash}:suffix";
        yield return $"rate:vk:{hash}3:rpm";
        yield return $"rate:vkg:{group}3:rpm";
        yield return $"pending_spend:group:{group}3";
        yield return $"pending_spend:group:{group}:unrelated";
        yield return $"processing_spend:group:{group}3:claim:sentinel";
        yield return $"pending_spend_window_units:group:{group}3:window:2026101107";
        yield return $"processing_spend_window_units:group:{group}3:window:2026101107:claim:sentinel";
        yield return $"key_usage:group:{group}:key:{key}3";
        yield return $"key_usage_window_units:group:{group}:window:2026101107:key:{key}3";
        yield return $"processing_key_usage_window_units:group:{group}:window:2026101107:key:{key}3:claim:sentinel";
        yield return $"ipfilter:check:127.0.0.1:{key}3";
        yield return $"spend:alerts:sent:{key}3:80";
        yield return $"conduit-tasks:FunctionCost:Config:{snapshot.ConfigurationId}3";
        yield return $"conduit-tasks:FunctionCost:Config:{snapshot.ConfigurationId}:suffix";
        yield return $"Conduit-DataProtection-Keys:cleanup-sentinel:{snapshot.Owner:N}";
        yield return $"conduit-tasks:FunctionCost:All:cleanup-sentinel:{snapshot.Owner:N}";
        yield return $"spend:history:cleanup-sentinel:{snapshot.Owner:N}";
    }

    private static async Task SeedAsync(IDatabase database, RedisKey key, string marker,
        List<(IDatabase Database, RedisKey Key)> seeded)
    {
        var added = await database.StringSetAsync(key, marker, when: When.NotExists).WaitAsync(TimeSpan.FromSeconds(5));
        if (added) seeded.Add((database, key));
        Assert.True(added, "A fixture key already existed; the test must not overwrite it.");
    }

    private static async Task AssertCleanupClientClosedAsync(IDatabase database, Guid owner)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = $"name=native-function-cleanup:{owner:N}";
        while (((string?)await database.ExecuteAsync("CLIENT", "LIST").WaitAsync(deadline.Token))
               ?.Contains(name, StringComparison.Ordinal) is true)
            await Task.Delay(25, deadline.Token);
    }
}
