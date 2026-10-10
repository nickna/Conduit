using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Tests.Admin.Endpoints;

internal sealed class AdminEndpointTestHost : IDisposable
{
    private readonly IHost _host;
    private readonly IDisposable? _ownedResource;
    public HttpClient Client { get; }

    private AdminEndpointTestHost(IHost host, IDisposable? ownedResource)
    {
        _host = host;
        _ownedResource = ownedResource;
        Client = host.GetTestClient();
    }

    public static AdminEndpointTestHost Create(
        Action<IServiceCollection> configureServices,
        Action<IEndpointRouteBuilder> mapEndpoints,
        IDisposable? ownedResource = null,
        bool validateServiceProvider = false)
    {
        var host = new HostBuilder()
            .UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = validateServiceProvider;
                options.ValidateScopes = validateServiceProvider;
            })
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", null);
                    services.AddAuthorization(options =>
                        options.AddPolicy("MasterKeyPolicy", policy => policy.RequireAuthenticatedUser()));
                    configureServices(services);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(mapEndpoints);
                });
            })
            .Start();
        return new AdminEndpointTestHost(host, ownedResource);
    }

    public void Dispose()
    {
        Client.Dispose();
        _host.Dispose();
        _ownedResource?.Dispose();
    }

    private sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.ContainsKey("X-Test-Anonymous"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-admin")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
