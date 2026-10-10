using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ConduitLLM.Admin.Security;
using ConduitLLM.Admin.Services;
using ConduitLLM.Security.Options;

namespace ConduitLLM.Tests.Admin.Security
{
    [Trait("Category", "Unit")]
    [Trait("Component", "Security")]
    [Collection("AdminMasterKeyEnvironment")]
    public class MasterKeyAuthenticationHandlerTests : IDisposable
    {
        private readonly Mock<IEphemeralMasterKeyService> _ephemeralKeyServiceMock;
        private readonly Mock<IConfiguration> _configurationMock;
        private readonly Mock<ILoggerFactory> _loggerFactoryMock;
        private readonly Mock<ILogger<MasterKeyAuthenticationHandler>> _loggerMock;

        public MasterKeyAuthenticationHandlerTests()
        {
            _ephemeralKeyServiceMock = new Mock<IEphemeralMasterKeyService>();
            _configurationMock = new Mock<IConfiguration>();
            _loggerFactoryMock = new Mock<ILoggerFactory>();
            _loggerMock = new Mock<ILogger<MasterKeyAuthenticationHandler>>();
            _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_loggerMock.Object);
        }

        public void Dispose()
        {
            // Clean up environment variable after each test
            Environment.SetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", null);
        }

        private async Task<AuthenticateResult> RunAuthenticationAsync(
            string? masterKey,
            Action<HttpContext>? configureContext = null,
            AdminSecurityOptions? securityOptions = null)
        {
            Environment.SetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", masterKey);

            var options = new MasterKeyAuthenticationSchemeOptions();
            var optionsMonitor = new Mock<IOptionsMonitor<MasterKeyAuthenticationSchemeOptions>>();
            optionsMonitor.Setup(o => o.Get(It.IsAny<string>())).Returns(options);
            optionsMonitor.Setup(o => o.CurrentValue).Returns(options);

            var scheme = new AuthenticationScheme("MasterKey", "MasterKey", typeof(MasterKeyAuthenticationHandler));

            var handler = new MasterKeyAuthenticationHandler(
                optionsMonitor.Object,
                _loggerFactoryMock.Object,
                UrlEncoder.Default,
                _configurationMock.Object,
                _ephemeralKeyServiceMock.Object,
                Options.Create(securityOptions ?? new AdminSecurityOptions()));

            var httpContext = new DefaultHttpContext();
            configureContext?.Invoke(httpContext);

            await handler.InitializeAsync(scheme, httpContext);
            return await handler.AuthenticateAsync();
        }

        [Fact]
        public async Task HandleAuthenticateAsync_HealthCheckPath_SucceedsWithoutKey()
        {
            var result = await RunAuthenticationAsync(null, ctx =>
            {
                ctx.Request.Path = "/health/live";
            });

            Assert.True(result.Succeeded);
            Assert.Equal("HealthCheck", result.Principal?.Identity?.Name);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_MissingKey_LogsWarningAndFails()
        {
            var result = await RunAuthenticationAsync("configured-key", ctx =>
            {
                ctx.Request.Path = "/api/test";
                // No key headers set
            });

            Assert.True(result.None || !result.Succeeded);

            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("no API key provided")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_InvalidKey_LogsWarningWithClientIp()
        {
            var result = await RunAuthenticationAsync("correct-key", ctx =>
            {
                ctx.Request.Path = "/api/test";
                ctx.Request.Headers["X-API-Key"] = "wrong-key";
                ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
            });

            Assert.False(result.Succeeded);

            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("invalid master key provided")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_ValidKey_SucceedsWithAdminClaims()
        {
            var result = await RunAuthenticationAsync("valid-key", ctx =>
            {
                ctx.Request.Path = "/api/test";
                ctx.Request.Headers["X-API-Key"] = "valid-key";
            });

            Assert.True(result.Succeeded);
            Assert.Equal("AdminUser", result.Principal?.Identity?.Name);
            Assert.True(result.Principal?.HasClaim("MasterKey", "true"));
        }

        [Fact]
        public async Task HandleAuthenticateAsync_CustomConfiguredHeader_Succeeds()
        {
            var result = await RunAuthenticationAsync(
                "valid-key",
                ctx =>
                {
                    ctx.Request.Path = "/api/test";
                    ctx.Request.Headers["X-Conduit-Admin-Key"] = "valid-key";
                },
                new AdminSecurityOptions
                {
                    ApiAuth = new ApiAuthOptions
                    {
                        ApiKeyHeader = "X-Conduit-Admin-Key",
                        AlternativeHeaders = []
                    }
                });

            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_QueryTokenOnRetiredHubPath_IsRejected()
        {
            var result = await RunAuthenticationAsync("valid-key", ctx =>
            {
                ctx.Request.Path = "/hubs/admin-notifications";
                ctx.Request.QueryString = new QueryString("?access_token=valid-key");
            });

            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_NonHubQueryToken_IsRejected()
        {
            var result = await RunAuthenticationAsync("valid-key", ctx =>
            {
                ctx.Request.Path = "/api/test";
                ctx.Request.QueryString = new QueryString("?access_token=valid-key");
            });

            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_MasterKeyNotConfigured_LogsError()
        {
            var result = await RunAuthenticationAsync(null, ctx =>
            {
                ctx.Request.Path = "/api/test";
                ctx.Request.Headers["X-API-Key"] = "some-key";
            });

            Assert.False(result.Succeeded);

            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Backend auth key is not configured")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_ValidEphemeralKey_SucceedsAndLogs()
        {
            _ephemeralKeyServiceMock.Setup(s => s.ValidateAndConsumeKeyAsync("emk_valid123"))
                .ReturnsAsync(true);

            var result = await RunAuthenticationAsync("master-key", ctx =>
            {
                ctx.Request.Path = "/api/test";
                ctx.Request.Headers["X-API-Key"] = "emk_valid123";
            });

            Assert.True(result.Succeeded);

            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Authenticated via ephemeral master key")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task HandleAuthenticateAsync_ExpiredEphemeralKey_LogsWarningAndFails()
        {
            _ephemeralKeyServiceMock.Setup(s => s.ValidateAndConsumeKeyAsync("emk_expired123"))
                .ReturnsAsync(false);
            _ephemeralKeyServiceMock.Setup(s => s.KeyExistsAsync("emk_expired123"))
                .ReturnsAsync(true);

            var result = await RunAuthenticationAsync("master-key", ctx =>
            {
                ctx.Request.Path = "/api/test";
                ctx.Request.Headers["X-API-Key"] = "emk_expired123";
            });

            Assert.False(result.Succeeded);

            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Ephemeral master key validation failed")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}
