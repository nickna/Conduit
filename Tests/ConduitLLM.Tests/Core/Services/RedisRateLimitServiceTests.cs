using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Services;
using Xunit.Abstractions;

namespace ConduitLLM.Tests.Core.Services
{
    /// <summary>
    /// Integration tests for Redis-based distributed rate limiting
    /// Verifies that rate limits are properly enforced across multiple instances
    /// </summary>
    /// <remarks>
    /// These run against the Redis service container in CI, where TEST_REDIS_CONNECTION is set
    /// and an unreachable server fails rather than skips — see <see cref="RedisTestServer"/>.
    /// </remarks>
    [Trait("Category", "Unit")]
    [Trait("Component", "RedisRateLimitService")]
    public class RedisRateLimitServiceTests : IAsyncLifetime
    {
        private readonly Mock<ILogger<RedisVirtualKeyRateLimitService>> _mockLogger = new();
        private readonly Mock<ILogger<RedisSignalRRateLimitService>> _mockSignalRLogger = new();
        private readonly string _testKeyPrefix = RedisTestServer.NewKeyPrefix("rate-limit-service");

        private IConnectionMultiplexer _redis;
        private IDatabase _db;
        private RedisVirtualKeyRateLimitService _rateLimitService;
        private RedisSignalRRateLimitService _signalRService;
        private readonly ITestOutputHelper _output;

        public RedisRateLimitServiceTests(ITestOutputHelper output) => _output = output;

        public Task InitializeAsync() => Task.CompletedTask;

        /// <summary>
        /// Skips when Redis is genuinely absent, and fails when it was declared mandatory but
        /// unreachable — a Redis-less CI run must not report these as passing.
        /// </summary>
        private void SkipIfRedisNotAvailable()
        {
            _redis = RedisTestServer.Require();
            _db = _redis.GetDatabase();
            _rateLimitService ??= new RedisVirtualKeyRateLimitService(_redis, _mockLogger.Object);
            _signalRService ??= new RedisSignalRRateLimitService(_redis, _mockSignalRLogger.Object);
        }

        [SkippableFact]
        public async Task CheckRateLimitAsync_RPM_ShouldEnforceMinuteLimit()
        {
            SkipIfRedisNotAvailable();
            
            // Arrange
            var virtualKeyHash = _testKeyPrefix + "rpm-test";
            var rpmLimit = 5;
            var allowedRequests = 0;
            var deniedRequests = 0;

            // Act - Make 10 requests, only 5 should succeed
            for (int i = 0; i < 10; i++)
            {
                var result = await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(rpmLimit, null));
                
                if (result.IsAllowed)
                    allowedRequests++;
                else
                    deniedRequests++;
            }

            // Assert
            Assert.Equal(rpmLimit, allowedRequests);
            Assert.Equal(5, deniedRequests);
        }

        [SkippableFact]
        public async Task CheckRateLimitAsync_RPD_ShouldEnforceDailyLimit()
        {
            SkipIfRedisNotAvailable();
            
            // Arrange
            var virtualKeyHash = _testKeyPrefix + "rpd-test";
            var rpdLimit = 10;
            var allowedRequests = 0;
            var deniedRequests = 0;

            // Act - Make 15 requests, only 10 should succeed
            for (int i = 0; i < 15; i++)
            {
                var result = await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(null, rpdLimit));
                
                if (result.IsAllowed)
                    allowedRequests++;
                else
                    deniedRequests++;
            }

            // Assert
            Assert.Equal(rpdLimit, allowedRequests);
            Assert.Equal(5, deniedRequests);
        }

        [SkippableFact]
        public async Task CheckRateLimitAsync_MultipleInstances_ShouldShareRateLimitState()
        {
            SkipIfRedisNotAvailable();
            
            // Arrange
            var virtualKeyHash = _testKeyPrefix + "multi-instance-test";
            var rpmLimit = 10;
            
            // Create multiple service instances (simulating multiple servers)
            var service1 = new RedisVirtualKeyRateLimitService(_redis, _mockLogger.Object);
            var service2 = new RedisVirtualKeyRateLimitService(_redis, _mockLogger.Object);
            var service3 = new RedisVirtualKeyRateLimitService(_redis, _mockLogger.Object);

            var totalAllowed = 0;
            var totalDenied = 0;

            // Act - Each instance makes 5 requests (15 total), only 10 should succeed
            var tasks = new List<Task>();
            
            // Instance 1
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 5; i++)
                {
                    var result = await service1.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(rpmLimit, null));
                    if (result.IsAllowed)
                        Interlocked.Increment(ref totalAllowed);
                    else
                        Interlocked.Increment(ref totalDenied);
                }
            }));

            // Instance 2
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 5; i++)
                {
                    var result = await service2.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(rpmLimit, null));
                    if (result.IsAllowed)
                        Interlocked.Increment(ref totalAllowed);
                    else
                        Interlocked.Increment(ref totalDenied);
                }
            }));

            // Instance 3
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 5; i++)
                {
                    var result = await service3.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(rpmLimit, null));
                    if (result.IsAllowed)
                        Interlocked.Increment(ref totalAllowed);
                    else
                        Interlocked.Increment(ref totalDenied);
                }
            }));

            await Task.WhenAll(tasks);

            // Assert
            Assert.Equal(rpmLimit, totalAllowed);
            Assert.Equal(5, totalDenied);
        }

        [SkippableFact]
        public async Task SignalRRateLimitService_ConnectionTracking_ShouldBeDistributed()
        {
            SkipIfRedisNotAvailable();
            
            // Arrange
            var virtualKeyHash = _testKeyPrefix + "signalr-conn-test";
            
            // Create multiple service instances
            var service1 = new RedisSignalRRateLimitService(_redis, _mockSignalRLogger.Object);
            var service2 = new RedisSignalRRateLimitService(_redis, _mockSignalRLogger.Object);

            // Act
            var count1 = await service1.IncrementConnectionCountAsync(virtualKeyHash);
            var count2 = await service2.IncrementConnectionCountAsync(virtualKeyHash);
            var count3 = await service1.IncrementConnectionCountAsync(virtualKeyHash);
            
            var currentCount = await service2.GetConnectionCountAsync(virtualKeyHash);
            
            await service1.DecrementConnectionCountAsync(virtualKeyHash);
            var afterDecrement = await service2.GetConnectionCountAsync(virtualKeyHash);

            // Assert
            Assert.Equal(1, count1);
            Assert.Equal(2, count2);
            Assert.Equal(3, count3);
            Assert.Equal(3, currentCount);
            Assert.Equal(2, afterDecrement);
        }

        [SkippableFact]
        public async Task SignalRRateLimitService_MethodInvocation_ShouldEnforceRPMLimit()
        {
            SkipIfRedisNotAvailable();
            
            // Arrange
            var virtualKeyHash = _testKeyPrefix + "signalr-rpm-test";
            var rpmLimit = 3;
            var service = new RedisSignalRRateLimitService(_redis, _mockSignalRLogger.Object);

            // Act
            var results = new List<SignalRRateLimitResult>();
            for (int i = 0; i < 5; i++)
            {
                var result = await service.CheckMethodInvocationAsync(virtualKeyHash, rpmLimit, null);
                results.Add(result);
            }

            // Assert
            Assert.Equal(3, results.Count(r => r.IsAllowed));
            Assert.Equal(2, results.Count(r => !r.IsAllowed));
            Assert.All(results.Where(r => !r.IsAllowed), r => 
            {
                Assert.Equal("RPM", r.LimitType);
                Assert.Contains("Rate limit exceeded", r.DenialReason);
            });
        }

        [SkippableFact]
        public async Task SlidingWindow_ShouldExpireOldRequests()
        {
            SkipIfRedisNotAvailable();
            
            // Arrange
            var virtualKeyHash = _testKeyPrefix + "sliding-window-test";
            var rpmLimit = 2;
            
            // Act
            // Make 2 requests (should succeed)
            var result1 = await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(rpmLimit, null));
            var result2 = await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(rpmLimit, null));
            
            // Third request should be denied
            var result3 = await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(rpmLimit, null));
            
            // Wait for window to expire (simplified test - in production this is 60 seconds)
            // For testing, we can manually clear the key to simulate expiration
            await _db.KeyDeleteAsync(RedisKeys.RateLimit.VirtualKeyRpm(virtualKeyHash));
            
            // Request after window expiration should succeed
            var result4 = await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(rpmLimit, null));

            // Assert
            Assert.True(result1.IsAllowed);
            Assert.True(result2.IsAllowed);
            Assert.False(result3.IsAllowed);
            Assert.True(result4.IsAllowed);
        }

        [SkippableFact]
        public async Task RateLimitUsage_ShouldReturnAccurateStatistics()
        {
            SkipIfRedisNotAvailable();
            
            // Arrange
            var virtualKeyHash = _testKeyPrefix + "usage-stats-test"; // Use unique key with test prefix
            var rpmLimit = 5;
            var rpdLimit = 20;

            // Act
            // Make some requests and verify they succeed
            for (int i = 0; i < 3; i++)
            {
                var result = await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(rpmLimit, rpdLimit));
                Assert.True(result.IsAllowed, $"Request {i+1} should be allowed");
                // Verify the request was counted
                Assert.True(result.RequestsRemaining >= 0, $"Request {i+1} should have remaining count");
            }

            var usage = await _rateLimitService.GetUsageAsync(virtualKeyHash);

            // Debug info if test fails
            if (usage.RequestsThisMinute != 3)
            {
                // Try to get the raw data from Redis to understand what's happening
                var db = _redis.GetDatabase();
                var rpmKey = RedisKeys.RateLimit.VirtualKeyRpm(virtualKeyHash);
                var rpmCount = await db.SortedSetLengthAsync(rpmKey);
                Assert.Equal(3, rpmCount); // This will fail with more info
            }

            // Assert
            Assert.Equal(3, usage.RequestsThisMinute);
            Assert.Equal(3, usage.RequestsToday);
            Assert.True(usage.MinuteWindowStart <= DateTime.UtcNow);
            Assert.True(usage.DayWindowStart <= DateTime.UtcNow);
        }

        [SkippableFact]
        public async Task RemoveRateLimits_ShouldClearAllData()
        {
            SkipIfRedisNotAvailable();
            
            // Arrange
            var virtualKeyHash = _testKeyPrefix + "remove-test";
            
            // Make some requests to populate data
            await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(10, 100));

            var before = await _rateLimitService.GetUsageAsync(virtualKeyHash);
            Assert.Equal(1, before.RequestsThisMinute);

            // Act
            await _rateLimitService.RemoveRateLimitsAsync(virtualKeyHash);

            // Verify data is removed
            var usage = await _rateLimitService.GetUsageAsync(virtualKeyHash);

            // Assert
            Assert.Equal(0, usage.RequestsThisMinute);
            Assert.Equal(0, usage.RequestsToday);
            Assert.Equal(0, usage.TokensThisMinute);
            Assert.Equal(0, usage.RequestsInFlight);
        }

        [SkippableFact]
        public async Task GetUsageAsync_ReportsEveryWindowThatGovernsTheKey()
        {
            SkipIfRedisNotAvailable();

            var virtualKeyHash = _testKeyPrefix + "usage-all-windows";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var limiter = new SlidingWindowRateLimiter(_redis, _mockLogger.Object);

            // Two requests, 1,500 tokens reserved, one slot held.
            await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(10, 100));
            await _rateLimitService.CheckRateLimitAsync(virtualKeyHash, new RequestRateLimits(10, 100));
            await limiter.CheckAsync(
                new[]
                {
                    new RateLimitWindow(
                        RedisKeys.RateLimit.VirtualKeyTpm(virtualKeyHash), "TPM", 60_000, 100_000, 1_500, UnitWeight: false)
                },
                now);
            await limiter.CheckAsync(
                new[] { new RateLimitWindow(RedisKeys.RateLimit.VirtualKeyConcurrency(virtualKeyHash), "concurrency", 900_000, 5) },
                now);

            var usage = await _rateLimitService.GetUsageAsync(virtualKeyHash);

            Assert.Equal(2, usage.RequestsThisMinute);
            Assert.Equal(2, usage.RequestsToday);
            Assert.Equal(1_500, usage.TokensThisMinute);
            Assert.Equal(1, usage.RequestsInFlight);

            // A key in no group reports no group figures at all, rather than zeroes that would
            // read as "the group is idle".
            Assert.Null(usage.GroupRequestsThisMinute);
            Assert.Null(usage.GroupTokensThisMinute);
        }

        // The multiplexer is shared across the suite, so clean up keys but leave it open.
        public Task DisposeAsync() => RedisTestServer.CleanupAsync(_testKeyPrefix, _output.WriteLine);
    }
}
