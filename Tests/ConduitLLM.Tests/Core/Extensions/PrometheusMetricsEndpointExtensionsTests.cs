using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;

using ConduitLLM.Core.Extensions;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Metrics;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Prometheus;

namespace ConduitLLM.Tests.Core.Extensions;

public sealed class PrometheusMetricsEndpointExtensionsTests
{
    private const string RemoteIpHeader = "X-Test-Remote-Ip";
    private const string AuthenticatedHeader = "X-Test-Authenticated";

    private static readonly Counter RegistryCounter = Prometheus.Metrics.CreateCounter(
        "conduit_issue_1066_registry_total",
        "Verifies prometheus-net registry exposition.");

    private static readonly Meter TestMeter = new("ConduitLLM.Tests.Issue1066");
    private static readonly System.Diagnostics.Metrics.Counter<long> MeterCounter = TestMeter.CreateCounter<long>(
        "conduit.issue_1066.meter");

    [Fact]
    public async Task MetricsEndpoint_ExposesRegistryAndMeterMetrics_ToPrivateNetworks()
    {
        RegistryCounter.Inc();
        MeterCounter.Add(1);
        UsageMetrics.BillingRevenue.WithLabels("issue-1066", "test").Inc(0.01);

        using var host = await StartHostAsync();
        using var request = CreateRequest("/metrics", "10.10.0.8");

        using var response = await host.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("conduit_issue_1066_registry_total");
        body.Should().Contain("conduit_issue_1066_meter");
        body.Should().Contain("conduit_billing_revenue_dollars_total");
    }

    [Fact]
    public async Task MetricsEndpoint_ExposesMetrics_ToAuthenticatedExternalRequests()
    {
        RegistryCounter.Inc();

        using var host = await StartHostAsync();
        using var request = CreateRequest("/metrics", "203.0.113.10", authenticated: true);

        using var response = await host.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("conduit_issue_1066_registry_total");
    }

    [Theory]
    [InlineData("text/plain", "text/plain")]
    [InlineData("application/openmetrics-text", "application/openmetrics-text")]
    public async Task MetricsEndpoint_NegotiatesTheDocumentedScrapeFormats(string accept, string contentType)
    {
        using var host = await StartHostAsync();
        using var request = CreateRequest("/metrics", "10.10.0.8");
        request.Headers.Accept.ParseAdd(accept);

        using var response = await host.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(contentType);
        if (contentType == "application/openmetrics-text")
            (await response.Content.ReadAsStringAsync()).Should().EndWith("# EOF\n");
    }

    [Fact]
    public async Task MetricsEndpoint_DoesNotExposeMetrics_ToAnonymousExternalRequests()
    {
        using var host = await StartHostAsync();
        using var request = CreateRequest("/metrics", "203.0.113.10");

        using var response = await host.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        body.Should().NotContain("conduit_issue_1066_registry_total");
    }

    [Fact]
    public async Task MetricsEndpoint_DoesNotInterceptAdminJsonMetricsRoute()
    {
        using var host = await StartHostAsync();
        using var request = CreateRequest("/metrics/", "203.0.113.10", authenticated: true);

        using var response = await host.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("admin-json-metrics");
    }

    [Fact]
    public void MeterAdapter_UsesSharedBucketsForStreamStartupLatency()
    {
        using var meter = new Meter("ConduitLLM.Tests.Issue1066.Buckets");
        var providerFirstChunk = meter.CreateHistogram<double>(
            SseTransportMetrics.TimeToProviderFirstChunkInstrumentName);
        var clientFirstFlush = meter.CreateHistogram<double>(
            SseTransportMetrics.TimeToClientFirstFlushInstrumentName);
        var unusedFallback = new Func<Instrument, double[]>(_ => []);

        var providerBuckets = PrometheusMeterAdapterConfiguration.ResolveHistogramBuckets(
            providerFirstChunk,
            unusedFallback);
        var clientBuckets = PrometheusMeterAdapterConfiguration.ResolveHistogramBuckets(
            clientFirstFlush,
            unusedFallback);

        clientBuckets.Should().BeSameAs(providerBuckets);
        providerBuckets.Should().BeInAscendingOrder();
        providerBuckets.First().Should().Be(TimeSpan.FromMilliseconds(5).TotalSeconds);
        providerBuckets.Last().Should().Be(TimeSpan.FromSeconds(15).TotalSeconds);
    }

    [Fact]
    public void MeterAdapter_UsesFinerBucketsForAccountingFinalization()
    {
        using var meter = new Meter("ConduitLLM.Tests.Issue1066.AccountingBuckets");
        var accountingFinalization = meter.CreateHistogram<double>(
            SseTransportMetrics.AccountingFinalizationInstrumentName);

        var accountingBuckets = PrometheusMeterAdapterConfiguration.ResolveHistogramBuckets(
            accountingFinalization,
            _ => []);

        accountingBuckets.Should().BeInAscendingOrder();
        accountingBuckets.First().Should().Be(TimeSpan.FromMilliseconds(1).TotalSeconds);
        accountingBuckets.Should().Contain(TimeSpan.FromMilliseconds(2).TotalSeconds);
        accountingBuckets.Should().Contain(TimeSpan.FromMilliseconds(5).TotalSeconds);
        accountingBuckets.Last().Should().Be(TimeSpan.FromSeconds(10).TotalSeconds);
    }

    [Fact]
    public void MeterAdapter_UsesDefaultBucketsForOtherHistograms()
    {
        using var meter = new Meter("ConduitLLM.Tests.Issue1066.DefaultBuckets");
        var unrelatedHistogram = meter.CreateHistogram<double>("unrelated.duration");
        double[] defaultBuckets = [1, 2, 3];

        var resolvedBuckets = PrometheusMeterAdapterConfiguration.ResolveHistogramBuckets(
            unrelatedHistogram,
            instrument => instrument == unrelatedHistogram ? defaultBuckets : []);

        resolvedBuckets.Should().BeSameAs(defaultBuckets);
    }

    private static HttpRequestMessage CreateRequest(string path, string remoteIp, bool authenticated = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(RemoteIpHeader, remoteIp);
        if (authenticated)
        {
            request.Headers.Add(AuthenticatedHeader, "true");
        }

        return request;
    }

    private static async Task<IHost> StartHostAsync()
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", null);
                    services.AddAuthorization();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[RemoteIpHeader]!);
                        await next(context);
                    });
                    app.UseAuthentication();
                    app.UseConduitPrometheusMetricsEndpoint();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/metrics/", () => "admin-json-metrics")
                            .RequireAuthorization();
                    });
                });
            })
            .StartAsync();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(AuthenticatedHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "metrics-scraper")], Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
