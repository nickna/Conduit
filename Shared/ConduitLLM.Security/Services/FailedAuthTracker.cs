using System.Globalization;
using System.Runtime.CompilerServices;
using ConduitLLM.Security.Models;
using ConduitLLM.Security.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConduitLLM.Security.Services;

public sealed record FailedAuthTrackingResult(long Attempts, bool NewBan, BannedIpInfo? Ban);

/// <summary>Atomic failed-auth recording with a sliding inactivity window and an absolute ban.</summary>
public interface IFailedAuthTracker
{
    Task<FailedAuthTrackingResult> RecordAsync(string scope, string ipAddress, int threshold,
        TimeSpan duration, string maskedKey);
    Task ClearAttemptsAsync(string scope, string ipAddress);
    Task<BannedIpInfo?> GetBanAsync(string scope, string ipAddress);
}

public static class FailedAuthTrackerFactory
{
    public static IFailedAuthTracker Create(SecurityOptionsBase options, IServiceProvider? services,
        IMemoryCache memory, ILogger logger)
    {
        // Disabled tracking must not resolve a lazily registered Redis connection.
        if (!options.UseDistributedTracking || !options.FailedAuth.Enabled)
            return new MemoryFailedAuthTracker(memory);
        var redis = services?.GetService<IConnectionMultiplexer>();
        if (redis is not null) return new RedisFailedAuthTracker(redis, logger);
        logger.LogWarning("Distributed failed-auth tracking is enabled but no Redis connection is registered; " +
            "authentication failures and bans will be tracked per instance only");
        return new MemoryFailedAuthTracker(memory);
    }
}

internal static class FailedAuthTrackerState
{
    internal const string Reason = "Exceeded max failed authentication attempts";
    // Scope preserves the production Admin/Gateway cache isolation. Both keys share a Redis slot.
    internal static string AttemptsKey(string scope, string ip) => $"security:failed_auth:v2:{{{scope}:{ip}}}:attempts";
    internal static string BanKey(string scope, string ip) => $"security:failed_auth:v2:{{{scope}:{ip}}}:ban";
    internal static BannedIpInfo Ban(long attempts, DateTime until, string source, string maskedKey) => new()
    {
        BannedUntil = until, FailedAttempts = (int)Math.Min(attempts, int.MaxValue),
        Source = source, Reason = Reason, LastAttemptedKey = maskedKey
    };
    internal static void Validate(int threshold, TimeSpan duration)
    {
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (duration.TotalMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(duration));
    }
}

public sealed class MemoryFailedAuthTracker(IMemoryCache cache, TimeProvider? clock = null) : IFailedAuthTracker
{
    // Separate services/trackers sharing a cache must also share the transition lock.
    private static readonly ConditionalWeakTable<IMemoryCache, object> Gates = new();
    private readonly object _gate = Gates.GetValue(cache, _ => new object());
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private sealed record Attempts(long Count, DateTimeOffset ExpiresAt, FailedAuthData Metadata);
    private sealed record Ban(long Count, BannedIpInfo Metadata, DateTime LastAttempt);

    public Task<FailedAuthTrackingResult> RecordAsync(string scope, string ipAddress, int threshold,
        TimeSpan duration, string maskedKey)
    {
        FailedAuthTrackerState.Validate(threshold, duration);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var attemptKey = FailedAuthTrackerState.AttemptsKey(scope, ipAddress);
            var banKey = FailedAuthTrackerState.BanKey(scope, ipAddress);
            var ban = cache.Get<Ban>(banKey);
            if (ban is not null && ban.Metadata.BannedUntil > now.UtcDateTime)
            {
                var info = FailedAuthTrackerState.Ban(ban.Count + 1, ban.Metadata.BannedUntil, scope, maskedKey);
                cache.Set(banKey, new Ban(ban.Count + 1, info, now.UtcDateTime), info.BannedUntil - now.UtcDateTime);
                return Task.FromResult(new FailedAuthTrackingResult(ban.Count + 1, false,
                    FailedAuthTrackerState.Ban(ban.Count + 1, info.BannedUntil, scope, maskedKey)));
            }
            cache.Remove(banKey);
            var previous = cache.Get<Attempts>(attemptKey);
            var count = (previous is not null && previous.ExpiresAt > now ? previous.Count : 0) + 1;
            if (count >= threshold)
            {
                var info = FailedAuthTrackerState.Ban(count, now.Add(duration).UtcDateTime, scope, maskedKey);
                cache.Set(banKey, new Ban(count, info, now.UtcDateTime), duration);
                cache.Remove(attemptKey);
                return Task.FromResult(new FailedAuthTrackingResult(count, true,
                    FailedAuthTrackerState.Ban(count, info.BannedUntil, scope, maskedKey)));
            }
            cache.Set(attemptKey, new Attempts(count, now.Add(duration), new FailedAuthData
            {
                Attempts = (int)Math.Min(count, int.MaxValue), Source = scope,
                LastAttempt = now.UtcDateTime, LastAttemptedKey = maskedKey
            }), duration);
            return Task.FromResult(new FailedAuthTrackingResult(count, false, null));
        }
    }

    public Task ClearAttemptsAsync(string scope, string ipAddress)
    {
        lock (_gate) cache.Remove(FailedAuthTrackerState.AttemptsKey(scope, ipAddress));
        return Task.CompletedTask;
    }

    public Task<BannedIpInfo?> GetBanAsync(string scope, string ipAddress)
    {
        lock (_gate)
        {
            var ban = cache.Get<Ban>(FailedAuthTrackerState.BanKey(scope, ipAddress));
            return Task.FromResult(ban is not null && ban.Metadata.BannedUntil > _clock.GetUtcNow().UtcDateTime
                ? FailedAuthTrackerState.Ban(ban.Count, ban.Metadata.BannedUntil, ban.Metadata.Source, ban.Metadata.LastAttemptedKey)
                : null);
        }
    }
}

public sealed class RedisFailedAuthTracker(IConnectionMultiplexer redis, ILogger logger) : IFailedAuthTracker
{
    private const string RecordScript = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local untilAt = tonumber(redis.call('HGET', KEYS[2], 'until') or '0')
        if untilAt > now then
            local count = redis.call('HINCRBY', KEYS[2], 'count', 1)
            redis.call('HSET', KEYS[2], 'lastAttempt', now, 'lastKey', ARGV[4])
            return {count, untilAt, 0}
        end
        redis.call('DEL', KEYS[2])
        local count = redis.call('HINCRBY', KEYS[1], 'count', 1)
        redis.call('HSET', KEYS[1], 'source', ARGV[3], 'lastAttempt', now, 'lastKey', ARGV[4])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        if count >= tonumber(ARGV[1]) then
            untilAt = now + tonumber(ARGV[2])
            redis.call('HSET', KEYS[2], 'count', count, 'until', untilAt,
                'source', ARGV[3], 'reason', ARGV[5], 'lastAttempt', now, 'lastKey', ARGV[4])
            redis.call('PEXPIREAT', KEYS[2], untilAt)
            redis.call('DEL', KEYS[1])
            return {count, untilAt, 1}
        end
        return {count, 0, 0}
        """;

    public async Task<FailedAuthTrackingResult> RecordAsync(string scope, string ipAddress, int threshold,
        TimeSpan duration, string maskedKey)
    {
        FailedAuthTrackerState.Validate(threshold, duration);
        try
        {
            // RedisResult scalars avoid reflection-based JSON metadata on NativeAOT.
            var result = (RedisResult[]?)await redis.GetDatabase().ScriptEvaluateAsync(RecordScript,
                new RedisKey[] { FailedAuthTrackerState.AttemptsKey(scope, ipAddress), FailedAuthTrackerState.BanKey(scope, ipAddress) },
                new RedisValue[] { threshold, (long)duration.TotalMilliseconds, scope, maskedKey, FailedAuthTrackerState.Reason })
                ?? throw new InvalidOperationException("Failed-auth tracking returned no result");
            var count = (long)result[0];
            var until = (long)result[1];
            return new(count, (long)result[2] == 1, until == 0 ? null : FailedAuthTrackerState.Ban(
                count, DateTimeOffset.FromUnixTimeMilliseconds(until).UtcDateTime, scope, maskedKey));
        }
        catch (Exception exception)
        {
            // Failure to track bans does not validate credentials; authentication still rejects them.
            logger.LogWarning(exception, "Failed-auth tracking unavailable for {Scope}/{IpAddress}", scope, ipAddress);
            return new(0, false, null);
        }
    }

    public async Task ClearAttemptsAsync(string scope, string ipAddress)
    {
        try { await redis.GetDatabase().KeyDeleteAsync(FailedAuthTrackerState.AttemptsKey(scope, ipAddress)); }
        catch (Exception exception) { logger.LogWarning(exception, "Failed-auth clearing unavailable for {Scope}/{IpAddress}", scope, ipAddress); }
    }

    public async Task<BannedIpInfo?> GetBanAsync(string scope, string ipAddress)
    {
        try
        {
            var fields = await redis.GetDatabase().HashGetAllAsync(FailedAuthTrackerState.BanKey(scope, ipAddress));
            var values = fields.ToDictionary(field => field.Name.ToString(), field => field.Value.ToString());
            if (!values.TryGetValue("until", out var rawUntil)) return null;
            var until = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(rawUntil, CultureInfo.InvariantCulture)).UtcDateTime;
            // PEXPIREAT makes Redis authoritative for ban expiry. An application clock
            // ahead of Redis must not ignore a hash that Redis still considers live.
            return FailedAuthTrackerState.Ban(long.Parse(values["count"], CultureInfo.InvariantCulture), until,
                values["source"], values["lastKey"]);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed-auth ban lookup unavailable for {Scope}/{IpAddress}", scope, ipAddress);
            return null;
        }
    }
}
