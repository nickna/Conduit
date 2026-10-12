using System.Net;

using ConduitLLM.Gateway.Services;
using ConduitLLM.Security.Models;
using ConduitLLM.Security.Options;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Tests.Gateway.Services;

[Trait("Category", "Unit")]
[Trait("Component", "GatewaySecurity")]
public sealed class SecurityServiceDiscoveryTests : IDisposable
{
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());
    private readonly ServiceProvider _serviceProvider = new ServiceCollection().BuildServiceProvider();
    private readonly GatewaySecurityOptions _options = new()
    {
        UseDistributedTracking = false,
        IpFiltering = { Enabled = false },
        FailedAuth = { Enabled = false },
        RateLimiting =
        {
            Enabled = true,
            MaxRequests = 1000,
            WindowSeconds = 60,
            Discovery = { MaxRequests = 2, WindowSeconds = 300 }
        }
    };

    [Theory]
    [InlineData("/v1/models")]
    [InlineData("/v1/models/")]
    [InlineData("/v1/models/example")]
    [InlineData("/V1/MODELS")]
    [InlineData("/v1/conduit/discovery")]
    [InlineData("/v1/conduit/discovery/")]
    [InlineData("/v1/conduit/discovery/models")]
    [InlineData("/v1/conduit/discovery/capabilities")]
    public async Task Discovery_root_and_descendants_enforce_discovery_limit(string path)
    {
        var service = CreateService();

        Assert.True((await CheckAsync(service, path)).IsAllowed);
        Assert.True((await CheckAsync(service, path)).IsAllowed);

        AssertDiscoveryLimit(await CheckAsync(service, path));
    }

    [Theory]
    [InlineData("/v1/models-other")]
    [InlineData("/v1/models-other/example")]
    [InlineData("/v1/models.json")]
    [InlineData("/unrelated/v1/models/example")]
    [InlineData("/v1/conduit/discovery-other/models")]
    [InlineData("/unrelated/v1/conduit/discovery/models")]
    public async Task Unrelated_paths_do_not_consume_discovery_quota(string path)
    {
        var service = CreateService();

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await CheckAsync(service, path)).IsAllowed);
        }

        Assert.True((await CheckAsync(service, "/v1/models")).IsAllowed);
        Assert.True((await CheckAsync(service, "/v1/models")).IsAllowed);
        AssertDiscoveryLimit(await CheckAsync(service, "/v1/models"));
    }

    [Theory]
    [InlineData("/custom/discovery")]
    [InlineData("/custom/discovery/")]
    public async Task Configured_roots_work_with_or_without_trailing_slash(string root)
    {
        _options.RateLimiting.Discovery.DiscoveryPaths = new List<string> { root };
        var service = CreateService();

        Assert.True((await CheckAsync(service, "/custom/discovery-other")).IsAllowed);
        Assert.True((await CheckAsync(service, "/custom/discovery")).IsAllowed);
        Assert.True((await CheckAsync(service, "/CUSTOM/DISCOVERY/example")).IsAllowed);
        AssertDiscoveryLimit(await CheckAsync(service, "/custom/discovery/"));
    }

    [Fact]
    public async Task Bound_discovery_settings_enforce_custom_quota_and_replace_default_paths()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CONDUIT_CORE_IP_FILTERING_ENABLED"] = "false",
            ["CONDUIT_CORE_IP_BANNING_ENABLED"] = "false",
            ["CONDUIT_SECURITY_USE_DISTRIBUTED_TRACKING"] = "false",
            ["CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS"] = "1000",
            ["CoreApi:Security:RateLimiting:Discovery:MaxRequests"] = "2",
            ["CoreApi:Security:RateLimiting:Discovery:WindowSeconds"] = "17",
            ["CoreApi:Security:RateLimiting:Discovery:DiscoveryPaths:0"] = "/custom/discovery/"
        }).Build();
        var services = new ServiceCollection();
        services.ConfigureGatewaySecurityOptions(configuration);
        using var provider = services.BuildServiceProvider();
        var service = new SecurityService(
            provider.GetRequiredService<IOptions<GatewaySecurityOptions>>(), configuration,
            NullLogger<SecurityService>.Instance, _memoryCache, provider);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await CheckAsync(service, "/v1/models")).IsAllowed);
        }

        Assert.True((await CheckAsync(service, "/custom/discovery")).IsAllowed);
        Assert.True((await CheckAsync(service, "/custom/discovery/models")).IsAllowed);

        var result = await CheckAsync(service, "/custom/discovery/");

        Assert.False(result.IsAllowed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        Assert.NotNull(result.RateLimit);
        Assert.Equal("discovery", result.RateLimit.Scope);
        Assert.Equal(2, result.RateLimit.Limit);
        Assert.InRange((result.RateLimit.ResetsAt - DateTime.UtcNow).TotalSeconds, 12, 17);
    }

    [Fact]
    public async Task Discovery_routes_share_one_quota_per_ip()
    {
        var service = CreateService();

        Assert.True((await CheckAsync(service, "/v1/models")).IsAllowed);
        Assert.True((await CheckAsync(service, "/v1/models/")).IsAllowed);
        AssertDiscoveryLimit(await CheckAsync(service, "/v1/conduit/discovery/models"));

        Assert.True((await CheckAsync(service, "/v1/models/example", "203.0.113.21")).IsAllowed);
        Assert.True((await CheckAsync(service, "/v1/chat/completions")).IsAllowed);
    }

    [Fact]
    public async Task General_ip_limit_counts_discovery_and_other_requests_independently()
    {
        _options.RateLimiting.MaxRequests = 2;
        _options.RateLimiting.Discovery.MaxRequests = 10;
        var service = CreateService();

        Assert.True((await CheckAsync(service, "/v1/models")).IsAllowed);
        Assert.True((await CheckAsync(service, "/v1/chat/completions")).IsAllowed);

        var result = await CheckAsync(service, "/v1/models/");

        Assert.False(result.IsAllowed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        Assert.NotNull(result.RateLimit);
        Assert.Equal("ip", result.RateLimit.Scope);
        Assert.Equal(2, result.RateLimit.Limit);
    }

    [Fact]
    public async Task Disabling_discovery_limit_keeps_general_ip_limit()
    {
        _options.RateLimiting.MaxRequests = 3;
        _options.RateLimiting.Discovery.Enabled = false;
        var service = CreateService();

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await CheckAsync(service, "/v1/models")).IsAllowed);
        }

        var result = await CheckAsync(service, "/v1/models");

        Assert.False(result.IsAllowed);
        Assert.Equal("ip", result.RateLimit?.Scope);
    }

    private SecurityService CreateService()
    {
        return new SecurityService(
            Options.Create(_options), new ConfigurationBuilder().Build(),
            NullLogger<SecurityService>.Instance, _memoryCache, _serviceProvider);
    }

    private static Task<SecurityCheckResult> CheckAsync(
        SecurityService service, string path, string ipAddress = "203.0.113.20")
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        context.Request.Path = path;
        return service.IsRequestAllowedAsync(context);
    }

    private static void AssertDiscoveryLimit(SecurityCheckResult result)
    {
        Assert.False(result.IsAllowed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        Assert.NotNull(result.RateLimit);
        Assert.Equal("discovery", result.RateLimit.Scope);
        Assert.Equal(2, result.RateLimit.Limit);
        Assert.Equal(0, result.RateLimit.Remaining);
        Assert.InRange((result.RateLimit.ResetsAt - DateTime.UtcNow).TotalSeconds, 295, 300);
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        _serviceProvider.Dispose();
    }
}
