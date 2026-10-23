using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Gateway.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Http.Middleware
{
    public class RedisAvailabilityMiddlewareTests
    {
        private readonly Mock<ILogger<RedisAvailabilityMiddleware>> _mockLogger;
        private readonly Mock<IRedisCircuitBreaker> _mockCircuitBreaker;
        private readonly RedisCircuitBreakerOptions _options;
        private readonly RedisAvailabilityMiddleware _middleware;
        private readonly Mock<RequestDelegate> _mockNext;

        public RedisAvailabilityMiddlewareTests()
        {
            _mockLogger = new Mock<ILogger<RedisAvailabilityMiddleware>>();
            _mockCircuitBreaker = new Mock<IRedisCircuitBreaker>();
            _mockNext = new Mock<RequestDelegate>();
            _options = new RedisCircuitBreakerOptions
            {
                OpenCircuitMessage = "Redis is currently unavailable",
                IncludeErrorDetails = true
            };

            _middleware = new RedisAvailabilityMiddleware(
                _mockNext.Object,
                _mockLogger.Object,
                Microsoft.Extensions.Options.Options.Create(_options));
        }

        [Fact]
        public async Task Middleware_Returns503_WhenCircuitIsOpen()
        {
            // Arrange
            var context = CreateHttpContext("/api/v1/test");
            _mockCircuitBreaker.Setup(x => x.IsOpen).Returns(true);
            _mockCircuitBreaker.Setup(x => x.Statistics).Returns(new CircuitBreakerStatistics
            {
                State = CircuitState.Open,
                TotalFailures = 10,
                RejectedRequests = 5,
                CircuitOpenedAt = DateTime.UtcNow.AddMinutes(-1),
                TimeUntilHalfOpen = TimeSpan.FromSeconds(30)
            });

            // Act
            await _middleware.InvokeAsync(context, _mockCircuitBreaker.Object);

            // Assert
            Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
            Assert.StartsWith("application/json", context.Response.ContentType);
            Assert.True(context.Response.Headers.ContainsKey("Retry-After"));
            Assert.Equal("30", context.Response.Headers["Retry-After"]);
            Assert.Equal("Open", context.Response.Headers["X-Circuit-Breaker-State"]);
            Assert.Equal("Degraded", context.Response.Headers["X-Service-Status"]);

            // Verify next middleware was NOT called
            _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Never);
        }

        [Fact]
        public async Task Middleware_PassesThrough_WhenCircuitIsClosed()
        {
            // Arrange
            var context = CreateHttpContext("/api/v1/test");
            _mockCircuitBreaker.Setup(x => x.IsOpen).Returns(false);

            // Act
            await _middleware.InvokeAsync(context, _mockCircuitBreaker.Object);

            // Assert
            _mockNext.Verify(x => x(context), Times.Once);
            Assert.Equal(200, context.Response.StatusCode); // Default status
        }

        [Fact]
        public async Task Middleware_BypassesHealthEndpoint_EvenWhenCircuitIsOpen()
        {
            // Arrange
            var context = CreateHttpContext("/health");
            _mockCircuitBreaker.Setup(x => x.IsOpen).Returns(true);

            // Act
            await _middleware.InvokeAsync(context, _mockCircuitBreaker.Object);

            // Assert
            _mockNext.Verify(x => x(context), Times.Once);
            Assert.Equal(200, context.Response.StatusCode);
        }

        [Fact]
        public async Task Middleware_BypassesMetricsEndpoint_EvenWhenCircuitIsOpen()
        {
            // Arrange
            var context = CreateHttpContext("/metrics");
            _mockCircuitBreaker.Setup(x => x.IsOpen).Returns(true);

            // Act
            await _middleware.InvokeAsync(context, _mockCircuitBreaker.Object);

            // Assert
            _mockNext.Verify(x => x(context), Times.Once);
            Assert.Equal(200, context.Response.StatusCode);
        }

        [Theory]
        [InlineData("/openapi/v1.json", false)]
        [InlineData("/scalar/v1", false)]
        [InlineData("/openapi/v1.json", true)]
        [InlineData("/scalar/v1", true)]
        [InlineData("/openapi", true)]
        [InlineData("/scalar", true)]
        [InlineData("/OPENAPI/v1.json", true)]
        public async Task Middleware_BypassesDocumentationEndpoints_EvenWhenCircuitIsOpen(
            string path, bool overrideBypassPaths)
        {
            var context = CreateHttpContext(path);
            _mockCircuitBreaker.Setup(x => x.IsOpen).Returns(true);
            if (overrideBypassPaths)
            {
                _options.BypassPaths.Clear();
            }

            await _middleware.InvokeAsync(context, _mockCircuitBreaker.Object);

            _mockNext.Verify(x => x(context), Times.Once);
            Assert.Equal(200, context.Response.StatusCode);
            Assert.False(context.Response.Headers.ContainsKey("X-Circuit-Breaker-State"));
        }

        [Fact]
        public void RedisCircuitBreakerOptions_DefaultConfiguration_IncludesCurrentDocumentationPrefixes()
        {
            Assert.Contains("/openapi", _options.BypassPaths);
            Assert.Contains("/scalar", _options.BypassPaths);
            Assert.DoesNotContain("/swagger", _options.BypassPaths);
        }

        [Theory]
        [InlineData("/v1/chat/completions")]
        [InlineData("/v1/embeddings")]
        [InlineData("/swagger/index.html")]
        [InlineData("/healthier")]
        [InlineData("/metrics-inference")]
        [InlineData("/openapi-private")]
        [InlineData("/scalarish")]
        public async Task Middleware_RejectsNonBypassEndpoints_WhenCircuitIsOpen(string path)
        {
            var context = CreateHttpContext(path);
            _mockCircuitBreaker.Setup(x => x.IsOpen).Returns(true);
            _mockCircuitBreaker.Setup(x => x.Statistics).Returns(new CircuitBreakerStatistics
            {
                State = CircuitState.Open
            });

            await _middleware.InvokeAsync(context, _mockCircuitBreaker.Object);

            _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Never);
            Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
        }

        [Theory]
        [InlineData("/diagnostics", "/diagnostics", true)]
        [InlineData("/diagnostics", "/diagnostics/status", true)]
        [InlineData("/DIAGNOSTICS", "/diagnostics/status", true)]
        [InlineData("/diagnostics", "/diagnostics-private", false)]
        [InlineData("/diagnostics/", "/diagnostics/", true)]
        [InlineData("/diagnostics/", "/diagnostics/status", true)]
        [InlineData("/diagnostics/", "/diagnostics", false)]
        [InlineData("/diagnostics/", "/diagnostics-private", false)]
        [InlineData("/v1/chat/completion", "/v1/chat/completions", false)]
        [InlineData("/v1/chat/completions", "/v1/chat/completions", true)]
        [InlineData("/v1/chat", "/v1/chat/completions", true)]
        [InlineData("/", "/v1/chat/completions", true)]
        [InlineData(null, "/v1/chat/completions", false)]
        [InlineData("", "/v1/chat/completions", false)]
        public async Task Middleware_ConfiguredBypassPath_RequiresExactPathOrDescendant(
            string bypassPath, string requestPath, bool shouldBypass)
        {
            var context = CreateHttpContext(requestPath);
            _options.BypassPaths.Clear();
            _options.BypassPaths.Add(bypassPath);
            _mockCircuitBreaker.Setup(x => x.IsOpen).Returns(true);
            _mockCircuitBreaker.Setup(x => x.Statistics).Returns(new CircuitBreakerStatistics
            {
                State = CircuitState.Open
            });

            await _middleware.InvokeAsync(context, _mockCircuitBreaker.Object);

            _mockNext.Verify(x => x(It.IsAny<HttpContext>()), shouldBypass ? Times.Once() : Times.Never());
            Assert.Equal(shouldBypass ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable,
                context.Response.StatusCode);
        }

        [Fact]
        public async Task Middleware_ReturnsCorrectErrorResponse_WhenCircuitIsOpen()
        {
            // Arrange
            var context = CreateHttpContext("/api/v1/test");
            var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            _mockCircuitBreaker.Setup(x => x.IsOpen).Returns(true);
            _mockCircuitBreaker.Setup(x => x.Statistics).Returns(new CircuitBreakerStatistics
            {
                State = CircuitState.Open,
                TotalFailures = 10,
                RejectedRequests = 5,
                LastFailureAt = DateTime.UtcNow.AddSeconds(-10),
                CircuitOpenedAt = DateTime.UtcNow.AddSeconds(-5),
                TimeUntilHalfOpen = TimeSpan.FromSeconds(25)
            });

            // Act
            await _middleware.InvokeAsync(context, _mockCircuitBreaker.Object);

            // Assert - Read and parse response body
            responseBody.Seek(0, SeekOrigin.Begin);
            var responseContent = new StreamReader(responseBody).ReadToEnd();
            var responseJson = JsonDocument.Parse(responseContent);
            
            Assert.Equal("service_unavailable", responseJson.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("Redis is currently unavailable", responseJson.RootElement.GetProperty("error").GetProperty("message").GetString());
            Assert.Equal("server_error", responseJson.RootElement.GetProperty("error").GetProperty("type").GetString());
            var metadata = responseJson.RootElement.GetProperty("error").GetProperty("metadata");
            Assert.Equal("Open", metadata.GetProperty("circuit_state").GetString());
            Assert.Equal(10, metadata.GetProperty("total_failures").GetInt64());
            Assert.Equal(5, metadata.GetProperty("rejected_requests").GetInt64());
            Assert.Equal(25, metadata.GetProperty("retry_after_seconds").GetDouble());
            Assert.False(responseJson.RootElement.TryGetProperty("request_id", out _));
        }

        private HttpContext CreateHttpContext(string path)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.Method = "GET";
            context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
            context.Response.Body = new MemoryStream();
            context.TraceIdentifier = Guid.NewGuid().ToString();
            context.RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();
            return context;
        }
    }
}
