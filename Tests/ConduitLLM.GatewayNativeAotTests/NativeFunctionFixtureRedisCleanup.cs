using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using StackExchange.Redis;

/// <summary>Removes Redis state scoped to a verified fixture after its hosts have stopped.</summary>
internal static class NativeFunctionFixtureRedisCleanup
{
    internal static async Task DeleteAsync(
        string redisConnectionString, NativeFunctionFixtureSnapshot snapshot, CancellationToken ct)
    {
        Validate(snapshot);
        if (string.IsNullOrWhiteSpace(redisConnectionString))
            throw new ArgumentException("A Redis connection string is required.", nameof(redisConnectionString));
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var options = ConfigurationOptions.Parse(redisConnectionString);
        options.ClientName = $"native-function-cleanup:{snapshot.Owner:N}";
        options.ConnectTimeout = options.SyncTimeout = options.AsyncTimeout = 5000;
        options.ConnectRetry = 0;
        options.AbortOnConnectFail = true;
        // Synchronous, bounded connection acquisition never abandons a pending
        // ConnectAsync task that could later return an undisposed multiplexer.
        using var redis = ConnectionMultiplexer.Connect(options);
        Exception? primary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var endpoints = redis.GetEndPoints();
            if (endpoints.Length != 1 || redis.GetServer(endpoints[0]).ServerType != ServerType.Standalone)
                throw new NotSupportedException("The native fixture cleanup requires standalone Redis.");
            var database = redis.GetDatabase();
            var owned = ExactKeys(snapshot).Select(value => (RedisKey)value).ToHashSet();
            foreach (var pattern in Patterns(snapshot))
            {
                ulong cursor = 0;
                do
                {
                    var result = await database.ExecuteAsync("SCAN", cursor.ToString(CultureInfo.InvariantCulture),
                        "MATCH", pattern, "COUNT", 128).WaitAsync(token);
                    var parts = (RedisResult[]?)result;
                    if (parts is not { Length: 2 } ||
                        !ulong.TryParse((string?)parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out cursor) ||
                        (RedisResult[]?)parts[1] is not { } keys)
                        throw new InvalidOperationException("Redis returned an invalid fixture cleanup scan.");
                    foreach (var key in keys)
                    {
                        token.ThrowIfCancellationRequested();
                        var bytes = (byte[]?)key
                            ?? throw new InvalidOperationException("Redis returned an invalid fixture cleanup key.");
                        owned.Add((RedisKey)bytes);
                        if (owned.Count > 4096)
                            throw new InvalidOperationException("Native function fixture Redis state exceeded its cleanup bound.");
                    }
                } while (cursor != 0);
            }

            foreach (var batch in owned.Chunk(128))
            {
                token.ThrowIfCancellationRequested();
                await database.KeyDeleteAsync(batch).WaitAsync(token);
                if ((long)await database.ExecuteAsync("EXISTS", batch.Select(key => (object)key).ToArray())
                    .WaitAsync(token) != 0)
                    throw new InvalidOperationException("Native function fixture cleanup left Redis state.");
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            // This deadline remains fresh even when the caller or operation timed out.
            // Dispose below still runs if closing fails; cleanup failure is observable.
            try { await redis.CloseAsync(allowCommandsToComplete: false).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception cleanup) when (primary is not null)
            {
                throw new AggregateException("Redis fixture cleanup and connection shutdown both failed.", primary, cleanup);
            }
        }
    }

    private static void Validate(NativeFunctionFixtureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Profile is not (NativeFunctionFixtureProfile.Exa or NativeFunctionFixtureProfile.Mcp))
            throw new ArgumentException("Redis cleanup requires a supported native function fixture profile.", nameof(snapshot));
        var key = $"condt_native_function_{snapshot.Owner:N}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        if (snapshot.Owner == Guid.Empty || snapshot.GroupId <= 0 || snapshot.KeyId <= 0 ||
            snapshot.ConfigurationId <= 0 || snapshot.CostId <= 0 || snapshot.CredentialId <= 0 || snapshot.MappingId <= 0 ||
            snapshot.VirtualKey != key || snapshot.VirtualKeyHash != hash ||
            snapshot.ConfigurationName != $"native_function_{snapshot.Owner:N}:{(snapshot.Profile == NativeFunctionFixtureProfile.Exa ? "exa" : "mcp")}")
            throw new ArgumentException("Redis cleanup requires a complete GUID-owned fixture snapshot.", nameof(snapshot));
    }

    private static IEnumerable<string> ExactKeys(NativeFunctionFixtureSnapshot value)
    {
        var hash = value.VirtualKeyHash;
        var group = value.GroupId.ToString(CultureInfo.InvariantCulture);
        var key = value.KeyId.ToString(CultureInfo.InvariantCulture);
        // CacheKeys.VirtualKey/IpFilter and RedisKeys.RateLimit, plus companion sums.
        yield return $"vkey:{hash}";
        yield return $"ipfilter:vkey:{key}";
        foreach (var scope in new[] { $"rate:vk:{hash}", $"rate:vkg:{group}" })
        foreach (var limit in new[] { "rpm", "rpd", "tpm", "concurrency" })
        {
            yield return $"{scope}:{limit}";
            yield return $"{scope}:{limit}:sum";
        }
        // StoreBackedFunctionCostService uses HybridCacheAccessor; Gateway's
        // IDistributedCache InstanceName is conduit-tasks:, not a FusionCache prefix.
        yield return $"conduit-tasks:FunctionCost:Config:{value.ConfigurationId.ToString(CultureInfo.InvariantCulture)}";
        yield return $"spend:patterns:{key}";
        foreach (var prefix in new[]
                 {
                     "pending_spend", "pending_spend_units", "processing_spend", "processing_spend_units",
                     "pending_spend_window_total_units", "reserved_spend", "spend_reservations",
                     "spend_reservation_expiry", "spend_reservations_started", "spend_reservations_settled",
                     "spend_reservations_indeterminate"
                 })
            yield return $"{prefix}:group:{group}";
        yield return $"key_usage:group:{group}:key:{key}";
        yield return $"key_usage_units:group:{group}:key:{key}";
    }

    private static IEnumerable<string> Patterns(NativeFunctionFixtureSnapshot value)
    {
        var group = value.GroupId.ToString(CultureInfo.InvariantCulture);
        var key = value.KeyId.ToString(CultureInfo.InvariantCulture);
        // Group/key identifiers are delimited on both sides. These cannot match
        // another group's numeric suffix or an unrelated key within the same group.
        yield return $"ipfilter:check:*:{key}";
        yield return $"rate:vk:{value.VirtualKeyHash}:model:*";
        yield return $"processing_spend:group:{group}:claim:*";
        yield return $"processing_spend_units:group:{group}:claim:*";
        yield return $"processing_key_usage:group:{group}:key:{key}:claim:*";
        yield return $"processing_key_usage_units:group:{group}:key:{key}:claim:*";
        yield return $"pending_spend_window_units:group:{group}:window:*";
        yield return $"processing_spend_window_units:group:{group}:window:*:claim:*";
        yield return $"key_usage_window_units:group:{group}:window:*:key:{key}";
        yield return $"processing_key_usage_window_units:group:{group}:window:*:key:{key}:claim:*";
        yield return $"spend:alerts:sent:{key}:*";
        yield return $"spend:alerts:cooldown:{key}:*";
        yield return $"lock:alert:vk:{key}:threshold:*";
        // Shared rings, catalog/cache lists, statistics, history streams and host
        // heartbeats have no fixture ownership and must never be deleted here.
    }
}
