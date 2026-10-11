using System.Net;

using ConduitLLM.Gateway.Services;
using ConduitLLM.Security.Models;
using ConduitLLM.Security.Options;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Tests.Gateway.Services;

[Trait("Category", "Unit")]
[Trait("Component", "Security")]
public class SecurityServiceRateLimitingTests
{
    [Fact]
    public void GatewayOptions_BaseAndDerivedViewsShareRateLimitingOptions()
    {
        var gatewayOptions = new GatewaySecurityOptions();
        SecurityOptionsBase baseOptions = gatewayOptions;

        Assert.Same(gatewayOptions.RateLimiting, baseOptions.RateLimiting);

        baseOptions.RateLimiting.MaxRequests = 12;
        Assert.Equal(12, gatewayOptions.RateLimiting.MaxRequests);

        var replacement = new GatewayRateLimitingOptions { MaxRequests = 34 };
        gatewayOptions.RateLimiting = replacement;
        Assert.Same(replacement, baseOptions.RateLimiting);

        var baseReplacement = new GatewayRateLimitingOptions { MaxRequests = 56 };
        baseOptions.RateLimiting = baseReplacement;
        Assert.Same(baseReplacement, gatewayOptions.RateLimiting);
    }

    [Fact]
    public void AdminOptions_RetainAdminRateLimitingDefaults()
    {
        var options = new AdminSecurityOptions();

        Assert.False(options.RateLimiting.Enabled);
        Assert.Equal(100, options.RateLimiting.MaxRequests);
        Assert.Equal(60, options.RateLimiting.WindowSeconds);
        Assert.Equal(new[] { "/health", "/swagger" }, options.RateLimiting.ExcludedPaths);
    }

    [Fact]
    public async Task IsRequestAllowedAsync_DefaultLimit_Allows1000RequestsThenDenies()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<SecurityService>();
        var context = CreateContext();
        var startedAt = DateTime.UtcNow;

        for (var requestNumber = 1; requestNumber <= 1000; requestNumber++)
        {
            var result = await service.IsRequestAllowedAsync(context);
            Assert.True(result.IsAllowed, $"Request {requestNumber} should be allowed by the Gateway default limit.");
        }

        var denied = await service.IsRequestAllowedAsync(context);

        AssertRateLimited(denied, 1000, 60, "ip", startedAt);
        Assert.Equal("Rate limit exceeded", denied.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsRequestAllowedAsync_UsesConfiguredLimitAndWindow(bool useEnvironmentOverrides)
    {
        var settings = new Dictionary<string, string>
        {
            ["CoreApi:Security:RateLimiting:Enabled"] = useEnvironmentOverrides ? "false" : "true",
            ["CoreApi:Security:RateLimiting:MaxRequests"] = useEnvironmentOverrides ? "9" : "2",
            ["CoreApi:Security:RateLimiting:WindowSeconds"] = useEnvironmentOverrides ? "30" : "120"
        };
        if (useEnvironmentOverrides)
        {
            settings["CONDUIT_CORE_RATE_LIMITING_ENABLED"] = "true";
            settings["CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS"] = "2";
            settings["CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS"] = "120";
        }

        using var provider = BuildProvider(settings);
        var service = provider.GetRequiredService<SecurityService>();
        var context = CreateContext();
        var startedAt = DateTime.UtcNow;

        Assert.True((await service.IsRequestAllowedAsync(context)).IsAllowed);
        Assert.True((await service.IsRequestAllowedAsync(context)).IsAllowed);
        var denied = await service.IsRequestAllowedAsync(context);

        AssertRateLimited(denied, 2, 120, "ip", startedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsRequestAllowedAsync_DisabledRateLimiting_AllowsRequestsBeyondLimit(bool useEnvironmentOverride)
    {
        var settings = new Dictionary<string, string>
        {
            ["CoreApi:Security:RateLimiting:Enabled"] = useEnvironmentOverride ? "true" : "false",
            ["CoreApi:Security:RateLimiting:MaxRequests"] = "1"
        };
        if (useEnvironmentOverride)
        {
            settings["CONDUIT_CORE_RATE_LIMITING_ENABLED"] = "false";
        }

        using var provider = BuildProvider(settings);
        var service = provider.GetRequiredService<SecurityService>();
        var context = CreateContext();

        for (var requestNumber = 0; requestNumber < 3; requestNumber++)
        {
            Assert.True((await service.IsRequestAllowedAsync(context)).IsAllowed);
        }
    }

    [Theory]
    [InlineData(false, "/unmetered/ready")]
    [InlineData(true, "/environment-unmetered/ready")]
    public async Task IsRequestAllowedAsync_ExcludedPaths_DoNotConsumeLimit(bool useEnvironmentOverride, string excludedPath)
    {
        var settings = new Dictionary<string, string>
        {
            ["CoreApi:Security:RateLimiting:MaxRequests"] = "1",
            ["CoreApi:Security:RateLimiting:ExcludedPaths"] = "/unmetered"
        };
        if (useEnvironmentOverride)
        {
            settings["CONDUIT_CORE_RATE_LIMIT_EXCLUDED_PATHS"] = "/environment-unmetered";
        }

        using var provider = BuildProvider(settings);
        var service = provider.GetRequiredService<SecurityService>();
        var excludedContext = CreateContext(excludedPath);
        var meteredContext = CreateContext();
        var startedAt = DateTime.UtcNow;

        for (var requestNumber = 0; requestNumber < 3; requestNumber++)
        {
            Assert.True((await service.IsRequestAllowedAsync(excludedContext)).IsAllowed);
        }

        Assert.True((await service.IsRequestAllowedAsync(meteredContext)).IsAllowed);
        var denied = await service.IsRequestAllowedAsync(meteredContext);
        AssertRateLimited(denied, 1, 60, "ip", startedAt);
        Assert.True((await service.IsRequestAllowedAsync(excludedContext)).IsAllowed);
    }

    [Fact]
    public async Task IsRequestAllowedAsync_DiscoveryAndGeneralRequests_UseSeparateLimitsAndWindows()
    {
        var settings = new Dictionary<string, string>
        {
            ["CoreApi:Security:RateLimiting:MaxRequests"] = "2",
            ["CoreApi:Security:RateLimiting:WindowSeconds"] = "120"
        };
        using var provider = BuildProvider(settings, options =>
        {
            options.RateLimiting.Discovery.MaxRequests = 1;
            options.RateLimiting.Discovery.WindowSeconds = 300;
        });
        var service = provider.GetRequiredService<SecurityService>();
        var discoveryContext = CreateContext("/v1/conduit/discovery/models");
        var generalContext = CreateContext();
        var startedAt = DateTime.UtcNow;

        Assert.True((await service.IsRequestAllowedAsync(discoveryContext)).IsAllowed);
        var discoveryDenied = await service.IsRequestAllowedAsync(discoveryContext);
        AssertRateLimited(discoveryDenied, 1, 300, "discovery", startedAt);

        // The first admitted discovery request also counts toward the general IP limit.
        Assert.True((await service.IsRequestAllowedAsync(generalContext)).IsAllowed);
        var generalDenied = await service.IsRequestAllowedAsync(generalContext);
        AssertRateLimited(generalDenied, 2, 120, "ip", startedAt);
    }

    [Fact]
    public async Task IsRequestAllowedAsync_DisablingVirtualKeyRateLimits_DoesNotDisableIpRateLimiting()
    {
        var settings = new Dictionary<string, string>
        {
            ["CoreApi:Security:RateLimiting:MaxRequests"] = "1",
            ["CONDUIT_CORE_ENFORCE_VKEY_RATE_LIMITS"] = "false"
        };
        using var provider = BuildProvider(settings);
        var service = provider.GetRequiredService<SecurityService>();
        var context = CreateContext();
        var startedAt = DateTime.UtcNow;

        Assert.True((await service.IsRequestAllowedAsync(context)).IsAllowed);
        var denied = await service.IsRequestAllowedAsync(context);

        AssertRateLimited(denied, 1, 60, "ip", startedAt);
    }

    private static ServiceProvider BuildProvider(
        Dictionary<string, string> settings = null,
        Action<GatewaySecurityOptions> configure = null)
    {
        var configurationValues = new Dictionary<string, string>
        {
            ["CONDUIT_CORE_IP_FILTERING_ENABLED"] = "false",
            ["CONDUIT_SECURITY_USE_DISTRIBUTED_TRACKING"] = "false"
        };
        if (settings != null)
        {
            foreach (var setting in settings)
            {
                configurationValues[setting.Key] = setting.Value;
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddMemoryCache();
        services.ConfigureGatewaySecurityOptions(configuration);
        if (configure != null)
        {
            services.Configure(configure);
        }
        services.AddSingleton<SecurityService>();
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(string path = "/v1/conduit/chat/completions")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        return context;
    }

    private static void AssertRateLimited(
        SecurityCheckResult result, long expectedLimit, int expectedWindowSeconds, string expectedScope, DateTime startedAt)
    {
        Assert.False(result.IsAllowed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        Assert.NotNull(result.RateLimit);
        Assert.Equal(expectedLimit, result.RateLimit.Limit);
        Assert.Equal(0, result.RateLimit.Remaining);
        Assert.Equal(expectedScope, result.RateLimit.Scope);
        Assert.InRange(result.RateLimit.ResetsAt,
            startedAt.AddSeconds(expectedWindowSeconds - 5),
            DateTime.UtcNow.AddSeconds(expectedWindowSeconds + 5));
    }
}
