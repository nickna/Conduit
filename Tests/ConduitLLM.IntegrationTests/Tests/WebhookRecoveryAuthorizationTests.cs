using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.IntegrationTests.Tests;

[Trait("Category", "Integration")]
[Trait("Component", "Webhooks")]
public sealed class WebhookRecoveryAuthorizationTests
{
    [Fact]
    public async Task RecoveryRoutes_RequireMasterPolicy_AndAttributeReplayToAuthenticatedActor()
    {
        var recovery = new Mock<IWebhookRecovery>();
        recovery.Setup(r => r.InspectAsync(It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>())).ReturnsAsync([]);
        recovery.Setup(r => r.ReplayAsync(It.IsAny<string>(), It.IsAny<WebhookReplayRequest>(), "operator-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebhookReplayResult("Accepted", 1));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(recovery.Object);
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, Authentication>("test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy("MasterKeyPolicy", p => p.RequireAuthenticatedUser().RequireClaim("master", "true")));
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, AdminHttpJsonContext.Default);
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, AdminHttpResponseJsonContext.Default);
        });
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapWebhookRecoveryEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single()) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/admin/webhook-deliveries/")).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-Actor", "viewer");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/admin/webhook-deliveries/")).StatusCode);
            recovery.VerifyNoOtherCalls();
            client.DefaultRequestHeaders.Remove("X-Test-Actor"); client.DefaultRequestHeaders.Add("X-Test-Actor", "operator-1");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/webhook-deliveries/?owner=1&limit=10")).StatusCode);
            var request = new WebhookReplayRequest(Guid.NewGuid(), 1, 0);
            using var response = await client.PostAsJsonAsync("/v1/admin/webhook-deliveries/delivery/replay", request,
                AdminHttpJsonContext.Default.WebhookReplayRequest);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            recovery.Verify(r => r.ReplayAsync("delivery", It.Is<WebhookReplayRequest>(v => v.OperationId == request.OperationId),
                "operator-1", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = Request.Headers["X-Test-Actor"].ToString();
            if (actor == "") return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor), new("master", actor == "operator-1" ? "true" : "false")], "test");
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), "test")));
        }
    }
}
