using System.Security.Claims;
using System.Text.Encodings.Web;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Middleware;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

internal sealed class GatewayEndpointTestHost : IAsyncDisposable
{
    private readonly IHost _host;
    public HttpClient Client { get; }

    private GatewayEndpointTestHost(IHost host)
    {
        _host = host;
        Client = host.GetTestClient();
    }

    public static async Task<GatewayEndpointTestHost> StartAsync(
        Action<IServiceCollection>? configure = null,
        bool exposeOpenApi = false)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    // Mirrors Program.Configuration.cs so binding failures surface as
                    // BadHttpRequestException and get the OpenAI error envelope.
                    services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
                    services.AddGatewayEndpointHandlers();
                    services.AddSingleton(Mock.Of<IMediaStorageService>());
                    services.AddSingleton(Mock.Of<IMediaRecordRepository>());
                    services.AddSingleton(Mock.Of<IEventPublisher>());
                    AddRateLimitingFilterDependencies(services);
                    services.AddAuthentication("VirtualKey")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("VirtualKey", null);
                    services.AddAuthorization(options =>
                    {
                        options.AddPolicy("VirtualKeyAuthentication", policy =>
                        {
                            policy.AuthenticationSchemes.Add("VirtualKey");
                            policy.RequireAuthenticatedUser();
                        });
                        options.AddPolicy("AdminOnly", policy =>
                        {
                            policy.AuthenticationSchemes.Add("VirtualKey");
                            policy.RequireAuthenticatedUser();
                        });
                    });
                    configure?.Invoke(services);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    // Mirrors Program.Middleware.cs: the error middleware sits below auth and above
                    // the endpoints, so endpoint exceptions map to OpenAI-shaped error responses.
                    app.UseOpenAIErrorHandling();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGatewayApiEndpoints();
                        if (exposeOpenApi)
                            endpoints.MapOpenApi();
                    });
                });
            })
            .StartAsync();

        return new GatewayEndpointTestHost(host);
    }

    /// <summary>
    /// Mirrors the no-Redis branch of <c>AddConduitRateLimiting</c>. The chat, embeddings and
    /// responses routes carry <see cref="TokenRateLimitFilter"/>; without its dependencies every
    /// request to them fails DI inside the filter chain and answers 500 dependency_resolution_error
    /// before the handler ever runs. <see cref="UnlimitedTokenRateLimitService"/> admits everything,
    /// so the filter stays a pass-through and tests observe the handler's own behaviour.
    /// </summary>
    private static void AddRateLimitingFilterDependencies(IServiceCollection services)
    {
        services.AddSingleton(new RateLimitOptions());
        services.AddSingleton<IRateLimitFailurePolicy, RateLimitFailurePolicy>();
        services.AddSingleton<ITokenRateLimitService, UnlimitedTokenRateLimitService>();
        services.AddSingleton(Mock.Of<ITokenCounter>());
        services.AddSingleton<RequestTokenEstimator>();
        services.AddSingleton<TokenRateLimitFilter>();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.ContainsKey("X-Test-Anonymous"))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([
                new Claim("VirtualKeyId", "1"),
                new Claim("VirtualKey", "test-key")
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
