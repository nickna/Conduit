using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.RateLimiting;

namespace ConduitLLM.Gateway.Middleware
{
    /// <summary>
    /// Enforces per-virtual-key sliding-window RPM/RPD rate limits via
    /// <see cref="IVirtualKeyRateLimitService"/>. The authenticated key's hash and
    /// configured limits are stashed in <c>HttpContext.Items</c> by
    /// <c>VirtualKeyAuthenticationHandler</c>, so this middleware does no DB lookups.
    /// </summary>
    /// <remarks>
    /// Behavior contract:
    /// - Excluded paths (health, metrics, public media, SignalR hubs) are passed through.
    /// - Requests not authenticated via the VirtualKey scheme (e.g., Backend service-to-service)
    ///   are passed through — they have no <c>VirtualKey.KeyHash</c> in <c>HttpContext.Items</c>.
    /// - A key with no request, daily or concurrency ceiling is unlimited and costs no round-trip.
    /// - On rate-limit rejection, returns 429 with <c>Retry-After</c> and an OpenAI-shaped error body.
    /// - On any internal exception (Redis unavailable, etc.), logs a warning and lets the request
    ///   through (fail open) — preferable to tanking the gateway.
    /// - A concurrency slot, when the key has a cap, is held for the lifetime of the request and
    ///   returned in a finally — so completion, failure and client abort all release it. For an
    ///   asynchronous job that means the submit call is what occupies a slot, not the job itself.
    /// - Token limits are not enforced here: they need the bound request body, so they run as an
    ///   endpoint filter. See <see cref="ConduitLLM.Gateway.RateLimiting.TokenRateLimitFilter"/>.
    /// </remarks>
    public class VirtualKeyRateLimitMiddleware
    {
        private static readonly string[] ExcludedPathPrefixes =
        {
            "/health",
            "/metrics",
            "/v1/conduit/media/public",
            "/hubs"
        };

        private readonly RequestDelegate _next;
        private readonly IVirtualKeyRateLimitService _rateLimitService;
        private readonly IConcurrencyRateLimitService _concurrencyService;
        private readonly IRateLimitFailurePolicy _failurePolicy;
        private readonly RateLimitOptions _options;
        private readonly ILogger<VirtualKeyRateLimitMiddleware> _logger;

        public VirtualKeyRateLimitMiddleware(
            RequestDelegate next,
            IVirtualKeyRateLimitService rateLimitService,
            IConcurrencyRateLimitService concurrencyService,
            IRateLimitFailurePolicy failurePolicy,
            RateLimitOptions options,
            ILogger<VirtualKeyRateLimitMiddleware> logger)
        {
            _next = next;
            _rateLimitService = rateLimitService;
            _concurrencyService = concurrencyService;
            _failurePolicy = failurePolicy;
            _options = options;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (ShouldSkip(context))
            {
                await _next(context);
                return;
            }

            // Only authenticated VirtualKey-scheme requests carry a KeyHash in Items.
            // Backend-scheme and unauthenticated requests pass through unrestricted here.
            if (context.Items[RateLimitContextKeys.KeyHash] is not string keyHash || string.IsNullOrEmpty(keyHash))
            {
                await _next(context);
                return;
            }

            var keyLimits = new RequestRateLimits(
                context.Items[RateLimitContextKeys.Rpm] as int?,
                context.Items[RateLimitContextKeys.Rpd] as int?);
            var maxParallel = context.Items[RateLimitContextKeys.MaxParallelRequests] as int?;

            // Group ceilings apply on top of the key's own; the tighter of the two governs.
            var groupId = context.Items[RateLimitContextKeys.GroupId] as int?;
            var groupLimits = groupId is null
                ? default
                : new RequestRateLimits(
                    context.Items[RateLimitContextKeys.GroupRpm] as int?,
                    context.Items[RateLimitContextKeys.GroupRpd] as int?);
            var groupMaxParallel = groupId is null
                ? null
                : context.Items[RateLimitContextKeys.GroupMaxParallelRequests] as int?;

            // A low-priority key is admitted against a reduced fraction of each group ceiling,
            // so it sheds first once the group's shared windows pass the saturation threshold.
            var saturationFraction = groupId is null
                ? null
                : RateLimitSaturationPolicy.SaturationFractionFor(
                    context.Items[RateLimitContextKeys.Priority] as int?,
                    _options.PrioritySaturationThreshold);

            // Nothing configured at either tier = unlimited; skip the Redis round-trip entirely.
            if (keyLimits.IsUnlimited && groupLimits.IsUnlimited &&
                !HasConfiguredLimit(maxParallel) && !HasConfiguredLimit(groupMaxParallel))
            {
                await _next(context);
                return;
            }

            if (!keyLimits.IsUnlimited || !groupLimits.IsUnlimited)
            {
                RateLimitCheckResult result;
                try
                {
                    result = await _rateLimitService.CheckRateLimitAsync(
                        keyHash, keyLimits, groupId, groupLimits, saturationFraction);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Rate limit check threw for virtual key {KeyHashPrefix}",
                        SpanHelper.MaskSecret(keyHash));

                    if (_failurePolicy.ShouldReject("request-limits", ex.Message))
                    {
                        await RateLimitResponse.WriteDegradedAsync(context, "request-limits");
                        return;
                    }

                    await _next(context);
                    return;
                }

                // The store answered but could not be trusted; the configured policy decides.
                if (result.Degraded && _failurePolicy.ShouldReject(
                        string.IsNullOrEmpty(result.LimitType) ? "request-limits" : result.LimitType,
                        "the rate limit store was unreachable"))
                {
                    await RateLimitResponse.WriteDegradedAsync(context, "request-limits");
                    return;
                }

                SetRateLimitHeaders(context, result);

                if (!result.IsAllowed)
                {
                    GatewayRateLimitMetrics.RecordRejected(result.LimitType);
                    await WriteRateLimitedResponseAsync(context, result);
                    return;
                }

                GatewayRateLimitMetrics.RecordAllowed(result.LimitType);
            }

            await InvokeWithConcurrencySlotAsync(context, keyHash);
        }

        /// <summary>
        /// Takes a concurrency slot for the request when the key has a cap, and returns it once
        /// the request is over, however it ended.
        /// </summary>
        private async Task InvokeWithConcurrencySlotAsync(HttpContext context, string keyHash)
        {
            ConcurrencyDecision? decision;
            try
            {
                decision = await _concurrencyService.TryAcquireAsync(context);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Concurrency check threw for virtual key {KeyHashPrefix}",
                    SpanHelper.MaskSecret(keyHash));

                if (_failurePolicy.ShouldReject(ConcurrencyRateLimitService.ScopeName, ex.Message))
                {
                    await RateLimitResponse.WriteDegradedAsync(context, ConcurrencyRateLimitService.ScopeName);
                    return;
                }

                await _next(context);
                return;
            }

            if (decision is { Degraded: true } &&
                _failurePolicy.ShouldReject(decision.Scope, "the rate limit store was unreachable"))
            {
                await RateLimitResponse.WriteDegradedAsync(context, decision.Scope);
                return;
            }

            // No cap configured, or the store was unavailable: nothing to hold.
            if (decision is null || (decision.IsAllowed && decision.Slot is null))
            {
                await _next(context);
                return;
            }

            if (!decision.IsAllowed)
            {
                // Capacity returns when some other request finishes, which cannot be predicted,
                // so the advertised wait is a short documented constant rather than an instant.
                var retryAt = DateTime.UtcNow.AddSeconds(_options.ConcurrencyRetryAfterSeconds);
                RateLimitResponse.SetHeaders(context, decision.Limit, 0, retryAt, decision.Scope);
                await RateLimitResponse.WriteAsync(
                    context,
                    decision.Scope,
                    decision.Limit,
                    retryAt,
                    $"Too many concurrent requests ({decision.Limit} in flight). Retry once an in-flight request completes.");
                return;
            }

            try
            {
                await _next(context);
            }
            finally
            {
                // Runs on completion, on a thrown exception, and on a client abort — an
                // abandoned stream must not hold its slot until the TTL expires.
                try
                {
                    await _concurrencyService.ReleaseAsync(decision.Slot!);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to release the concurrency slot for virtual key {KeyHashPrefix}; it will expire on its own",
                        SpanHelper.MaskSecret(keyHash));
                }
            }
        }

        private static bool ShouldSkip(HttpContext context)
        {
            var path = context.Request.Path;
            if (!path.HasValue) return false;
            foreach (var prefix in ExcludedPathPrefixes)
            {
                if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool HasConfiguredLimit(int? limit) => limit.HasValue && limit.Value > 0;

        private static void SetRateLimitHeaders(HttpContext context, RateLimitCheckResult result)
        {
            // Headers are informational — set on both allow and deny so clients can pace themselves.
            RateLimitResponse.SetHeaders(
                context, result.Limit, result.RequestsRemaining, result.ResetsAt, result.LimitType);
        }

        private static Task WriteRateLimitedResponseAsync(HttpContext context, RateLimitCheckResult result)
        {
            // A saturation shed is not the caller exceeding their own limit: the group is busy
            // and this key's tier only reaches part of the group ceiling. Say so. Every other
            // scope uses the shared default so the middleware and filter 429s stay identical.
            string? message = null;
            if (RateLimitSaturationPolicy.IsSaturationScope(result.LimitType))
            {
                var retryAfterSeconds = RateLimitResponse.RetryAfterSeconds(result.ResetsAt);
                message = $"The key's group is saturated and this key's priority tier is admitted to only {result.Limit} of the group's requests. Retry after {retryAfterSeconds} seconds.";
            }

            return RateLimitResponse.WriteAsync(
                context,
                result.LimitType,
                result.Limit,
                result.ResetsAt,
                message);
        }

    }

    public static class VirtualKeyRateLimitMiddlewareExtensions
    {
        public static IApplicationBuilder UseVirtualKeyRateLimiting(this IApplicationBuilder app)
        {
            return app.UseMiddleware<VirtualKeyRateLimitMiddleware>();
        }
    }
}
