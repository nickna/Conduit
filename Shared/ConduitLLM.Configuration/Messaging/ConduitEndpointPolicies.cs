namespace ConduitLLM.Configuration.Messaging
{
    /// <summary>
    /// The four hand-tuned receive endpoints expressed declaratively as
    /// <see cref="EndpointPolicy"/> data. Each value is a faithful transcription of the
    /// imperative endpoint configuration in the Gateway's <c>Program.Messaging.cs</c>,
    /// so a backend can translate the descriptor back into identical behavior and the
    /// Wolverine backend can consume the same data (epic #909, issues #917 / #926).
    /// </summary>
    /// <remarks>
    /// Where a value is config-driven at runtime (video/image prefetch and concurrency
    /// come from <c>ConduitLLM:RabbitMQ</c>), the descriptor leaves it null to mean
    /// "inherit the bus/config default", and the host applies the configured value.
    /// </remarks>
    public static class ConduitEndpointPolicies
    {
        /// <summary>webhook-delivery: high-throughput, consumer-managed retry, circuit breaker, rate limit.</summary>
        public static readonly EndpointPolicy WebhookDelivery = new(
            Name: "webhook-delivery",
            PrefetchCount: 100,
            ConcurrentMessageLimit: 75,
            QuorumQueue: true,
            Retry: null,
            // Receiver failures are isolated by destination admission. A queue-wide
            // exception-count circuit would pause healthy destinations on exhaustion.
            CircuitBreaker: null,
            RateLimit: new RateLimitPolicy(100, TimeSpan.FromSeconds(1)),
            QueueArguments: new Dictionary<string, object>
            {
                ["x-delivery-limit"] = 10,
                ["x-max-length"] = 50000,
                ["x-overflow"] = "reject-publish",
            });

        /// <summary>video-generation-events: partition-key ordering (no single-active-consumer), incremental retry.</summary>
        public static readonly EndpointPolicy VideoGeneration = new(
            Name: "video-generation-events",
            QuorumQueue: true,
            ConfigureConsumeTopology: true,
            Retry: RetryPolicy.Incremental(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)),
            CircuitBreaker: new CircuitBreakerPolicy(TimeSpan.FromMinutes(2), TripThreshold: 20, ActiveThreshold: 5, TimeSpan.FromMinutes(10)));

        /// <summary>image-generation-events: single-active-consumer, incremental retry.</summary>
        public static readonly EndpointPolicy ImageGeneration = new(
            Name: "image-generation-events",
            SingleActiveConsumer: true,
            QuorumQueue: true,
            Retry: RetryPolicy.Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
            CircuitBreaker: new CircuitBreakerPolicy(TimeSpan.FromMinutes(1), TripThreshold: 15, ActiveThreshold: 5, TimeSpan.FromMinutes(5)));

        /// <summary>spend-update-events: strict ordering (concurrency 1 + single-active-consumer), immediate retry.</summary>
        public static readonly EndpointPolicy SpendUpdate = new(
            Name: "spend-update-events",
            PrefetchCount: 10,
            ConcurrentMessageLimit: 1,
            SingleActiveConsumer: true,
            QuorumQueue: true,
            Retry: RetryPolicy.Immediate(3),
            QueueArguments: new Dictionary<string, object>
            {
                ["x-max-length"] = 10000,
            });
    }
}
