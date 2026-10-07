using System;

using ConduitLLM.Configuration.Messaging;

using AwesomeAssertions;

using Xunit;

namespace ConduitLLM.Tests.Messaging
{
    /// <summary>
    /// Drift guard for the declarative endpoint policy descriptors (#917): each value must
    /// match the imperative configuration in the Gateway's Program.Messaging.cs so the
    /// backends emit identical behavior.
    /// </summary>
    public class EndpointPolicyTests
    {
        [Fact]
        public void WebhookDelivery_MatchesCurrentConfig()
        {
            var p = ConduitEndpointPolicies.WebhookDelivery;

            p.Name.Should().Be("webhook-delivery");
            p.PrefetchCount.Should().Be(100);
            p.ConcurrentMessageLimit.Should().Be(75);
            p.QuorumQueue.Should().BeTrue();
            p.SingleActiveConsumer.Should().BeFalse();
            p.Retry.Should().BeNull();
        p.CircuitBreaker.Should().BeNull("receiver exhaustion must not pause healthy destinations");
            p.RateLimit.Should().Be(new RateLimitPolicy(100, TimeSpan.FromSeconds(1)));
            p.QueueArguments!["x-delivery-limit"].Should().Be(10);
            p.QueueArguments!["x-max-length"].Should().Be(50000);
            p.QueueArguments!["x-overflow"].Should().Be("reject-publish");
        }

        [Fact]
        public void VideoGeneration_MatchesCurrentConfig()
        {
            var p = ConduitEndpointPolicies.VideoGeneration;

            p.Name.Should().Be("video-generation-events");
            p.QuorumQueue.Should().BeTrue();
            p.ConfigureConsumeTopology.Should().BeTrue();
            p.SingleActiveConsumer.Should().BeFalse(); // partition-key ordering, NOT single-active-consumer
            p.Retry.Should().Be(RetryPolicy.Incremental(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
            p.CircuitBreaker.Should().Be(new CircuitBreakerPolicy(TimeSpan.FromMinutes(2), 20, 5, TimeSpan.FromMinutes(10)));
        }

        [Fact]
        public void ImageGeneration_MatchesCurrentConfig()
        {
            var p = ConduitEndpointPolicies.ImageGeneration;

            p.Name.Should().Be("image-generation-events");
            p.QuorumQueue.Should().BeTrue();
            p.SingleActiveConsumer.Should().BeTrue();
            p.Retry.Should().Be(RetryPolicy.Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
            p.CircuitBreaker.Should().Be(new CircuitBreakerPolicy(TimeSpan.FromMinutes(1), 15, 5, TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void SpendUpdate_MatchesCurrentConfig()
        {
            var p = ConduitEndpointPolicies.SpendUpdate;

            p.Name.Should().Be("spend-update-events");
            p.PrefetchCount.Should().Be(10);
            p.ConcurrentMessageLimit.Should().Be(1); // strict sequential per partition
            p.SingleActiveConsumer.Should().BeTrue();
            p.QuorumQueue.Should().BeTrue();
            p.Retry.Should().Be(RetryPolicy.Immediate(3));
            p.CircuitBreaker.Should().BeNull();
            p.RateLimit.Should().BeNull();
            p.QueueArguments!["x-max-length"].Should().Be(10000);
        }
    }
}
