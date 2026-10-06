using System.Security.Cryptography;
using System.Text;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ConduitLLM.Core.Services;

/// <summary>Leased concurrency and generation-fenced circuits; Redis is optional coordination only.</summary>
public sealed class WebhookAdmission(IOptions<WebhookDeliveryOptions> options, ILogger<WebhookAdmission> logger,
    IConnectionMultiplexer? redis = null, TimeProvider? timeProvider = null) : IWebhookAdmission
{
    private readonly WebhookDeliveryOptions _options = options.Value;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new();
    private readonly Dictionary<string, LocalLease> _active = new();
    private DateTime _warnAfter;
    private const string AcquireScript = """
        local clock = redis.call('TIME'); local now = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
        local expiry = now + tonumber(ARGV[2]) - tonumber(ARGV[1]); local token = ARGV[3]
        for i = 2,4 do redis.call('ZREMRANGEBYSCORE', KEYS[i], '-inf', now) end
        local untilAt = tonumber(redis.call('HGET', KEYS[1], 'until') or '0')
        local generation = tonumber(redis.call('HGET', KEYS[1], 'generation') or '0')
        if untilAt > now then return {0, untilAt, generation, 0} end
        local probe = untilAt > 0 and 1 or 0
        if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[4]) or
           redis.call('ZCARD', KEYS[3]) >= tonumber(ARGV[5]) or
           (probe == 1 and redis.call('ZCARD', KEYS[4]) >= tonumber(ARGV[6])) then
           return {0, now + tonumber(ARGV[7]), generation, probe}
        end
        redis.call('ZADD', KEYS[2], expiry, token); redis.call('ZADD', KEYS[3], expiry, token)
        if probe == 1 then redis.call('ZADD', KEYS[4], expiry, token) end
        for i = 1,4 do redis.call('PEXPIRE', KEYS[i], 1800000) end
        return {1, expiry, generation, probe}
        """;
    private const string ReleaseScript = """
        local clock = redis.call('TIME'); local now = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
        local owned = redis.call('ZSCORE', KEYS[3], ARGV[1])
        for i = 2,4 do redis.call('ZREM', KEYS[i], ARGV[1]) end
        if not owned or tonumber(owned) <= now then return 0 end
        local generation = tonumber(redis.call('HGET', KEYS[1], 'generation') or '0')
        if generation ~= tonumber(ARGV[3]) or ARGV[4] == '-1' then return 0 end
        if ARGV[4] == '1' then
            if ARGV[5] == '1' then
                redis.call('HSET', KEYS[1], 'failures', 0, 'until', 0, 'generation', generation + 1)
            end
        else
            local failures = redis.call('HINCRBY', KEYS[1], 'failures', 1)
            if ARGV[5] == '1' or failures >= tonumber(ARGV[6]) then
                redis.call('HSET', KEYS[1], 'until', now + tonumber(ARGV[7]), 'generation', generation + 1)
            end
        end
        redis.call('PEXPIRE', KEYS[1], 1800000); return 1
        """;

    public async Task<WebhookAdmissionDecision> AcquireAsync(string destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(destination)));
        var now = _clock.GetUtcNow().UtcDateTime;
        var token = Guid.NewGuid().ToString("N");
        var expires = now.AddSeconds(_options.AttemptTimeoutSeconds + 30);
        // Always apply a process-local cap, including while Redis connectivity changes.
        var local = AcquireLocal(key, token, now, expires);
        if (local.Lease == null || redis == null) return local;
        try
        {
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(AcquireScript, Keys(key),
                [Millis(now), Millis(expires), token, _options.GlobalConcurrency, _options.DestinationConcurrency,
                    _options.RecoveryProbes, _options.DeferralSeconds * 1000]))!;
            if ((int)result[0] == 0)
            {
                await local.Lease.DisposeAsync();
                return new(null, DateTimeOffset.FromUnixTimeMilliseconds((long)result[1]).UtcDateTime);
            }
            return new(new Lease(this, key, token, local.Lease, (long)result[2], (int)result[3] == 1), expires, (int)result[3] == 1);
        }
        catch (RedisException)
        {
            Warn(now);
            return local;
        }
    }

    private WebhookAdmissionDecision AcquireLocal(string key, string token, DateTime now, DateTime expires)
    {
        lock (_gate)
        {
            foreach (var entry in _active.Where(e => e.Value.Expires <= now).ToArray()) _active.Remove(entry.Key);
            foreach (var entry in _states.Where(e => e.Value.Touched < now.AddMinutes(-30) && !_active.Values.Any(l => l.Key == e.Key)).ToArray())
                _states.Remove(entry.Key);
            if (!_states.TryGetValue(key, out var state))
            {
                if (_states.Count >= 4096) return new(null, now.AddSeconds(_options.DeferralSeconds));
                _states[key] = state = new();
            }
            state.Touched = now;
            if (state.OpenUntil > now) return new(null, state.OpenUntil);
            var probe = state.OpenUntil != default;
            var destination = _active.Values.Where(l => l.Key == key).ToArray();
            if (_active.Count >= _options.GlobalConcurrency || destination.Length >= _options.DestinationConcurrency ||
                (probe && destination.Count(l => l.Probe) >= _options.RecoveryProbes))
                return new(null, now.AddSeconds(_options.DeferralSeconds));
            _active[token] = new(key, expires, state.Generation, probe);
            return new(new Lease(this, key, token, null, state.Generation, probe), expires, probe);
        }
    }

    private async Task ReleaseAsync(string key, string token, IWebhookAdmissionLease? local, long generation, bool probe, bool? success)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (local != null)
        {
            try
            {
                await redis!.GetDatabase().ScriptEvaluateAsync(ReleaseScript, Keys(key),
                    [token, Millis(now), generation, success.HasValue ? (success.Value ? 1 : 0) : -1,
                        probe ? 1 : 0, _options.CircuitFailureThreshold, _options.CircuitOpenSeconds * 1000]);
            }
            catch (RedisException) { Warn(now); }
            finally { await local.RecordAsync(success); }
            return;
        }
        lock (_gate)
        {
            if (!_active.Remove(token, out var lease) || lease.Expires <= now || !_states.TryGetValue(key, out var state) ||
                state.Generation != generation || success == null) return;
            if (success.Value)
            {
                if (probe) { state.Generation++; state.Failures = 0; state.OpenUntil = default; }
            }
            else if (++state.Failures >= _options.CircuitFailureThreshold || probe)
            {
                state.Generation++; state.OpenUntil = now.AddSeconds(_options.CircuitOpenSeconds);
            }
        }
    }

    private void Warn(DateTime now)
    {
        lock (_gate)
        {
            if (now < _warnAfter) return;
            _warnAfter = now.AddMinutes(1);
            logger.LogWarning("Webhook admission Redis unavailable; using bounded process-local coordination");
        }
    }
    private static long Millis(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();
    private static RedisKey[] Keys(string key) => [$"webhooks:{{admission}}:{key}:state", "webhooks:{admission}:global",
        $"webhooks:{{admission}}:{key}:active", $"webhooks:{{admission}}:{key}:probes"];
    private sealed class State { public long Generation; public int Failures; public DateTime OpenUntil; public DateTime Touched; }
    private sealed record LocalLease(string Key, DateTime Expires, long Generation, bool Probe);
    private sealed class Lease(WebhookAdmission owner, string key, string token, IWebhookAdmissionLease? local, long generation, bool probe)
        : IWebhookAdmissionLease
    {
        private int _released;
        public Task RecordAsync(bool? success, CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _released, 1) == 0 ? owner.ReleaseAsync(key, token, local, generation, probe, success) : Task.CompletedTask;
        public async ValueTask DisposeAsync() => await RecordAsync(null);
    }
}
