#nullable enable

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Middleware;
using ConduitLLM.Admin.Security;
using ConduitLLM.Admin.Services;
using ConduitLLM.Core.Middleware;
using ConduitLLM.Security.Models;
using ConduitLLM.Security.Options;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;
using AdminProgram = ConduitLLM.Admin.Program;
using SharedSecurityService = ConduitLLM.Security.Interfaces.ISecurityService;

namespace ConduitLLM.Tests.Admin.Middleware;

[Collection("AdminCompositionEnvironment")]
public sealed class NativeAdminRuntimeProfileMiddlewareTests
{
    private const string Detail = "This management operation is not supported by the native-aot runtime profile.";

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ExcludedRequestsRejectBeforeUnreadableBodyServiceFilterOrHandler(string method)
    {
        var state = new Calls();
        await using var app = await CreateSentinelApp(method, state, unreadableBody: true);
        using var client = app.GetTestClient();
        using var request = Request(method, "/v1/admin/excluded", "{");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, deadline.Token);
        await AssertProblem(response, deadline.Token);
        Assert.Equal(0, state.BodyReads);
        Assert.Equal(0, state.Services);
        Assert.Equal(0, state.Filters);
        Assert.Equal(0, state.Handlers);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("name-case")]
    [InlineData("foreign-pattern")]
    [InlineData("foreign-method")]
    [InlineData("multiple-methods")]
    public async Task UnknownOrForgedSafeMetadataCannotEnterTheSelectedProfile(string mutation)
    {
        var builder = Builder();
        await using var app = builder.Build();
        var name = mutation switch { "missing" => null, "name-case" => "globalsettings_GetDefinitions", "unknown" => "Unknown_Operation", _ => "GlobalSettings_GetDefinitions" };
        var pattern = mutation == "foreign-pattern" ? "/v1/admin/provider-tools/" : "/v1/admin/global-settings/definitions";
        var methods = mutation switch { "foreign-method" => new[] { "POST" }, "multiple-methods" => new[] { "GET", "POST" }, _ => new[] { "GET" } };
        var metadata = new List<object> { new HttpMethodMetadata(methods) };
        if (name is not null) metadata.Add(new EndpointNameMetadata(name));
        var endpoint = new RouteEndpoint(_ => throw new InvalidOperationException("Handler reached."),
            RoutePatternFactory.Parse(pattern), 0, new EndpointMetadataCollection(metadata), "owned-forged-metadata");
        var context = new DefaultHttpContext { RequestServices = app.Services, TraceIdentifier = "owned-metadata" };
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);
        var nextCalls = 0;
        await new NativeAdminRuntimeProfileMiddleware(_ => { nextCalls++; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.Equal(501, context.Response.StatusCode);
        Assert.Equal(0, nextCalls);
    }

    [Theory]
    [InlineData("/V1/ADMIN/provider-tools/73")]
    [InlineData("/v1/admin/provider-tools/73/")]
    [InlineData("/v1/admin/provider-tools/73?unused=1")]
    [InlineData("/v1/admin/provider-tools/not-an-integer")]
    [InlineData("/v1/admin/provider-tools/a%2Fb")]
    [InlineData("/v1/admin/provider-tools/a%20b")]
    public async Task RejectionUsesMatchedPatternBeforeParameterConversion(string path)
    {
        var builder = Builder();
        await using var app = builder.Build();
        app.UseCorrelationId();
        app.UseMiddleware<NativeAdminRuntimeProfileMiddleware>();
        var calls = 0;
        app.MapGet("/v1/admin/provider-tools/{id}", (int id) => { calls++; return Results.NoContent(); })
            .WithName("ProviderTools_GetById");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(deadline.Token);
        try
        {
            using var client = app.GetTestClient();
            using var request = Request("GET", path);
            using var response = await client.SendAsync(request, deadline.Token);
            await AssertProblem(response, deadline.Token);
            Assert.Equal(0, calls);
        }
        finally { await Stop(app); }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task ProductionSecurityDenialPrecedesNativeCapabilityResponse(int status)
    {
        var state = new Calls();
        var security = new Mock<SharedSecurityService>(MockBehavior.Strict);
        security.Setup(service => service.IsRequestAllowedAsync(It.IsAny<HttpContext>()))
            .ReturnsAsync(SecurityCheckResult.Denied("owned security denial", status));
        await using var app = await CreateSentinelApp("POST", state, security: security.Object);
        using var client = app.GetTestClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = Request("POST", "/v1/admin/excluded", "{");
        using var response = await client.SendAsync(request, deadline.Token);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, state.Services);
        Assert.Equal(0, state.Filters);
        Assert.Equal(0, state.Handlers);
        security.Verify(service => service.IsRequestAllowedAsync(It.IsAny<HttpContext>()), Times.Once);
        security.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizationDenialPrecedesTheNativeBoundary()
    {
        var builder = Builder();
        builder.Services.AddAuthorization(options => options.AddPolicy("deny", policy => policy.RequireAssertion(_ => false)));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<NativeAdminRuntimeProfileMiddleware>();
        var calls = 0;
        app.MapGet("/v1/admin/excluded", () => { calls++; return Results.NoContent(); }).RequireAuthorization("deny");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(deadline.Token);
        try
        {
            using var client = app.GetTestClient();
            using var response = await client.GetAsync("/v1/admin/excluded", deadline.Token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, calls);
        }
        finally { await Stop(app); }
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics/")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/api/v1/admin/excluded")]
    public async Task NonManagementMatchedRoutesRemainOutsideTheBoundary(string path)
    {
        var builder = Builder();
        await using var app = builder.Build();
        app.UseMiddleware<NativeAdminRuntimeProfileMiddleware>();
        var calls = 0;
        app.MapGet(path, () => { calls++; return Results.NoContent(); });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(deadline.Token);
        try
        {
            using var client = app.GetTestClient();
            using var response = await client.GetAsync(path, deadline.Token);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(1, calls);
        }
        finally { await Stop(app); }
    }

    [Theory]
    [InlineData("GET", "/v1/admin/unmatched", 404)]
    [InlineData("GET", "/v1/admin/excluded", 405)]
    public async Task UnmatchedAndWrongMethodResponsesRemainRoutingDecisions(string method, string path, int expected)
    {
        var state = new Calls();
        await using var app = await CreateSentinelApp("POST", state);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = app.GetTestClient();
        using var request = Request(method, path);
        using var response = await client.SendAsync(request, deadline.Token);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, state.Services);
        Assert.Equal(0, state.Filters);
        Assert.Equal(0, state.Handlers);
    }

    [Fact]
    public async Task HeadRetainsActualGetRoutingSelection()
    {
        var observations = new List<(int Status, int Calls)>();
        foreach (var gated in new[] { false, true })
        {
            var builder = Builder();
            await using var app = builder.Build();
            if (gated) app.UseMiddleware<NativeAdminRuntimeProfileMiddleware>();
            var calls = 0;
            app.MapGet("/v1/admin/excluded", () => { calls++; return Results.NoContent(); });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StartAsync(deadline.Token);
            try
            {
                using var client = app.GetTestClient();
                using var request = Request("HEAD", "/v1/admin/excluded");
                using var response = await client.SendAsync(request, deadline.Token);
                observations.Add(((int)response.StatusCode, calls));
            }
            finally { await Stop(app); }
        }
        Assert.Contains(observations[0].Status, new[] { 204, 405 });
        Assert.Equal(observations[0].Status == 204 ? 501 : 405, observations[1].Status);
        Assert.Equal(0, observations[1].Calls);
    }

    [Fact]
    public async Task ActualProgramRegistrationRejectsOnlyTheNativeProfile()
    {
        var state = new Calls();
        await using var app = await CreateSentinelApp("POST", state, programRegistration: true, throwingService: false);
        using var client = app.GetTestClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = Request("POST", "/v1/admin/excluded", "{}");
        using var response = await client.SendAsync(request, deadline.Token);
#if CONDUIT_NATIVE_AOT
        await AssertProblem(response, deadline.Token);
        Assert.Equal(0, state.Services);
        Assert.Equal(0, state.Filters);
        Assert.Equal(0, state.Handlers);
#else
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, state.Services);
        Assert.Equal(1, state.Filters);
        Assert.Equal(1, state.Handlers);
#endif
    }

    [Fact]
    public async Task ActualCacheOnlyEphemeralRouteAndSelectedDefinitionsRetainSingleUseAuthentication()
    {
        const string master = "owned-unit119-master-key";
        var previous = Environment.GetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY");
        Environment.SetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", master);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            AdminProgram.ConfigureHttpServices(builder);
            builder.Services.AddMemoryCache();
            builder.Services.AddDistributedMemoryCache();
            builder.Services.Configure<AdminSecurityOptions>(options =>
            {
                options.FailedAuth.Enabled = false;
                options.RateLimiting.Enabled = false;
                options.IpFiltering.Enabled = false;
                options.UseDistributedTracking = false;
            });
            builder.Services.AddSingleton<IEphemeralMasterKeyService, EphemeralMasterKeyService>();
            builder.Services.AddSingleton<SharedSecurityService, SecurityService>();
            builder.Services.AddScoped<IAdminGlobalSettingService>(_ => throw new InvalidOperationException("Persistence resolved."));
            builder.Services.AddAuthentication("MasterKey")
                .AddScheme<MasterKeyAuthenticationSchemeOptions, MasterKeyAuthenticationHandler>("MasterKey", null);
            builder.Services.AddAuthorization(options => options.AddPolicy("MasterKeyPolicy", policy => policy.AddRequirements(new MasterKeyRequirement())));
            builder.Services.AddSingleton<IAuthorizationHandler, MasterKeyAuthorizationHandler>();
            await using var app = builder.Build();
            app.UseCorrelationId();
            app.UseAdminSecurity();
            app.UseMiddleware<EphemeralMasterKeyCleanupMiddleware>();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<NativeAdminRuntimeProfileMiddleware>();
            app.MapAdminAuthEndpoints();
            app.MapGlobalSettingsEndpoints();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await app.StartAsync(deadline.Token);
            try
            {
                using var client = app.GetTestClient();
                using var create = Request("POST", "/v1/admin/auth-tokens/ephemeral-master-key");
                create.Headers.Add("X-Master-Key", master);
                using var created = await client.SendAsync(create, deadline.Token);
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);
                using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(deadline.Token));
                var key = body.RootElement.GetProperty("ephemeralMasterKey").GetString()!;
                Assert.True(key.StartsWith("emk_", StringComparison.Ordinal));
                using var read = Request("GET", "/v1/admin/global-settings/definitions");
                read.Headers.Add("X-Master-Key", key);
                using var readResult = await client.SendAsync(read, deadline.Token);
                Assert.Equal(HttpStatusCode.OK, readResult.StatusCode);
                Assert.False(await app.Services.GetRequiredService<IEphemeralMasterKeyService>().KeyExistsAsync(key));
                using var replay = Request("GET", "/v1/admin/global-settings/definitions");
                replay.Headers.Add("X-Master-Key", key);
                using var replayResult = await client.SendAsync(replay, deadline.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, replayResult.StatusCode);
            }
            finally { await Stop(app); }
        }
        finally { Environment.SetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", previous); }
    }

    [Fact]
    public void ConstructorRejectsMissingNext() => Assert.Throws<ArgumentNullException>(() => new NativeAdminRuntimeProfileMiddleware(null!));

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        AdminProgram.ConfigureHttpServices(builder);
        builder.Services.AddAuthentication("owned-test").AddScheme<AuthenticationSchemeOptions, OwnedAuthenticationHandler>("owned-test", null);
        builder.Services.AddAuthorization();
        return builder;
    }

    private static async Task<SentinelApplication> CreateSentinelApp(string method, Calls state, bool unreadableBody = false,
        SharedSecurityService? security = null, bool programRegistration = false, bool throwingService = true)
    {
        var builder = Builder();
        builder.Services.AddScoped<ProbeDependency>(_ =>
        {
            state.Services++;
            if (throwingService) throw new InvalidOperationException("Excluded service resolved.");
            return new ProbeDependency();
        });
        if (security is not null) builder.Services.AddSingleton(security);
        var app = builder.Build();
        app.UseCorrelationId();
        if (unreadableBody) app.Use((context, next) => { context.Request.Body = new UnreadableBody(state); return next(context); });
        if (security is not null) app.UseAdminSecurity();
        app.UseAuthentication();
        app.UseAuthorization();
        if (programRegistration) AdminProgram.ConfigureRuntimeProfileBoundary(app);
        else app.UseMiddleware<NativeAdminRuntimeProfileMiddleware>();
        app.MapMethods("/v1/admin/excluded", [method], ([FromServices] ProbeDependency dependency, [FromBody] JsonElement body) =>
        {
            state.Handlers++;
            return Results.NoContent();
        }).WithName("Excluded_Operation").AddEndpointFilter((context, next) =>
        {
            state.Filters++;
            if (throwingService) throw new InvalidOperationException("Excluded filter invoked.");
            return next(context);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await app.StartAsync(deadline.Token); }
        catch { await app.DisposeAsync(); throw; }
        return new SentinelApplication(app);
    }

    private static HttpRequestMessage Request(string method, string path, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("x-request-id", "owned-unit119-correlation");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task AssertProblem(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("owned-unit119-correlation", Assert.Single(response.Headers.GetValues("x-request-id")));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = body.RootElement;
        Assert.Equal(new[] { "code", "detail", "status", "title", "traceId", "type" },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(501, root.GetProperty("status").GetInt32());
        Assert.Equal("Not Implemented", root.GetProperty("title").GetString());
        Assert.Equal("https://httpstatuses.com/501", root.GetProperty("type").GetString());
        Assert.Equal(Detail, root.GetProperty("detail").GetString());
        Assert.Equal("not_implemented", root.GetProperty("code").GetString());
        Assert.Equal("owned-unit119-correlation", root.GetProperty("traceId").GetString());
    }

    private static async Task Stop(WebApplication app)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await app.StopAsync(cleanup.Token);
    }

    public sealed class ProbeDependency;
    private sealed class SentinelApplication(WebApplication app) : IAsyncDisposable
    {
        public HttpClient GetTestClient() => app.GetTestClient();
        public async ValueTask DisposeAsync()
        {
            try { await Stop(app); }
            finally { await app.DisposeAsync(); }
        }
    }
    private sealed class Calls { public int BodyReads; public int Services; public int Filters; public int Handlers; }
    private sealed class UnreadableBody(Calls state) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { state.BodyReads++; throw new IOException("Excluded body read."); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { state.BodyReads++; throw new IOException("Excluded body read."); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public sealed class OwnedAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("MasterKey", "true")], Scheme.Name)), Scheme.Name)));
    }
}
