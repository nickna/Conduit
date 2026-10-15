using System.Net;
using System.Net.Http.Headers;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Security.Authorization;
using ConduitLLM.Security.Interfaces;
using ConduitLLM.Security.Middleware;
using ConduitLLM.Security.Options;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using GatewaySecurityService = ConduitLLM.Gateway.Services.SecurityService;
using VirtualKeyEntity = ConduitLLM.Configuration.Entities.VirtualKey;

namespace ConduitLLM.Tests.Security.Middleware;

[Collection("AdminCompositionEnvironment")]
[Trait("Category", "Integration")]
[Trait("Component", "Security")]
public sealed class HealthEndpointBoundaryHttpTests
{
    private static readonly string[] HealthRoots = ["/health", "/api/health", "/v1/admin/health-status"];

    public static IEnumerable<object[]> HealthPaths()
    {
        foreach (var root in HealthRoots)
        {
            yield return [root];
            yield return [root + "/"];
            yield return [root + "/ready"];
            yield return [root.ToUpperInvariant() + "/READY/"];
        }
    }

    [Theory]
    [MemberData(nameof(HealthPaths))]
    public async Task HealthSegmentsKeepTheExternalHealthGateAndAcceptHealthKeysOrPrivatePeers(string path)
    {
        await using var external = await Pipeline.StartAsync(privatePeer: false);
        using var missing = await external.SendAsync(path);
        using var invalid = await external.SendAsync(path, healthKey: "invalid-health-key");
        using var ordinaryKey = await external.SendAsync(path, virtualKey: Pipeline.VirtualKey);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ordinaryKey.StatusCode);
        Assert.Equal("", await missing.Content.ReadAsStringAsync());

        using var authorized = await external.SendAsync(path, healthKey: Pipeline.HealthKey);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal("health", await authorized.Content.ReadAsStringAsync());
        external.Keys.Verify(service => service.ValidateVirtualKeyForAuthenticationAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        await using var local = await Pipeline.StartAsync(privatePeer: true);
        using var privateResponse = await local.SendAsync(path);
        Assert.Equal(HttpStatusCode.OK, privateResponse.StatusCode);
        Assert.Equal("health", await privateResponse.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/healthy")]
    [InlineData("/health-attacker-controlled")]
    [InlineData("/HEALTHY/")]
    [InlineData("/health.ready")]
    [InlineData("/api/healthy")]
    [InlineData("/api/health-attacker-controlled")]
    [InlineData("/API/HEALTHY/")]
    [InlineData("/v1/admin/health-statuses")]
    [InlineData("/V1/ADMIN/HEALTH-STATUS-extra/")]
    public async Task PrefixCollisionsRequireOrdinaryAuthenticationInsteadOfHealthAccess(string path)
    {
        await using var external = await Pipeline.StartAsync(privatePeer: false);
        using var anonymous = await external.SendAsync(path);
        using var healthOnly = await external.SendAsync(path, healthKey: Pipeline.HealthKey);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, healthOnly.StatusCode);

        using var authenticated = await external.SendAsync(path, virtualKey: Pipeline.VirtualKey);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal("ordinary:101", await authenticated.Content.ReadAsStringAsync());
        external.Keys.Verify(service => service.ValidateVirtualKeyForAuthenticationAsync(
            Pipeline.VirtualKey, It.IsAny<string>()), Times.Once);

        await using var local = await Pipeline.StartAsync(privatePeer: true);
        using var privateResponse = await local.SendAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, privateResponse.StatusCode);
    }

    [Theory]
    [InlineData("/healthy")]
    [InlineData("/api/healthy")]
    [InlineData("/v1/admin/health-statuses")]
    public async Task AuthenticatedPrefixCollisionsStillConsumeOrdinarySecurityRateLimits(string path)
    {
        await using var pipeline = await Pipeline.StartAsync(privatePeer: false, rateLimit: true);
        using var first = await pipeline.SendAsync(path, virtualKey: Pipeline.VirtualKey);
        using var second = await pipeline.SendAsync(path, virtualKey: Pipeline.VirtualKey);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.True(second.Headers.Contains("Retry-After"));
        pipeline.Keys.Verify(service => service.ValidateVirtualKeyForAuthenticationAsync(
            Pipeline.VirtualKey, It.IsAny<string>()), Times.Exactly(2));
    }

    private sealed class Pipeline : IAsyncDisposable
    {
        public const string HealthKey = "boundary-test-health-key";
        public const string VirtualKey = "condt_boundary_test_key";
        private readonly string _previousHealthKey;
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        public Mock<IVirtualKeyRuntimeService> Keys { get; }

        private Pipeline(WebApplication app, Mock<IVirtualKeyRuntimeService> keys, string previousHealthKey)
        {
            _app = app;
            _client = app.GetTestClient();
            _previousHealthKey = previousHealthKey;
            Keys = keys;
        }

        public static async Task<Pipeline> StartAsync(bool privatePeer, bool rateLimit = false)
        {
            var previousHealthKey = Environment.GetEnvironmentVariable(HealthKeyAuthorizationHandler.HealthKeyEnvVar);
            Environment.SetEnvironmentVariable(HealthKeyAuthorizationHandler.HealthKeyEnvVar, HealthKey);
            WebApplication app = null;
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
                builder.Logging.ClearProviders();
                builder.WebHost.UseTestServer();
                // Register the actual Gateway schemes and authorization policies.
                global::Program.ConfigureSecurityServices(builder);
                var keys = new Mock<IVirtualKeyRuntimeService>(MockBehavior.Strict);
                keys.Setup(service => service.ValidateVirtualKeyForAuthenticationAsync(VirtualKey, It.IsAny<string>()))
                    .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKeyEntity
                    {
                        Id = 101, KeyName = "Boundary test", KeyHash = "boundary-test-hash", IsEnabled = true
                    }));
                builder.Services.AddSingleton(keys.Object);
                builder.Services.AddSingleton(Mock.Of<IEphemeralKeyService>());
                builder.Services.AddMemoryCache();
                var security = new GatewaySecurityOptions { UseDistributedTracking = false };
                security.FailedAuth.Enabled = false;
                security.IpFiltering.Enabled = false;
                security.RateLimiting.Enabled = rateLimit;
                security.RateLimiting.MaxRequests = 1;
                security.RateLimiting.Discovery.Enabled = false;
                security.RateLimiting.ExcludedPaths = [.. HealthRoots];
                builder.Services.AddSingleton(Options.Create(security));
                builder.Services.AddSingleton<IGatewaySecurityService, GatewaySecurityService>();
                builder.Services.AddSingleton<ISecurityService>(provider => provider.GetRequiredService<IGatewaySecurityService>());
                app = builder.Build();
                app.Use((context, next) =>
                {
                    // The test transport supplies the peer address; forwarded headers
                    // remain untrusted and are not used to select health access.
                    context.Connection.RemoteIpAddress = IPAddress.Parse(privatePeer ? "10.12.34.56" : "203.0.113.42");
                    return next(context);
                });
                app.UseRouting();
                app.UseHealthEndpointAuthorization();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseCoreApiSecurity();
                foreach (var root in HealthRoots)
                {
                    app.MapGet(root, () => Results.Text("health")).AllowAnonymous();
                    app.MapGet(root + "/{**healthPath}", () => Results.Text("health")).AllowAnonymous();
                }
                app.MapGet("/{**ordinaryPath}", (HttpContext context) => Results.Text("ordinary:" + context.User.FindFirst("VirtualKeyId")?.Value))
                    .RequireAuthorization("VirtualKeyAuthentication");
                await app.StartAsync();
                return new Pipeline(app, keys, previousHealthKey);
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                Environment.SetEnvironmentVariable(HealthKeyAuthorizationHandler.HealthKeyEnvVar, previousHealthKey);
                throw;
            }
        }

        public async Task<HttpResponseMessage> SendAsync(string path, string healthKey = null, string virtualKey = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (healthKey is not null) request.Headers.Add(HealthKeyAuthorizationHandler.HealthKeyHeaderName, healthKey);
            if (virtualKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", virtualKey);
            return await _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            try
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HealthKeyAuthorizationHandler.HealthKeyEnvVar, _previousHealthKey);
            }
        }
    }
}
