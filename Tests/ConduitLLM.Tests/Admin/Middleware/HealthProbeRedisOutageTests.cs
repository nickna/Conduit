using System.Net;

using ConduitLLM.Admin.Middleware;
using ConduitLLM.Admin.Services;
using ConduitLLM.Security.Interfaces;
using ConduitLLM.Security.Middleware;
using ConduitLLM.Security.Options;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using SecurityService = ConduitLLM.Admin.Services.SecurityService;

namespace ConduitLLM.Tests.Admin.Middleware;

public sealed class HealthProbeRedisOutageTests
{
    [Theory]
    [InlineData("/health", "GET", 503)]
    [InlineData("/health/live", "GET", 200)]
    [InlineData("/health/ready", "GET", 503)]
    [InlineData("/health/live/", "GET", 200)]
    [InlineData("/HEALTH/READY", "GET", 503)]
    [InlineData("/health/live", "HEAD", 200)]
    [InlineData("/health/ready", "HEAD", 503)]
    public async Task PrivateNetworkHealthProbes_ReachActualHealthChecksWithoutRedis(string path, string method, int expected)
    {
        var cache = OutageCache();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache.Object, memory);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISecurityService>(service);
        builder.Services.AddHealthChecks().AddCheck("database", () => HealthCheckResult.Unhealthy("database unavailable"), tags: ["ready"]);
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            return next(context);
        });
        app.UseHealthEndpointAuthorization();
        app.UseAdminSecurity();
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/live", new() { Predicate = registration => registration.Tags.Contains("live") });
        app.MapHealthChecks("/health/ready", new() { Predicate = registration => registration.Tags.Contains("ready") });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        cache.Verify(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("/health/live/details", "GET")]
    [InlineData("/health/ready-extra", "GET")]
    [InlineData("/healthiness", "GET")]
    [InlineData("/health/ready", "POST")]
    [InlineData("/api/health-status", "GET")]
    public async Task OtherPathsAndMethods_StillRunSecurityChecks(string path, string method)
    {
        var cache = OutageCache();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache.Object, memory);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        await Assert.ThrowsAsync<IOException>(() => service.IsRequestAllowedAsync(context));
        cache.Verify(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExternalProbeWithoutHealthKey_RemainsHiddenBeforeSecurityOrRedis()
    {
        var called = false;
        var gate = new HealthEndpointAuthorizationMiddleware(_ => { called = true; return Task.CompletedTask; },
            NullLogger<HealthEndpointAuthorizationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/live";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.1");
        await gate.InvokeAsync(context);
        Assert.False(called);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    private static Mock<IDistributedCache> OutageCache()
    {
        var cache = new Mock<IDistributedCache>(MockBehavior.Strict);
        cache.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("distributed cache unavailable"));
        return cache;
    }

    private static SecurityService CreateService(IDistributedCache cache, IMemoryCache memory) =>
        new(Microsoft.Extensions.Options.Options.Create(new AdminSecurityOptions { UseDistributedTracking = true }),
            new ConfigurationBuilder().Build(), NullLogger<SecurityService>.Instance, memory, cache);
}
