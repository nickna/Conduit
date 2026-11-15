using System.Text.Json;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Middleware;
using ConduitLLM.Core.Exceptions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Admin.Middleware
{
    [Trait("Category", "Unit")]
    [Trait("Component", "Admin")]
    public class AdminExceptionMiddlewareTests
    {
        private readonly Mock<RequestDelegate> _mockNext;
        private readonly Mock<ILogger<AdminExceptionMiddleware>> _mockLogger;
        private readonly Mock<IWebHostEnvironment> _mockEnvironment;
        private readonly AdminExceptionMiddleware _middleware;
        private readonly DefaultHttpContext _httpContext;

        public AdminExceptionMiddlewareTests()
        {
            _mockNext = new Mock<RequestDelegate>();
            _mockLogger = new Mock<ILogger<AdminExceptionMiddleware>>();
            _mockEnvironment = new Mock<IWebHostEnvironment>();

            _mockEnvironment.Setup(x => x.EnvironmentName).Returns(Environments.Production);

            _middleware = new AdminExceptionMiddleware(
                _mockNext.Object,
                _mockLogger.Object,
                _mockEnvironment.Object);

            _httpContext = new DefaultHttpContext();
            _httpContext.Response.Body = new MemoryStream();
            _httpContext.TraceIdentifier = "test-trace-id";
        }

        [Fact]
        public async Task ModelNotFoundException_Returns404()
        {
            // Arrange
            var modelName = "gpt-5";
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new ModelNotFoundException(modelName));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(404, _httpContext.Response.StatusCode);
            Assert.Equal("application/problem+json", _httpContext.Response.ContentType);

            var (error, code) = GetErrorResponse(_httpContext);

            Assert.Contains(modelName, error);
            Assert.Equal(AdminErrorCodes.NotFound, code);
        }

        [Fact]
        public async Task InvalidRequestException_Returns400()
        {
            // Arrange
            var exception = new InvalidRequestException("Invalid parameter", "invalid_param", "test_field");
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(exception);

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(400, _httpContext.Response.StatusCode);

            var (error, code) = GetErrorResponse(_httpContext);

            Assert.Equal("Invalid parameter", error);
            Assert.Equal(AdminErrorCodes.ValidationError, code);
        }

        [Fact]
        public async Task AuthorizationException_Returns403()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new AuthorizationException("Access denied"));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(403, _httpContext.Response.StatusCode);

            var (error, code) = GetErrorResponse(_httpContext);

            Assert.Equal("Access denied", error);
            Assert.Equal(AdminErrorCodes.Forbidden, code);
        }

        [Fact]
        public async Task RequestTimeoutException_Returns408()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new RequestTimeoutException("Request timed out"));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(408, _httpContext.Response.StatusCode);

            var (error, code) = GetErrorResponse(_httpContext);

            Assert.Equal("Request timed out", error);
            Assert.Equal(AdminErrorCodes.RequestTimeout, code);
        }

        [Fact]
        public async Task RateLimitException_Returns429_WithRetryAfterHeader()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new RateLimitExceededException("Rate limit exceeded", 60));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(429, _httpContext.Response.StatusCode);
            Assert.Equal("60", _httpContext.Response.Headers["Retry-After"]);

            var (error, code) = GetErrorResponse(_httpContext);

            Assert.Equal("Rate limit exceeded", error);
            Assert.Equal(AdminErrorCodes.RateLimitExceeded, code);
        }

        [Fact]
        public async Task ServiceUnavailableException_Returns503()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new ServiceUnavailableException("Service unavailable", "TestService"));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(503, _httpContext.Response.StatusCode);

            var (error, code) = GetErrorResponse(_httpContext);

            Assert.Equal("Service unavailable", error);
            Assert.Equal(AdminErrorCodes.ServiceUnavailable, code);
        }

        [Fact]
        public async Task ArgumentNullException_Returns400()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new ArgumentNullException("param"));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(400, _httpContext.Response.StatusCode);

            var (error, code) = GetErrorResponse(_httpContext);

            Assert.Equal("Required parameter is missing", error);
            Assert.Equal(AdminErrorCodes.ValidationError, code);
        }

        [Fact]
        public async Task KeyNotFoundException_Returns404()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new KeyNotFoundException("Resource not found"));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(404, _httpContext.Response.StatusCode);

            var (error, code) = GetErrorResponse(_httpContext);

            Assert.Equal("The requested resource was not found", error);
            Assert.Equal(AdminErrorCodes.NotFound, code);
        }

        [Fact]
        public async Task UnhandledException_Returns500_GenericMessage()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new Exception("secret details"));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(500, _httpContext.Response.StatusCode);

            var (error, code) = GetErrorResponse(_httpContext);

            // In production, should not expose internal details
            Assert.Equal("An unexpected error occurred", error);
            Assert.Equal(AdminErrorCodes.InternalError, code);
        }

        [Fact]
        public async Task UnhandledException_InDevelopment_ShowsDetails()
        {
            // Arrange
            _mockEnvironment.Setup(x => x.EnvironmentName).Returns(Environments.Development);
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new Exception("Detailed error message"));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal(500, _httpContext.Response.StatusCode);

            var (error, _) = GetErrorResponse(_httpContext);

            // In development, should show actual error message
            Assert.Equal("Detailed error message", error);
        }

        [Fact]
        public async Task XRequestIdHeader_IsSet()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new Exception("test"));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal("test-trace-id", _httpContext.Response.Headers["X-Request-Id"]);
        }

        [Fact]
        public async Task ResponseAlreadyStarted_DoesNotThrow()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new Exception("test"));

            MarkResponseStarted("test-trace-id");

            // Act & Assert - should not throw
            await _middleware.InvokeAsync(_httpContext);
            AssertStartedResponseUnchanged();
        }

        [Fact]
        public async Task NoException_PassesThrough()
        {
            // Arrange
            var nextCalled = false;
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .Callback<HttpContext>(_ => nextCalled = true)
                .Returns(Task.CompletedTask);

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.True(nextCalled);
            Assert.Equal(200, _httpContext.Response.StatusCode);
        }

        [Fact]
        public async Task ContentType_IsProblemJson()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new Exception("test"));

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert
            Assert.Equal("application/problem+json", _httpContext.Response.ContentType);
        }

        [Fact]
        public async Task LogsServerError_AtErrorLevel_WithExceptionAndRequestDetails()
        {
            // Arrange — ConfigurationException maps to 500, so it is a genuine operator concern.
            var exception = new ConfigurationException("something broke");
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(exception);

            _httpContext.Request.Method = "POST";
            _httpContext.Request.Path = "/api/providers";

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert — logged at Error with the exception and the trace id
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("test-trace-id")),
                    exception,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task LogsClientError_AtWarningLevel_NotErrorLevel()
        {
            // The log severity must track the mapped status: a 4xx is the caller's problem, not the
            // operator's. Logging every unknown model or malformed body at Error buries real faults.
            var exception = new InvalidOperationException("something the caller did");
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(exception);

            _httpContext.Request.Method = "POST";
            _httpContext.Request.Path = "/api/providers";

            await _middleware.InvokeAsync(_httpContext);

            Assert.Equal(400, _httpContext.Response.StatusCode);

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("test-trace-id")),
                    exception,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task ResponseAlreadyStarted_LogsWarning()
        {
            // Arrange
            _mockNext.Setup(x => x(It.IsAny<HttpContext>()))
                .ThrowsAsync(new Exception("test"));

            MarkResponseStarted("started-trace-id");

            // Act
            await _middleware.InvokeAsync(_httpContext);

            // Assert — LogWarning was called about response already started
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("started-trace-id")),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
            AssertStartedResponseUnchanged();
        }

        private void MarkResponseStarted(string traceIdentifier)
        {
            _httpContext.TraceIdentifier = traceIdentifier;
            _httpContext.Request.Method = "GET";
            _httpContext.Request.Path = "/test";
            _httpContext.Response.Body.Write("already-sent"u8);
            var headers = new HeaderDictionary
            {
                ["Content-Type"] = "application/json",
                ["x-sent"] = "original"
            };
            _httpContext.Features.Set<IHttpResponseFeature>(new StartedResponseFeature
            {
                StatusCode = StatusCodes.Status200OK,
                Headers = headers
            });
            headers.IsReadOnly = true;
        }

        private void AssertStartedResponseUnchanged()
        {
            Assert.Equal(StatusCodes.Status200OK, _httpContext.Response.StatusCode);
            Assert.Equal("application/json", _httpContext.Response.ContentType);
            Assert.Equal("original", _httpContext.Response.Headers["x-sent"]);
            Assert.Equal(2, _httpContext.Response.Headers.Count);
            Assert.False(_httpContext.Response.Headers.ContainsKey("x-request-id"));
            Assert.Equal("already-sent"u8.ToArray(), ((MemoryStream)_httpContext.Response.Body).ToArray());
        }

        private sealed class StartedResponseFeature : HttpResponseFeature
        {
            public override bool HasStarted => true;
        }

        /// <summary>
        /// Deserializes the RFC 9457 response body and extracts detail and code.
        /// </summary>
        private static (string error, string? code) GetErrorResponse(HttpContext context)
        {
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            var body = reader.ReadToEnd();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var error = root.GetProperty("detail").GetString() ?? string.Empty;
            var code = root.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString()
                : null;

            return (error, code);
        }
    }
}
