using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Metrics;

namespace ConduitLLM.Gateway.RateLimiting;

/// <summary>
/// A held concurrency slot. Releasing it returns capacity to the key immediately rather than
/// waiting for the slot's expiry.
/// </summary>
public sealed record ConcurrencySlot(string Key, string EntryId)
{
    /// <summary>
    /// Group slot window the same entry occupies, when the key belongs to a group with its own
    /// concurrency ceiling. Both have to be returned or the group total drifts upward.
    /// </summary>
    public string? GroupKey { get; init; }
}

/// <summary>
/// Outcome of asking for a concurrency slot. <see cref="Slot"/> is set only when one was taken.
/// </summary>
public sealed record ConcurrencyDecision(
    bool IsAllowed,
    long Limit,
    long InFlight,
    ConcurrencySlot? Slot,
    string Scope = "concurrency")
{
    /// <summary>True when the verdict is a guess because the store could not be reached.</summary>
    public bool Degraded { get; init; }
}

public interface IConcurrencyRateLimitService
{
    /// <summary>
    /// Takes a slot for the current request, or reports that the key is at its ceiling.
    /// Returns null when the key has no concurrency cap.
    /// </summary>
    Task<ConcurrencyDecision?> TryAcquireAsync(HttpContext context);

    /// <summary>
    /// Returns a slot. Safe to call twice: the second call finds nothing to release and cannot
    /// free a slot belonging to another request.
    /// </summary>
    Task ReleaseAsync(ConcurrencySlot slot);
}

/// <summary>
/// Caps how many requests a virtual key may have in flight at once.
/// </summary>
/// <remarks>
/// <para>
/// Slots are entries in a sliding window whose span is the slot lifetime, so the leak problem
/// solves itself: a node that dies mid-request never releases its slots, but they age out of
/// the window instead of being held forever. The window span therefore has to exceed the
/// longest legitimate request — streaming responses can run for minutes — which is why it is
/// configurable rather than tied to a request timeout.
/// </para>
/// <para>
/// Each slot carries a unique id, so a double release removes nothing rather than freeing
/// whichever slot happens to be there.
/// </para>
/// </remarks>
public sealed class ConcurrencyRateLimitService : IConcurrencyRateLimitService
{
    internal const string ScopeName = "concurrency";
    internal const string GroupScopeName = "group:concurrency";

    private readonly ISlidingWindowRateLimiter _limiter;
    private readonly RateLimitOptions _options;
    private readonly ILogger<ConcurrencyRateLimitService> _logger;

    public ConcurrencyRateLimitService(
        ISlidingWindowRateLimiter limiter,
        RateLimitOptions options,
        ILogger<ConcurrencyRateLimitService> logger)
    {
        _limiter = limiter;
        _options = options;
        _logger = logger;
    }

    public async Task<ConcurrencyDecision?> TryAcquireAsync(HttpContext context)
    {
        if (context.Items[RateLimitContextKeys.KeyHash] is not string keyHash || string.IsNullOrEmpty(keyHash))
        {
            return null;
        }

        var keyMax = context.Items[RateLimitContextKeys.MaxParallelRequests] as int?;
        var groupId = context.Items[RateLimitContextKeys.GroupId] as int?;
        var groupMax = context.Items[RateLimitContextKeys.GroupMaxParallelRequests] as int?;

        var hasKeyLimit = keyMax is > 0;
        var hasGroupLimit = groupId is not null && groupMax is > 0;
        if (!hasKeyLimit && !hasGroupLimit)
        {
            return null;
        }

        var slotTtlMs = _options.ConcurrencySlotTtlSeconds * 1000;
        var key = RedisKeys.RateLimit.VirtualKeyConcurrency(keyHash);
        var windows = new List<RateLimitWindow>(2);

        if (keyMax is int keyLimit && keyLimit > 0)
        {
            windows.Add(new RateLimitWindow(key, ScopeName, slotTtlMs, keyLimit));
        }

        string? groupKey = null;
        if (groupId is int limitedGroupId && groupMax is int groupCeiling && groupCeiling > 0)
        {
            // A low-priority key sees a capped group ceiling, so it sheds first once the
            // group's shared slots pass the saturation threshold.
            var saturationFraction = RateLimitSaturationPolicy.SaturationFractionFor(
                context.Items[RateLimitContextKeys.Priority] as int?,
                _options.PrioritySaturationThreshold);
            var (groupLimit, groupScope) = saturationFraction is double fraction
                ? (RateLimitSaturationPolicy.Cap(groupCeiling, fraction),
                    GroupScopeName + RateLimitSaturationPolicy.ScopeSuffix)
                : (groupCeiling, GroupScopeName);

            groupKey = RedisKeys.RateLimit.GroupConcurrency(limitedGroupId);
            windows.Add(new RateLimitWindow(groupKey, groupScope, slotTtlMs, groupLimit));
        }

        var result = await _limiter.CheckAsync(windows, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        if (!result.IsAllowed)
        {
            var denied = result.DeniedWindow;
            _logger.LogWarning(
                "Virtual key {KeyHashPrefix} is at its {Scope} ceiling: {InFlight}/{Limit} requests in flight",
                SafePrefix(keyHash), denied?.Scope ?? ScopeName, denied?.Current ?? 0, denied?.Limit ?? 0);
            GatewayRateLimitMetrics.RecordRejected(denied?.Scope ?? ScopeName);

            return new ConcurrencyDecision(
                false,
                denied?.Limit ?? 0,
                denied?.Current ?? 0,
                null,
                denied?.Scope ?? ScopeName);
        }

        var tightest = result.TightestWindow;
        GatewayRateLimitMetrics.RecordAllowed(tightest?.Scope ?? ScopeName);

        return new ConcurrencyDecision(
            true,
            tightest?.Limit ?? 0,
            tightest?.Current ?? 0,
            result.EntryId is null
                ? null
                : new ConcurrencySlot(hasKeyLimit ? key : groupKey!, result.EntryId)
                {
                    GroupKey = hasKeyLimit ? groupKey : null
                },
            tightest?.Scope ?? ScopeName)
        {
            Degraded = result.Degraded
        };
    }

    public async Task ReleaseAsync(ConcurrencySlot slot)
    {
        // Weight is 1 for every slot, so a release always returns exactly one unit.
        await _limiter.ReleaseAsync(slot.Key, slot.EntryId, 1);

        // The same entry occupies the group window; leaving it there would permanently shrink
        // the group's capacity.
        if (slot.GroupKey is not null)
        {
            await _limiter.ReleaseAsync(slot.GroupKey, slot.EntryId, 1);
        }
    }

    private static string SafePrefix(string keyHash) => keyHash.Length <= 8 ? keyHash : keyHash[..8];
}

/// <summary>
/// Stand-in for deployments with no Redis: concurrency is shared state across instances, so
/// without a shared store there is nothing to enforce.
/// </summary>
public sealed class UnlimitedConcurrencyRateLimitService : IConcurrencyRateLimitService
{
    public Task<ConcurrencyDecision?> TryAcquireAsync(HttpContext context) =>
        Task.FromResult<ConcurrencyDecision?>(null);

    public Task ReleaseAsync(ConcurrencySlot slot) => Task.CompletedTask;
}
