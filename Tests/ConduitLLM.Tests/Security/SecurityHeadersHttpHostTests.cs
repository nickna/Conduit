using System.Net;
using System.Text.Json;

using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Middleware;
using ConduitLLM.Core.Middleware;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Security.Middleware;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Tests.Security;

/// <summary>
/// Real loopback HTTP coverage for the production security registration and header
/// pipeline only. These partial security hosts do not start either complete API.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Component", "Security")]
public sealed class SecurityHeadersHttpHostTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private const string DownstreamServer = "controlled-downstream-server";
    private const string DownstreamPoweredBy = "controlled-downstream-runtime";

    public enum ResponsePath
    {
        Endpoint,
        Middleware,
        StartingCallback,
        StartedBeforeReturn,
        HandledException,
        ShortCircuit,
        NoContent
    }

    public static TheoryData<bool, bool, ResponsePath> ApplicationResponseCases
    {
        get
        {
            var cases = new TheoryData<bool, bool, ResponsePath>();
            foreach (var gateway in new[] { true, false })
            foreach (var enabled in new[] { true, false })
            foreach (var path in Enum.GetValues<ResponsePath>())
                cases.Add(gateway, enabled, path);
            return cases;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialSecurityHost_DefaultsSuppressKestrelIdentification(bool gateway)
    {
        var response = await SendAsync(gateway, new Dictionary<string, string>());

        Assert.Empty(response.Server);
        Assert.False(response.AddServerHeader);
        Assert.Equal("nosniff", response.ContentTypeOptions);
        Assert.Equal("v1", response.ApiVersion);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task PartialSecurityHost_ExplicitAggregateSuppressesEvenWhenIndividualHeadersAreDisabled(
        bool gateway, bool disableIndividualHeaders)
    {
        var prefix = Prefix(gateway);
        var configuration = new Dictionary<string, string>
        {
            [$"{prefix}SECURITY_HEADERS_ENABLED"] = "true"
        };
        if (disableIndividualHeaders)
        {
            configuration[$"{prefix}SECURITY_HEADERS_X_CONTENT_TYPE_OPTIONS_ENABLED"] = "false";
            configuration[$"{prefix}SECURITY_HEADERS_X_XSS_PROTECTION_ENABLED"] = "false";
            configuration[$"{prefix}SECURITY_HEADERS_HSTS_ENABLED"] = "false";
        }

        var response = await SendAsync(gateway, configuration, addServerHeader: true);

        Assert.Empty(response.Server);
        Assert.False(response.AddServerHeader);
        Assert.Equal(disableIndividualHeaders ? null : "nosniff", response.ContentTypeOptions);
        Assert.Equal("v1", response.ApiVersion);
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public async Task PartialSecurityHost_DisabledAggregatePreservesTheHostsKestrelSetting(
        bool gateway, bool? configuredAddServerHeader, bool expectedAddServerHeader)
    {
        var response = await SendAsync(gateway, new Dictionary<string, string>
        {
            [$"{Prefix(gateway)}SECURITY_HEADERS_ENABLED"] = "false"
        }, configuredAddServerHeader);

        Assert.Equal(expectedAddServerHeader, response.AddServerHeader);
        if (expectedAddServerHeader) Assert.Equal("Kestrel", Assert.Single(response.Server));
        else Assert.Empty(response.Server);
        Assert.Null(response.ContentTypeOptions);
        Assert.Null(response.ApiVersion);
    }

    [Theory]
    [MemberData(nameof(ApplicationResponseCases))]
    public async Task PartialSecurityHost_AggregateControlsDownstreamIdentificationAtResponseStart(
        bool gateway, bool enabled, ResponsePath path)
    {
        var response = await SendAsync(gateway, new Dictionary<string, string>
        {
            [$"{Prefix(gateway)}SECURITY_HEADERS_ENABLED"] = enabled.ToString()
        }, configureDownstream: app => ConfigureApplicationResponse(app, gateway, path));

        if (enabled)
        {
            Assert.Empty(response.Server);
            Assert.Empty(response.PoweredBy);
        }
        else
        {
            Assert.Equal(DownstreamServer, Assert.Single(response.Server));
            Assert.Equal(DownstreamPoweredBy, Assert.Single(response.PoweredBy));
        }
        Assert.Equal(!enabled, response.AddServerHeader);
        Assert.Equal(enabled ? "nosniff" : null, response.ContentTypeOptions);
        Assert.Equal(enabled ? "v1" : null, response.ApiVersion);
        Assert.Equal(path == ResponsePath.StartingCallback ? "ran" : null, response.DownstreamCallback);

        if (path == ResponsePath.HandledException)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(gateway ? "application/json" : "application/problem+json", response.ContentType);
            using var json = JsonDocument.Parse(response.Body);
            if (gateway) Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("error").ValueKind);
            else Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
        }
        else if (path == ResponsePath.NoContent)
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Empty(response.Body);
        }
        else
        {
            Assert.Equal(path == ResponsePath.ShortCircuit ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
                response.StatusCode);
            Assert.Equal(path == ResponsePath.ShortCircuit ? "short-circuit response" : "application response",
                response.Body);
        }
    }

    private static void ConfigureApplicationResponse(IApplicationBuilder app, bool gateway, ResponsePath path)
    {
        if (path == ResponsePath.HandledException)
        {
            if (gateway) app.UseOpenAIErrorHandling();
            else app.UseAdminExceptionHandling();
        }

        if (path == ResponsePath.Middleware)
        {
            app.Use(next => context =>
            {
                AddIdentification(context);
                return next(context);
            });
        }

        if (path == ResponsePath.ShortCircuit)
        {
            app.Use(_ => context =>
            {
                AddIdentification(context);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return context.Response.WriteAsync("short-circuit response", context.RequestAborted);
            });
            app.Run(_ => throw new InvalidOperationException("The short circuit must not invoke its endpoint."));
            return;
        }

        app.Run(async context =>
        {
            if (path == ResponsePath.StartingCallback)
            {
                context.Response.OnStarting(() =>
                {
                    AddIdentification(context);
                    context.Response.Headers["X-Downstream-Callback"] = "ran";
                    return Task.CompletedTask;
                });
            }
            else if (path != ResponsePath.Middleware)
            {
                AddIdentification(context);
            }

            if (path == ResponsePath.HandledException)
                throw new Exception("Controlled downstream exception.");
            if (path == ResponsePath.NoContent)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            if (path == ResponsePath.StartedBeforeReturn)
            {
                await context.Response.StartAsync(context.RequestAborted);
                Assert.True(context.Response.HasStarted);
            }
            await context.Response.WriteAsync("application response", context.RequestAborted);
        });
    }

    private static void AddIdentification(HttpContext context)
    {
        context.Response.Headers["Server"] = DownstreamServer;
        context.Response.Headers["X-Powered-By"] = DownstreamPoweredBy;
    }

    private static string Prefix(bool gateway) => gateway ? "CONDUIT_CORE_" : "CONDUIT_ADMIN_";

    private static async Task<ObservedHeaders> SendAsync(
        bool gateway, Dictionary<string, string> configuration, bool? addServerHeader = null,
        Action<IApplicationBuilder> configureDownstream = null)
    {
        // No default environment/configuration providers: a developer's API settings
        // must not alter the security options exercised by this isolated host.
        var builder = new HostBuilder()
            .ConfigureAppConfiguration((_, options) => options.AddInMemoryCollection(configuration))
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web =>
            {
                web.UseKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, 0);
                    if (addServerHeader.HasValue) options.AddServerHeader = addServerHeader.Value;
                });
                web.ConfigureServices((context, services) =>
                {
                    var existingHostedServices = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToHashSet();
                    if (gateway) services.AddCoreApiSecurity(context.Configuration);
                    else services.AddAdminServices(context.Configuration);

                    // Keep the web-server lifecycle. Remove only background workers
                    // added by API registration, whose database/messaging work is not
                    // part of this partial security host's header contract.
                    foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                        !existingHostedServices.Contains(descriptor)).ToArray())
                        services.Remove(descriptor);
                });
                web.Configure(app =>
                {
                    if (gateway) app.UseGatewaySecurityHeaders();
                    else app.UseAdminSecurityHeaders();
                    if (configureDownstream is null)
                        app.Run(context => context.Response.WriteAsync("partial security host", context.RequestAborted));
                    else
                        configureDownstream(app);
                });
            });

        var host = builder.Build();
        try
        {
            using var deadline = new CancellationTokenSource(TestTimeout);
            await host.StartAsync(deadline.Token).WaitAsync(TestTimeout);
            var server = host.Services.GetRequiredService<IServer>();
            var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
            var uri = new Uri(address);
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(uri.Host)));
            Assert.InRange(uri.Port, 1, 65535);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                BaseAddress = uri,
                Timeout = TestTimeout
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/proof")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var response = await client.SendAsync(request, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            if (configureDownstream is null)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("partial security host", body);
            }
            return new ObservedHeaders(response.Headers.Server.Select(value => value.ToString()).ToArray(),
                response.Headers.TryGetValues("X-Powered-By", out var poweredBy) ? poweredBy.ToArray() : [],
                response.Headers.TryGetValues("X-Content-Type-Options", out var contentType) ? Assert.Single(contentType) : null,
                response.Headers.TryGetValues("X-API-Version", out var apiVersion) ? Assert.Single(apiVersion) : null,
                host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.AddServerHeader,
                response.Headers.TryGetValues("X-Downstream-Callback", out var callback) ? Assert.Single(callback) : null,
                response.StatusCode, body, response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            try
            {
                using var shutdown = new CancellationTokenSource(TestTimeout);
                await host.StopAsync(shutdown.Token).WaitAsync(TestTimeout);
            }
            finally
            {
                await Assert.IsAssignableFrom<IAsyncDisposable>(host).DisposeAsync().AsTask().WaitAsync(TestTimeout);
            }
        }
    }

    private sealed record ObservedHeaders(string[] Server, string[] PoweredBy, string? ContentTypeOptions,
        string? ApiVersion, bool AddServerHeader, string? DownstreamCallback, HttpStatusCode StatusCode,
        string Body, string? ContentType);
}
