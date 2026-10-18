using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.Options
{
    /// <summary>
    /// What to do with a request whose rate limit could not be evaluated.
    /// </summary>
    public enum RateLimitFailureMode
    {
        /// <summary>Admit it. Rate limiting is defence-in-depth; a cache outage should not take the data plane with it.</summary>
        Open = 0,

        /// <summary>
        /// Reject it. For deployments where a limit is a hard commercial or safety boundary,
        /// unverifiable is not the same as permitted.
        /// </summary>
        Closed = 1
    }

    /// <summary>
    /// Deployment-wide knobs for rate-limit enforcement. Per-key ceilings live on the
    /// VirtualKey entity; these govern how those ceilings are applied.
    /// </summary>
    public class RateLimitOptions
    {
        /// <summary>
        /// Configuration section name.
        /// </summary>
        public const string SectionName = "RateLimiting";

        /// <summary>
        /// Completion tokens reserved when a request does not cap its own output.
        /// </summary>
        /// <remarks>
        /// Token limits are enforced by reserving an estimate before the provider is called and
        /// correcting it afterwards. A request that specifies no <c>max_tokens</c> has no
        /// declared ceiling, so without this cap a single call would reserve — and therefore
        /// block — the whole window. The reservation is corrected down to actual usage as soon
        /// as the response is billed, so a generous default costs little.
        /// </remarks>
        [Range(1, 1_000_000)]
        public int DefaultCompletionTokenBudget { get; set; } = 1024;

        /// <summary>
        /// Upper bound on the completion budget a request may reserve by declaring a large
        /// <c>max_tokens</c>. Prevents one request from parking the entire window.
        /// </summary>
        [Range(1, 10_000_000)]
        public int MaxCompletionTokenReservation { get; set; } = 32_768;

        /// <summary>
        /// How long a concurrency slot survives without being released.
        /// </summary>
        /// <remarks>
        /// Slots are normally returned the moment a request ends. This bound exists for the
        /// cases where that cannot happen — a killed pod, a severed connection — so a leaked
        /// slot frees itself instead of permanently shrinking the key's capacity. It must
        /// therefore exceed the longest legitimate request: streaming responses can run for
        /// minutes, and a slot that expires underneath a live request would let the key exceed
        /// its ceiling.
        /// </remarks>
        [Range(30, 86_400)]
        public int ConcurrencySlotTtlSeconds { get; set; } = 900;

        /// <summary>
        /// Retry-After advertised when a request is turned away at the concurrency ceiling.
        /// </summary>
        /// <remarks>
        /// Unlike a time window, concurrency frees up when some other request happens to
        /// finish, which is unknowable in advance. A short constant is the honest answer.
        /// </remarks>
        [Range(1, 300)]
        public int ConcurrencyRetryAfterSeconds { get; set; } = 1;

        /// <summary>
        /// What happens to a request whose limits cannot be evaluated. Defaults to admitting it.
        /// </summary>
        /// <remarks>
        /// Set by <c>CONDUIT_RATE_LIMIT_FAILURE_MODE=open|closed</c>. Fail-closed is an explicit
        /// opt-in because it converts a cache outage into a data-plane outage for every key that
        /// has a limit configured — keys with none never take a Redis round-trip and are
        /// therefore unaffected either way.
        /// </remarks>
        public RateLimitFailureMode FailureMode { get; set; } = RateLimitFailureMode.Open;

        /// <summary>
        /// Fraction of each group ceiling that low-priority keys are admitted against.
        /// </summary>
        /// <remarks>
        /// Set by <c>CONDUIT_RATE_LIMIT_SATURATION_THRESHOLD</c>. A key whose
        /// <c>RateLimitPriority</c> is below normal is checked against
        /// <c>floor(groupLimit × threshold)</c> instead of the full group ceiling, so once the
        /// group's shared window passes the threshold, low-priority keys are shed while
        /// normal- and high-priority keys still have the remaining headroom. Because the reduced
        /// ceiling is evaluated inside the same atomic window check as everything else, the
        /// decision cannot race the fill it is based on and there is no shedding state to flap.
        /// 1.0 disables shedding (the reduced ceiling equals the full one).
        /// </remarks>
        [Range(0.0, 1.0, MinimumIsExclusive = true)]
        public double PrioritySaturationThreshold { get; set; } = 0.8;

        /// <summary>
        /// How long a single observed failure is treated as an ongoing degradation.
        /// </summary>
        /// <remarks>
        /// Debouncing keeps a flapping store from producing a warning per request, and keeps the
        /// reported degraded state stable enough to alert on.
        /// </remarks>
        [Range(1, 3600)]
        public int FailureDebounceSeconds { get; set; } = 10;
    }
}
