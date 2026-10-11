using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.RateLimiting;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Http.Middleware
{
    [Trait("Category", "Unit")]
    [Trait("Component", "Http")]
    public class VirtualKeyRateLimitMiddlewareTests
    {
        private readonly Mock<IVirtualKeyRateLimitService> _mockService = new();
        private readonly Mock<IConcurrencyRateLimitService> _mockConcurrency = new();
        private readonly Mock<IRateLimitFailurePolicy> _mockFailurePolicy = new();
        private readonly RateLimitOptions _options = new();
        private bool _nextCalled;

        private VirtualKeyRateLimitMiddleware CreateMiddleware()
        {
            return new VirtualKeyRateLimitMiddleware(
                next: _ => { _nextCalled = true; return Task.CompletedTask; },
                rateLimitService: _mockService.Object,
                concurrencyService: _mockConcurrency.Object,
                failurePolicy: _mockFailurePolicy.Object,
                options: _options,
                logger: NullLogger<VirtualKeyRateLimitMiddleware>.Instance);
        }

        private VirtualKeyRateLimitMiddleware CreateMiddleware(RequestDelegate next)
        {
            return new VirtualKeyRateLimitMiddleware(
                next: next,
                rateLimitService: _mockService.Object,
                concurrencyService: _mockConcurrency.Object,
                failurePolicy: _mockFailurePolicy.Object,
                options: _options,
                logger: NullLogger<VirtualKeyRateLimitMiddleware>.Instance);
        }

        private static DefaultHttpContext NewContext(string path = "/v1/chat/completions")
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Path = path;
            ctx.Response.Body = new MemoryStream();
            return ctx;
        }

        [Theory]
        [InlineData("/health")]
        [InlineData("/health/ready")]
        [InlineData("/metrics")]
        [InlineData("/v1/conduit/media/public/foo")]
        [InlineData("/hubs/images")]
        public async Task Skips_excluded_paths_without_calling_service(string path)
        {
            var ctx = NewContext(path);
            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeTrue();
            _mockService.Verify(s => s.CheckRateLimitAsync(It.IsAny<string>(), It.IsAny<RequestRateLimits>(), It.IsAny<int?>(), It.IsAny<RequestRateLimits>()),
                Times.Never);
        }

        [Fact]
        public async Task Passes_through_when_no_virtual_key_hash_in_items()
        {
            // Backend-scheme requests (CONDUIT_API_TO_API_BACKEND_AUTH_KEY) authenticate but
            // don't stash VirtualKey.KeyHash, so they bypass rate limiting.
            var ctx = NewContext();
            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeTrue();
            _mockService.Verify(s => s.CheckRateLimitAsync(It.IsAny<string>(), It.IsAny<RequestRateLimits>(), It.IsAny<int?>(), It.IsAny<RequestRateLimits>()),
                Times.Never);
        }

        [Fact]
        public async Task Passes_through_when_both_limits_null()
        {
            // null/null = unlimited per business rule. No Redis round-trip.
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.RateLimitRpm"] = null;
            ctx.Items["VirtualKey.RateLimitRpd"] = null;

            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeTrue();
            _mockService.Verify(s => s.CheckRateLimitAsync(It.IsAny<string>(), It.IsAny<RequestRateLimits>(), It.IsAny<int?>(), It.IsAny<RequestRateLimits>()),
                Times.Never);
        }

        [Fact]
        public async Task Passes_through_when_both_limits_zero_or_negative()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.RateLimitRpm"] = 0;
            ctx.Items["VirtualKey.RateLimitRpd"] = 0;

            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeTrue();
            _mockService.Verify(s => s.CheckRateLimitAsync(It.IsAny<string>(), It.IsAny<RequestRateLimits>(), It.IsAny<int?>(), It.IsAny<RequestRateLimits>()),
                Times.Never);
        }

        [Fact]
        public async Task Allows_request_and_sets_headers_when_service_returns_allowed()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.RateLimitRpm"] = 60;
            ctx.Items["VirtualKey.RateLimitRpd"] = null;

            _mockService.Setup(s => s.CheckRateLimitAsync("hash-abc", new RequestRateLimits(60, null)))
                .ReturnsAsync(new RateLimitCheckResult
                {
                    IsAllowed = true,
                    Limit = 60,
                    RequestsRemaining = 42,
                    ResetsAt = DateTime.UtcNow.AddSeconds(30),
                    LimitType = "RPM"
                });

            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeTrue();
            ctx.Response.Headers["X-RateLimit-Limit"].ToString().Should().Be("60");
            ctx.Response.Headers["X-RateLimit-Remaining"].ToString().Should().Be("42");
            ctx.Response.Headers["X-RateLimit-Scope"].ToString().Should().Be("RPM");
            ctx.Response.Headers.ContainsKey("X-RateLimit-Reset").Should().BeTrue();
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        [Theory]
        [InlineData("/v1/chat/completions")]
        [InlineData("/v1/models")]
        [InlineData("/v1/models/")]
        [InlineData("/v1/models/example")]
        public async Task Returns_429_with_Retry_After_when_service_rejects(string path)
        {
            var ctx = NewContext(path);
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.RateLimitRpm"] = 10;
            ctx.Items["VirtualKey.RateLimitRpd"] = null;

            _mockService.Setup(s => s.CheckRateLimitAsync("hash-abc", new RequestRateLimits(10, null)))
                .ReturnsAsync(new RateLimitCheckResult
                {
                    IsAllowed = false,
                    Limit = 10,
                    RequestsRemaining = 0,
                    ResetsAt = DateTime.UtcNow.AddSeconds(45),
                    LimitType = "RPM"
                });

            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeFalse();
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            ctx.Response.Headers.ContainsKey("Retry-After").Should().BeTrue();
            int.Parse(ctx.Response.Headers["Retry-After"].ToString()).Should().BeGreaterThan(0);
            ctx.Response.Headers["X-RateLimit-Limit"].ToString().Should().Be("10");
            ctx.Response.Headers["X-RateLimit-Scope"].ToString().Should().Be("RPM");
            ctx.Response.ContentType.Should().Contain("application/json");
        }

        [Fact]
        public async Task Retry_After_rounds_up_so_the_advertised_retry_actually_succeeds()
        {
            // The window frees 4.2s from now. Truncating to 4 would send the client back
            // before capacity exists; the hint must be 5.
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.RateLimitRpm"] = 10;

            var resetsAt = DateTime.UtcNow.AddMilliseconds(4200);
            _mockService.Setup(s => s.CheckRateLimitAsync("hash-abc", new RequestRateLimits(10, null)))
                .ReturnsAsync(new RateLimitCheckResult
                {
                    IsAllowed = false,
                    Limit = 10,
                    RequestsRemaining = 0,
                    ResetsAt = resetsAt,
                    LimitType = "RPM"
                });

            await CreateMiddleware().InvokeAsync(ctx);

            var retryAfter = int.Parse(ctx.Response.Headers["Retry-After"].ToString());
            retryAfter.Should().Be(5);

            // X-RateLimit-Reset must land on or after the true expiry, never before it.
            var reset = long.Parse(ctx.Response.Headers["X-RateLimit-Reset"].ToString());
            reset.Should().BeGreaterThanOrEqualTo(new DateTimeOffset(resetsAt).ToUnixTimeSeconds());
        }

        [Fact]
        public async Task Reports_the_window_reset_verbatim_rather_than_a_calendar_boundary()
        {
            // #1206: RPD enforcement is a rolling 24h window, so a key exhausted at 23:50
            // must not be told it recovers at midnight.
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.RateLimitRpd"] = 1000;

            var resetsAt = DateTime.UtcNow.AddHours(23);
            _mockService.Setup(s => s.CheckRateLimitAsync("hash-abc", new RequestRateLimits(null, 1000)))
                .ReturnsAsync(new RateLimitCheckResult
                {
                    IsAllowed = false,
                    Limit = 1000,
                    RequestsRemaining = 0,
                    ResetsAt = resetsAt,
                    LimitType = "RPD"
                });

            await CreateMiddleware().InvokeAsync(ctx);

            var retryAfter = int.Parse(ctx.Response.Headers["Retry-After"].ToString());
            retryAfter.Should().BeInRange(23 * 3600 - 5, 23 * 3600 + 5);
            ctx.Response.Headers["X-RateLimit-Scope"].ToString().Should().Be("RPD");
        }

        [Fact]
        public async Task Fails_open_when_service_throws()
        {
            // If Redis is down, we'd rather let the request through than tank the gateway.
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.RateLimitRpm"] = 60;

            _mockService.Setup(s => s.CheckRateLimitAsync(It.IsAny<string>(), It.IsAny<RequestRateLimits>(), It.IsAny<int?>(), It.IsAny<RequestRateLimits>()))
                .ThrowsAsync(new InvalidOperationException("Redis is required for secure distributed rate limiting"));

            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeTrue();
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        [Fact]
        public async Task Acquires_and_releases_a_concurrency_slot_around_the_request()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.MaxParallelRequests"] = 4;

            var slot = new ConcurrencySlot("rate:vk:hash-abc:concurrency", "slot-1");
            _mockConcurrency.Setup(s => s.TryAcquireAsync(It.IsAny<HttpContext>()))
                .ReturnsAsync(new ConcurrencyDecision(true, 4, 1, slot));

            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeTrue();
            _mockConcurrency.Verify(s => s.ReleaseAsync(slot), Times.Once);
            _mockService.Verify(s => s.CheckRateLimitAsync(It.IsAny<string>(), It.IsAny<RequestRateLimits>(), It.IsAny<int?>(), It.IsAny<RequestRateLimits>()),
                Times.Never, "a key with only a concurrency cap needs no window check");
        }

        [Fact]
        public async Task Releases_the_concurrency_slot_when_the_pipeline_throws()
        {
            // A provider error or a client abort must not leak the slot for the whole TTL.
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.MaxParallelRequests"] = 4;

            var slot = new ConcurrencySlot("rate:vk:hash-abc:concurrency", "slot-1");
            _mockConcurrency.Setup(s => s.TryAcquireAsync(It.IsAny<HttpContext>()))
                .ReturnsAsync(new ConcurrencyDecision(true, 4, 1, slot));

            var middleware = CreateMiddleware(_ => throw new InvalidOperationException("upstream exploded"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(ctx));

            _mockConcurrency.Verify(s => s.ReleaseAsync(slot), Times.Once);
        }

        [Fact]
        public async Task Releases_the_concurrency_slot_when_the_client_aborts()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.MaxParallelRequests"] = 4;

            var slot = new ConcurrencySlot("rate:vk:hash-abc:concurrency", "slot-1");
            _mockConcurrency.Setup(s => s.TryAcquireAsync(It.IsAny<HttpContext>()))
                .ReturnsAsync(new ConcurrencyDecision(true, 4, 1, slot));

            var middleware = CreateMiddleware(_ => throw new OperationCanceledException());

            await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(ctx));

            _mockConcurrency.Verify(s => s.ReleaseAsync(slot), Times.Once);
        }

        [Fact]
        public async Task Returns_429_with_concurrency_scope_when_the_cap_is_reached()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.MaxParallelRequests"] = 4;

            _mockConcurrency.Setup(s => s.TryAcquireAsync(It.IsAny<HttpContext>()))
                .ReturnsAsync(new ConcurrencyDecision(false, 4, 4, null));

            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeFalse();
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            ctx.Response.Headers["X-RateLimit-Scope"].ToString().Should().Be("concurrency");
            ctx.Response.Headers["X-RateLimit-Limit"].ToString().Should().Be("4");
            ctx.Response.Headers["X-RateLimit-Remaining"].ToString().Should().Be("0");

            // Concurrency frees when some other request ends, which is unknowable — a short
            // constant is advertised rather than a computed instant.
            int.Parse(ctx.Response.Headers["Retry-After"].ToString())
                .Should().Be(_options.ConcurrencyRetryAfterSeconds);
            _mockConcurrency.Verify(s => s.ReleaseAsync(It.IsAny<ConcurrencySlot>()), Times.Never);
        }

        [Fact]
        public async Task Fails_open_when_the_concurrency_store_is_unavailable()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.MaxParallelRequests"] = 1;

            _mockConcurrency.Setup(s => s.TryAcquireAsync(It.IsAny<HttpContext>()))
                .ThrowsAsync(new InvalidOperationException("redis is down"));

            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeTrue();
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        [Fact]
        public async Task Checks_concurrency_only_after_the_request_windows_admit()
        {
            // A request already rejected for RPM must not take — and then have to release — a slot.
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.RateLimitRpm"] = 10;
            ctx.Items["VirtualKey.MaxParallelRequests"] = 4;

            _mockService.Setup(s => s.CheckRateLimitAsync("hash-abc", new RequestRateLimits(10, null)))
                .ReturnsAsync(new RateLimitCheckResult
                {
                    IsAllowed = false,
                    Limit = 10,
                    ResetsAt = DateTime.UtcNow.AddSeconds(5),
                    LimitType = "RPM"
                });

            await CreateMiddleware().InvokeAsync(ctx);

            ctx.Response.Headers["X-RateLimit-Scope"].ToString().Should().Be("RPM");
            _mockConcurrency.Verify(s => s.TryAcquireAsync(It.IsAny<HttpContext>()), Times.Never);
        }

        [Fact]
        public async Task Forwards_the_saturation_fraction_for_a_low_priority_key_in_a_group()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.GroupId"] = 7;
            ctx.Items["VirtualKeyGroup.RateLimitRpm"] = 1000;
            ctx.Items["VirtualKey.RateLimitPriority"] = RateLimitSaturationPolicy.LowPriority;

            _mockService.Setup(s => s.CheckRateLimitAsync(It.IsAny<string>(), It.IsAny<RequestRateLimits>(), It.IsAny<int?>(), It.IsAny<RequestRateLimits>(), It.IsAny<double?>()))
                .ReturnsAsync(new RateLimitCheckResult { IsAllowed = true, Limit = 800, RequestsRemaining = 100, LimitType = "group:RPM:saturation" });

            await CreateMiddleware().InvokeAsync(ctx);

            _mockService.Verify(s => s.CheckRateLimitAsync(
                "hash-abc", new RequestRateLimits(null, null), 7, new RequestRateLimits(1000, null),
                _options.PrioritySaturationThreshold), Times.Once);
            _nextCalled.Should().BeTrue();
        }

        [Fact]
        public async Task Forwards_no_saturation_fraction_for_a_normal_priority_key()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.GroupId"] = 7;
            ctx.Items["VirtualKeyGroup.RateLimitRpm"] = 1000;

            _mockService.Setup(s => s.CheckRateLimitAsync(It.IsAny<string>(), It.IsAny<RequestRateLimits>(), It.IsAny<int?>(), It.IsAny<RequestRateLimits>(), It.IsAny<double?>()))
                .ReturnsAsync(new RateLimitCheckResult { IsAllowed = true, Limit = 1000, RequestsRemaining = 900, LimitType = "group:RPM" });

            await CreateMiddleware().InvokeAsync(ctx);

            _mockService.Verify(s => s.CheckRateLimitAsync(
                "hash-abc", new RequestRateLimits(null, null), 7, new RequestRateLimits(1000, null),
                null), Times.Once);
        }

        [Fact]
        public async Task Returns_429_with_the_saturation_scope_when_the_group_sheds_the_key()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.GroupId"] = 7;
            ctx.Items["VirtualKeyGroup.RateLimitRpm"] = 1000;
            ctx.Items["VirtualKey.RateLimitPriority"] = RateLimitSaturationPolicy.LowPriority;

            _mockService.Setup(s => s.CheckRateLimitAsync(It.IsAny<string>(), It.IsAny<RequestRateLimits>(), It.IsAny<int?>(), It.IsAny<RequestRateLimits>(), It.IsAny<double?>()))
                .ReturnsAsync(new RateLimitCheckResult
                {
                    IsAllowed = false,
                    Limit = 800,
                    RequestsRemaining = 0,
                    ResetsAt = DateTime.UtcNow.AddSeconds(10),
                    LimitType = "group:RPM:saturation"
                });

            await CreateMiddleware().InvokeAsync(ctx);

            _nextCalled.Should().BeFalse();
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);

            // The distinct scope is the contract: a caller must be able to tell "your tenant is
            // busy" from "you are over your own limit".
            ctx.Response.Headers["X-RateLimit-Scope"].ToString().Should().Be("group:RPM:saturation");

            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            body.Should().Contain("saturated");
        }

        [Fact]
        public async Task Forwards_both_RPM_and_RPD_when_both_configured()
        {
            var ctx = NewContext();
            ctx.Items["VirtualKey.KeyHash"] = "hash-abc";
            ctx.Items["VirtualKey.RateLimitRpm"] = 60;
            ctx.Items["VirtualKey.RateLimitRpd"] = 10000;

            _mockService.Setup(s => s.CheckRateLimitAsync("hash-abc", new RequestRateLimits(60, 10000)))
                .ReturnsAsync(new RateLimitCheckResult { IsAllowed = true, Limit = 60, RequestsRemaining = 50, LimitType = "RPM" });

            await CreateMiddleware().InvokeAsync(ctx);

            _mockService.Verify(s => s.CheckRateLimitAsync("hash-abc", new RequestRateLimits(60, 10000)), Times.Once);
            _nextCalled.Should().BeTrue();
        }
    }
}
