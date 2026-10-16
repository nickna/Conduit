using System.Net;

using ConduitLLM.Security.Authorization;
using ConduitLLM.Security.Middleware;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Moq;

using Xunit;
using Xunit.Abstractions;

namespace ConduitLLM.Tests.Security.Middleware;

/// <summary>
/// Unit tests for <see cref="HealthEndpointAuthorizationMiddleware"/>.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "Security")]
[Collection("AdminCompositionEnvironment")]
public class HealthEndpointAuthorizationMiddlewareTests : TestBase
{
    private const string TestHealthKey = "test-health-monitoring-key-12345";
    private readonly Mock<ILogger<HealthEndpointAuthorizationMiddleware>> _loggerMock;
    private readonly string? _originalHealthKey = Environment.GetEnvironmentVariable(HealthKeyAuthorizationHandler.HealthKeyEnvVar);

    public HealthEndpointAuthorizationMiddlewareTests(ITestOutputHelper output) : base(output)
    {
        _loggerMock = CreateLogger<HealthEndpointAuthorizationMiddleware>();
    }

    private HealthEndpointAuthorizationMiddleware CreateMiddleware(
        RequestDelegate next,
        string? healthKey = TestHealthKey)
    {
        // Set the environment variable for the test
        if (healthKey != null)
        {
            Environment.SetEnvironmentVariable(HealthKeyAuthorizationHandler.HealthKeyEnvVar, healthKey);
        }
        else
        {
            Environment.SetEnvironmentVariable(HealthKeyAuthorizationHandler.HealthKeyEnvVar, null);
        }

        return new HealthEndpointAuthorizationMiddleware(next, _loggerMock.Object);
    }

    private static DefaultHttpContext CreateHttpContext(
        string path,
        IPAddress? remoteIpAddress = null,
        string? healthKeyHeader = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        if (remoteIpAddress != null)
        {
            context.Connection.RemoteIpAddress = remoteIpAddress;
        }

        if (healthKeyHeader != null)
        {
            context.Request.Headers[HealthKeyAuthorizationHandler.HealthKeyHeaderName] = healthKeyHeader;
        }

        return context;
    }

    [Fact]
    public async Task InvokeAsync_HealthEndpoint_PrivateNetwork_PassesThrough()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/health", IPAddress.Parse("10.0.0.1"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeTrue("Private network requests should pass through");
        context.Response.StatusCode.Should().NotBe(404);
    }

    [Fact]
    public async Task InvokeAsync_HealthEndpoint_ExternalWithValidKey_PassesThrough()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/health", IPAddress.Parse("203.0.113.1"), TestHealthKey);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeTrue("Valid health key should pass through");
        context.Response.StatusCode.Should().NotBe(404);
    }

    [Fact]
    public async Task InvokeAsync_HealthEndpoint_ExternalNoKey_Returns404()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/health", IPAddress.Parse("203.0.113.1"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeFalse("Unauthorized external requests should not pass through");
        context.Response.StatusCode.Should().Be(404, "Should return 404 to hide endpoint existence");
    }

    [Fact]
    public async Task InvokeAsync_HealthEndpoint_ExternalInvalidKey_Returns404()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/health", IPAddress.Parse("203.0.113.1"), "wrong-key");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeFalse("Invalid key should not pass through");
        context.Response.StatusCode.Should().Be(404, "Should return 404 to hide endpoint existence");
    }

    [Fact]
    public async Task InvokeAsync_NonHealthEndpoint_PassesThroughRegardless()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/api/chat/completions", IPAddress.Parse("203.0.113.1"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeTrue("Non-health endpoints should pass through regardless of IP/key");
    }

    [Fact]
    public async Task InvokeAsync_HealthLiveEndpoint_SameRulesApply()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/health/live", IPAddress.Parse("203.0.113.1"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeFalse("/health/live should be protected");
        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task InvokeAsync_HealthReadyEndpoint_SameRulesApply()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/health/ready", IPAddress.Parse("203.0.113.1"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeFalse("/health/ready should be protected");
        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task InvokeAsync_ApiHealthServicesEndpoint_SameRulesApply()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/v1/admin/health-status/services", IPAddress.Parse("203.0.113.1"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeFalse("/v1/admin/health-status/* should be protected");
        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task InvokeAsync_HealthSignalREndpoint_SameRulesApply()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/health/signalr", IPAddress.Parse("203.0.113.1"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeFalse("/health/signalr should be protected");
        context.Response.StatusCode.Should().Be(404);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("127.0.0.1")]
    public async Task InvokeAsync_VariousPrivateNetworkIPs_AllPassThrough(string ipAddress)
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/health", IPAddress.Parse(ipAddress));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeTrue($"IP {ipAddress} should be recognized as private network");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.1")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")] // Just outside 172.16-31 range
    public async Task InvokeAsync_VariousPublicIPs_AllReturn404WithoutKey(string ipAddress)
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/health", IPAddress.Parse(ipAddress));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeFalse($"IP {ipAddress} should be recognized as public");
        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task InvokeAsync_CaseSensitivePath_StillMatches()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/Health", IPAddress.Parse("203.0.113.1"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeFalse("/Health (uppercase) should still be protected");
        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task InvokeAsync_HealthyEndpoint_NotProtected()
    {
        // Arrange - /healthy is NOT /health, so it should pass through
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(next);
        var context = CreateHttpContext("/healthy", IPAddress.Parse("203.0.113.1"));

        // Act
        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue("/healthy is a different route segment from /health");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("/health/")]
    [InlineData("/HEALTH/READY/")]
    [InlineData("/health/deep/path")]
    [InlineData("/api/health")]
    [InlineData("/api/health/")]
    [InlineData("/API/HEALTH/ready")]
    [InlineData("/v1/admin/health-status")]
    [InlineData("/v1/admin/health-status/")]
    [InlineData("/V1/ADMIN/HEALTH-STATUS/services")]
    public async Task InvokeAsync_HealthRootsAndDescendants_KeepExternalUnauthorizedResponsesHidden(string path)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateHttpContext(path, IPAddress.Parse("203.0.113.1"));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/health-attacker-controlled")]
    [InlineData("/HEALTHY")]
    [InlineData("/health.json")]
    [InlineData("/api/healthy")]
    [InlineData("/api/health-attacker")]
    [InlineData("/v1/admin/health-status-attacker")]
    [InlineData("/v1/admin/health-status.json")]
    public async Task InvokeAsync_PrefixCollisions_AreHandledByTheOrdinaryDownstreamPipeline(string path)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext(path, IPAddress.Parse("203.0.113.1"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/health/")]
    [InlineData("/HEALTH/READY")]
    [InlineData("/api/health")]
    [InlineData("/API/HEALTH/ready")]
    [InlineData("/v1/admin/health-status")]
    [InlineData("/V1/ADMIN/HEALTH-STATUS/services")]
    public async Task InvokeAsync_HealthRootsAndDescendants_PreservePrivateNetworkAndValidKeyAccess(string path)
    {
        var calls = 0;
        var middleware = CreateMiddleware(context =>
        {
            calls++;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var privateRequest = CreateHttpContext(path, IPAddress.Parse("10.0.0.1"));
        var externalWithKey = CreateHttpContext(path, IPAddress.Parse("203.0.113.1"), TestHealthKey);

        await middleware.InvokeAsync(privateRequest);
        await middleware.InvokeAsync(externalWithKey);

        Assert.Equal(2, calls);
        Assert.Equal(StatusCodes.Status204NoContent, privateRequest.Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, externalWithKey.Response.StatusCode);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable(HealthKeyAuthorizationHandler.HealthKeyEnvVar, _originalHealthKey);
        }
        base.Dispose(disposing);
    }
}
