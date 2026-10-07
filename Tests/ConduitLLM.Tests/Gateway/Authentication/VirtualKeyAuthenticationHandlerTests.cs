using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit.Abstractions;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Authentication;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Security.Options;

namespace ConduitLLM.Tests.Http.Authentication
{
    /// <summary>
    /// Unit tests for VirtualKeyAuthenticationHandler
    /// </summary>
    public class VirtualKeyAuthenticationHandlerTests : TestBase
    {
        private readonly Mock<IVirtualKeyService> _virtualKeyServiceMock;
        private readonly Mock<IEphemeralKeyService> _ephemeralKeyServiceMock;
        private readonly Mock<IOptionsMonitor<AuthenticationSchemeOptions>> _optionsMock;
        private readonly Mock<ILoggerFactory> _loggerFactoryMock;
        private readonly Mock<ILogger<VirtualKeyAuthenticationHandler>> _loggerMock;
        private readonly VirtualKeyAuthenticationHandler _handler;
        private readonly DefaultHttpContext _httpContext;

        public VirtualKeyAuthenticationHandlerTests(ITestOutputHelper output) : base(output)
        {
            _virtualKeyServiceMock = new Mock<IVirtualKeyService>();
            _ephemeralKeyServiceMock = new Mock<IEphemeralKeyService>();
            _optionsMock = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
            _loggerFactoryMock = new Mock<ILoggerFactory>();
            _loggerMock = CreateLogger<VirtualKeyAuthenticationHandler>();
            
            _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
                .Returns(_loggerMock.Object);

            _optionsMock.Setup(o => o.Get(It.IsAny<string>()))
                .Returns(new AuthenticationSchemeOptions());

            _handler = new VirtualKeyAuthenticationHandler(
                _optionsMock.Object,
                _loggerFactoryMock.Object,
                UrlEncoder.Default,
                _virtualKeyServiceMock.Object,
                _ephemeralKeyServiceMock.Object,
                Options.Create(new GatewaySecurityOptions()));

            _httpContext = new DefaultHttpContext();
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithValidBearerToken_ReturnsSuccessResult()
        {
            // Arrange
            var virtualKey = CreateValidVirtualKey();
            var keyValue = "condt_test123";
            
            _httpContext.Request.Headers["Authorization"] = $"Bearer {keyValue}";
            _httpContext.Request.Path = "/v1/chat/completions";
            
            _virtualKeyServiceMock.Setup(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(virtualKey));

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.True(result.Succeeded);
            Assert.NotNull(result.Principal);
            Assert.Equal("VirtualKey", result.Principal.Identity.AuthenticationType);
            
            var claims = result.Principal.Claims;
            Assert.Contains(claims, c => c.Type == ClaimTypes.Name && c.Value == "Test Key");
            Assert.Contains(claims, c => c.Type == "VirtualKeyId" && c.Value == "1");
            Assert.Contains(claims, c => c.Type == "VirtualKey" && c.Value == keyValue);
            
            // Verify context items are set
            Assert.Equal(1, _httpContext.Items["VirtualKeyId"]);
            Assert.Equal(keyValue, _httpContext.Items["VirtualKey"]);
            Assert.Same(virtualKey, _httpContext.Items["VirtualKeyEntity"]);
            Assert.NotNull(_httpContext.Items["RequestStartTime"]);
            
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null), Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithValidApiKeyHeader_ReturnsSuccessResult()
        {
            // Arrange
            var virtualKey = CreateValidVirtualKey();
            var keyValue = "condt_test456";
            
            _httpContext.Request.Headers["X-API-Key"] = keyValue;
            _httpContext.Request.Path = "/v1/models";
            
            _virtualKeyServiceMock.Setup(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(virtualKey));

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.True(result.Succeeded);
            Assert.NotNull(result.Principal);
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null), Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithMissingVirtualKey_ReturnsNoResult()
        {
            // Arrange
            _httpContext.Request.Path = "/v1/chat/completions";
            // No Authorization or X-API-Key header

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.False(result.Succeeded);
            Assert.True(result.None); // Returns NoResult to allow other auth schemes
            
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithInvalidVirtualKey_ReturnsFailure()
        {
            // Arrange
            var keyValue = "invalid_key";
            
            _httpContext.Request.Headers["Authorization"] = $"Bearer {keyValue}";
            _httpContext.Request.Path = "/v1/chat/completions";
            
            _virtualKeyServiceMock.Setup(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null))
                .ReturnsAsync(VirtualKeyValidationOutcome.Failure(
                    VirtualKeyValidationFailureCodes.KeyNotFound,
                    401,
                    "Virtual key was not found."));

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.False(result.Succeeded);
            Assert.Equal("Invalid Virtual Key", result.Failure.Message);
            
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null), Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithZeroBalanceKey_ReturnsSuccess()
        {
            // Arrange - This tests our fix where authentication succeeds even with $0.00 balance
            var virtualKey = CreateValidVirtualKey(); // Valid key but potentially $0.00 balance
            var keyValue = "condt_zerobalance";
            
            _httpContext.Request.Headers["Authorization"] = $"Bearer {keyValue}";
            _httpContext.Request.Path = "/v1/conduit/models/gpt-4/metadata";
            
            // ValidateVirtualKeyForAuthenticationAsync should succeed even with $0.00 balance
            _virtualKeyServiceMock.Setup(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(virtualKey));

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.True(result.Succeeded); // Authentication succeeds regardless of balance
            Assert.NotNull(result.Principal);
            
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null), Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithExcludedPath_ReturnsAnonymousSuccess()
        {
            // Arrange
            _httpContext.Request.Path = "/health";
            // No authentication headers required for excluded paths

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.True(result.Succeeded);
            Assert.NotNull(result.Principal);
            Assert.False(result.Principal.Identity.IsAuthenticated); // Anonymous identity
            
            // Verify service was never called for excluded paths
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData("/health")]
        [InlineData("/health/ready")]
        [InlineData("/health/live")]
        [InlineData("/v1/conduit/media/public")]
        public async Task HandleAuthenticateAsync_WithExcludedPaths_SkipsAuthentication(string path)
        {
            // Arrange
            _httpContext.Request.Path = path;

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.True(result.Succeeded);
            Assert.False(result.Principal.Identity.IsAuthenticated);
            
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithMetricsPath_AuthenticatesVirtualKey()
        {
            var virtualKey = CreateValidVirtualKey();
            var keyValue = "condt_metrics";

            _httpContext.Request.Headers["Authorization"] = $"Bearer {keyValue}";
            _httpContext.Request.Path = "/metrics";
            _virtualKeyServiceMock.Setup(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(virtualKey));

            await InitializeHandler();

            var result = await _handler.AuthenticateAsync();

            Assert.True(result.Succeeded);
            Assert.True(result.Principal.Identity.IsAuthenticated);
            _virtualKeyServiceMock.Verify(
                s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null),
                Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithSignalRConnection_ExtractsFromQueryString()
        {
            // Arrange
            var virtualKey = CreateValidVirtualKey();
            var keyValue = "condt_signalr";
            
            _httpContext.Request.Path = "/hubs/chat";
            _httpContext.Request.QueryString = new QueryString($"?access_token={keyValue}");
            
            _virtualKeyServiceMock.Setup(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(virtualKey));

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.True(result.Succeeded);
            Assert.NotNull(result.Principal);
            
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null), Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithServiceException_ReturnsFailure()
        {
            // Arrange
            var keyValue = "condt_exception";
            
            _httpContext.Request.Headers["Authorization"] = $"Bearer {keyValue}";
            _httpContext.Request.Path = "/v1/chat/completions";
            
            _virtualKeyServiceMock.Setup(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null))
                .ThrowsAsync(new Exception("Database connection failed"));

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.False(result.Succeeded);
            Assert.Equal("Authentication error", result.Failure.Message);
            
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(keyValue, null), Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithValidEphemeralKey_ReturnsSuccessResult()
        {
            // Arrange
            var ephemeralKey = "ek_test123";
            var actualVirtualKey = "condt_actual456";
            var virtualKeyEntity = CreateValidVirtualKey();
            
            _httpContext.Request.Headers["Authorization"] = $"Bearer {ephemeralKey}";
            _httpContext.Request.Path = "/v1/conduit/media/upload";
            
            var keyData = new ConduitLLM.Gateway.Models.EphemeralKeyData
            {
                VirtualKeyId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = DateTimeOffset.UtcNow
            };
            
            _ephemeralKeyServiceMock.Setup(s => s.GetKeyDataAsync(ephemeralKey))
                .ReturnsAsync(keyData);
            
            _ephemeralKeyServiceMock.Setup(s => s.GetVirtualKeyAsync(ephemeralKey))
                .ReturnsAsync(actualVirtualKey);
            
            _virtualKeyServiceMock.Setup(s => s.ValidateVirtualKeyForAuthenticationAsync(actualVirtualKey, null))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(virtualKeyEntity));

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.True(result.Succeeded);
            Assert.NotNull(result.Principal);
            Assert.Equal("VirtualKey", result.Principal.Identity.AuthenticationType);
            
            // Verify context items are set
            Assert.Equal(1, _httpContext.Items["VirtualKeyId"]);
            Assert.Equal(actualVirtualKey, _httpContext.Items["VirtualKey"]);
            Assert.True((bool)_httpContext.Items["IsEphemeralKey"]);
            Assert.Equal("EphemeralKey", _httpContext.Items["AuthType"]);
            
            _ephemeralKeyServiceMock.Verify(s => s.GetKeyDataAsync(ephemeralKey), Times.Once);
            _ephemeralKeyServiceMock.Verify(s => s.GetVirtualKeyAsync(ephemeralKey), Times.Once);
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(actualVirtualKey, null), Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_WithExpiredEphemeralKey_ReturnsFailure()
        {
            // Arrange
            var ephemeralKey = "ek_expired";
            
            _httpContext.Request.Headers["Authorization"] = $"Bearer {ephemeralKey}";
            _httpContext.Request.Path = "/v1/conduit/media/upload";
            
            var keyData = new ConduitLLM.Gateway.Models.EphemeralKeyData
            {
                VirtualKeyId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5), // Expired
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            };
            
            _ephemeralKeyServiceMock.Setup(s => s.GetKeyDataAsync(ephemeralKey))
                .ReturnsAsync(keyData);

            await InitializeHandler();

            // Act
            var result = await _handler.AuthenticateAsync();

            // Assert
            Assert.False(result.Succeeded);
            Assert.Equal("Ephemeral key expired", result.Failure.Message);
            
            _ephemeralKeyServiceMock.Verify(s => s.GetKeyDataAsync(ephemeralKey), Times.Once);
            _ephemeralKeyServiceMock.Verify(s => s.GetVirtualKeyAsync(It.IsAny<string>()), Times.Never);
            _virtualKeyServiceMock.Verify(s => s.ValidateVirtualKeyForAuthenticationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        /// <summary>
        /// Creates a valid virtual key for testing
        /// </summary>
        private VirtualKey CreateValidVirtualKey()
        {
            return new VirtualKey
            {
                Id = 1,
                KeyName = "Test Key",
                KeyHash = "test-hash",
                IsEnabled = true,
                VirtualKeyGroupId = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Initializes the handler with the HTTP context
        /// </summary>
        private async Task InitializeHandler()
        {
            var scheme = new AuthenticationScheme("VirtualKey", "Virtual Key", typeof(VirtualKeyAuthenticationHandler));
            await _handler.InitializeAsync(scheme, _httpContext);
        }
    }
}
