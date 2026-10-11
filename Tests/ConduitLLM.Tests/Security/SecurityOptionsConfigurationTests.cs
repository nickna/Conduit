using System.Text;

using ConduitLLM.Security.Options;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Tests.Security;

[Trait("Category", "Unit")]
[Trait("Component", "Security")]
public sealed class SecurityOptionsConfigurationTests
{
    [Fact]
    public void AdminOptions_TemplateLimitNamesBindNonDefaultValues()
    {
        var values = File.ReadLines(FindEnvironmentTemplate())
            .Where(line => line.StartsWith("CONDUIT_ADMIN_", StringComparison.Ordinal))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1]);

        // Use the actual template names so a future template/configuration mismatch
        // cannot hide behind assertions against the default limit values.
        values[values.Keys.Single(key => key.EndsWith("MAX_REQUESTS", StringComparison.Ordinal))] = "7";
        values[values.Keys.Single(key => key.EndsWith("WINDOW_SECONDS", StringComparison.Ordinal))] = "13";
        values[values.Keys.Single(key => key.EndsWith("ATTEMPTS", StringComparison.Ordinal))] = "2";
        values[values.Keys.Single(key => key.EndsWith("BAN_DURATION_MINUTES", StringComparison.Ordinal))] = "3";

        var options = ResolveAdminOptions(values);

        Assert.True(options.RateLimiting.Enabled);
        Assert.Equal(7, options.RateLimiting.MaxRequests);
        Assert.Equal(13, options.RateLimiting.WindowSeconds);
        Assert.Equal(2, options.FailedAuth.MaxAttempts);
        Assert.Equal(3, options.FailedAuth.BanDurationMinutes);
    }

    [Fact]
    public void AdminOptions_PreviouslyPublishedLimitNamesRemainSupported()
    {
        var options = ResolveAdminOptions(new Dictionary<string, string>
        {
            ["CONDUIT_ADMIN_RATE_LIMITING_MAX_REQUESTS"] = "7",
            ["CONDUIT_ADMIN_RATE_LIMITING_WINDOW_SECONDS"] = "13",
            ["CONDUIT_ADMIN_FAILED_AUTH_MAX_ATTEMPTS"] = "2",
            ["CONDUIT_ADMIN_FAILED_AUTH_BAN_DURATION_MINUTES"] = "3"
        });

        Assert.Equal(7, options.RateLimiting.MaxRequests);
        Assert.Equal(13, options.RateLimiting.WindowSeconds);
        Assert.Equal(2, options.FailedAuth.MaxAttempts);
        Assert.Equal(3, options.FailedAuth.BanDurationMinutes);
    }

    [Fact]
    public void AdminOptions_CanonicalNamesTakePrecedenceOverLegacyAliases()
    {
        var options = ResolveAdminOptions(new Dictionary<string, string>
        {
            ["CONDUIT_ADMIN_RATE_LIMIT_MAX_REQUESTS"] = "17",
            ["CONDUIT_ADMIN_RATE_LIMIT_WINDOW_SECONDS"] = "23",
            ["CONDUIT_ADMIN_MAX_FAILED_AUTH_ATTEMPTS"] = "4",
            ["CONDUIT_ADMIN_AUTH_BAN_DURATION_MINUTES"] = "9",
            ["CONDUIT_ADMIN_RATE_LIMITING_MAX_REQUESTS"] = "ignored",
            ["CONDUIT_ADMIN_RATE_LIMITING_WINDOW_SECONDS"] = "ignored",
            ["CONDUIT_ADMIN_FAILED_AUTH_MAX_ATTEMPTS"] = "ignored",
            ["CONDUIT_ADMIN_FAILED_AUTH_BAN_DURATION_MINUTES"] = "ignored"
        });

        Assert.Equal(17, options.RateLimiting.MaxRequests);
        Assert.Equal(23, options.RateLimiting.WindowSeconds);
        Assert.Equal(4, options.FailedAuth.MaxAttempts);
        Assert.Equal(9, options.FailedAuth.BanDurationMinutes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GatewayOptions_DiscoverySectionBindsIndependentlyAndPreservesParentEnvironmentPrecedence(
        bool parentEnabled,
        bool discoveryEnabled)
    {
        var options = ResolveGatewayOptions(new Dictionary<string, string>
        {
            ["CONDUIT_CORE_RATE_LIMITING_ENABLED"] = parentEnabled.ToString(),
            ["CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS"] = "19",
            ["CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS"] = "23",
            ["CONDUIT_CORE_RATE_LIMIT_EXCLUDED_PATHS"] = "/env-health,/env-metrics",
            ["CoreApi:Security:RateLimiting:Enabled"] = (!parentEnabled).ToString(),
            ["CoreApi:Security:RateLimiting:MaxRequests"] = "101",
            ["CoreApi:Security:RateLimiting:WindowSeconds"] = "103",
            ["CoreApi:Security:RateLimiting:ExcludedPaths"] = "/section-health",
            ["CoreApi:Security:RateLimiting:Discovery:Enabled"] = discoveryEnabled.ToString(),
            ["CoreApi:Security:RateLimiting:Discovery:MaxRequests"] = "7",
            ["CoreApi:Security:RateLimiting:Discovery:WindowSeconds"] = "13",
            ["CoreApi:Security:RateLimiting:Discovery:MaxCapabilityChecksPerModel"] = "2",
            ["CoreApi:Security:RateLimiting:Discovery:CapabilityCheckWindowSeconds"] = "17",
            ["CoreApi:Security:RateLimiting:Discovery:DiscoveryPaths:0"] = "/custom-discovery",
            ["CoreApi:Security:RateLimiting:Discovery:DiscoveryPaths:1"] = "/custom-models"
        });

        Assert.Equal(parentEnabled, options.RateLimiting.Enabled);
        Assert.Equal(19, options.RateLimiting.MaxRequests);
        Assert.Equal(23, options.RateLimiting.WindowSeconds);
        Assert.Equal(new[] { "/env-health", "/env-metrics" }, options.RateLimiting.ExcludedPaths);

        var discovery = options.RateLimiting.Discovery;
        Assert.Equal(discoveryEnabled, discovery.Enabled);
        Assert.Equal(7, discovery.MaxRequests);
        Assert.Equal(13, discovery.WindowSeconds);
        Assert.Equal(2, discovery.MaxCapabilityChecksPerModel);
        Assert.Equal(17, discovery.CapabilityCheckWindowSeconds);
        Assert.Equal(new[] { "/custom-discovery", "/custom-models" }, discovery.DiscoveryPaths);
    }

    [Fact]
    public void GatewayOptions_OmittedDiscoverySettingsRetainDefaults()
    {
        var options = ResolveGatewayOptions(new Dictionary<string, string>
        {
            ["CoreApi:Security:RateLimiting:Discovery:MaxRequests"] = "7"
        });
        var defaults = new DiscoveryRateLimitOptions();

        Assert.Equal(7, options.RateLimiting.Discovery.MaxRequests);
        Assert.Equal(defaults.Enabled, options.RateLimiting.Discovery.Enabled);
        Assert.Equal(defaults.WindowSeconds, options.RateLimiting.Discovery.WindowSeconds);
        Assert.Equal(defaults.MaxCapabilityChecksPerModel, options.RateLimiting.Discovery.MaxCapabilityChecksPerModel);
        Assert.Equal(defaults.CapabilityCheckWindowSeconds, options.RateLimiting.Discovery.CapabilityCheckWindowSeconds);
        Assert.Equal(defaults.DiscoveryPaths, options.RateLimiting.Discovery.DiscoveryPaths);
    }

    [Fact]
    public void GatewayOptions_EmptyDiscoveryPathsArrayReplacesDefaults()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("""
            { "CoreApi": { "Security": { "RateLimiting": { "Discovery": { "DiscoveryPaths": [] } } } } }
            """));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        var services = new ServiceCollection().ConfigureGatewaySecurityOptions(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value;

        Assert.Empty(options.RateLimiting.Discovery.DiscoveryPaths);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("models")]
    [InlineData(" /v1/models")]
    public void GatewayOptions_InvalidDiscoveryRootsRejectConfigurationAtStartup(string path)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["CoreApi:Security:RateLimiting:Discovery:DiscoveryPaths:0"] = path
        }).Build();
        var services = new ServiceCollection().ConfigureGatewaySecurityOptions(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("nonblank absolute route roots", exception.Message);
    }

    private static AdminSecurityOptions ResolveAdminOptions(Dictionary<string, string> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().ConfigureAdminSecurityOptions(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AdminSecurityOptions>>().Value;
    }

    private static GatewaySecurityOptions ResolveGatewayOptions(Dictionary<string, string> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().ConfigureGatewaySecurityOptions(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value;
    }

    private static string FindEnvironmentTemplate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Join(directory.FullName, ".env.example");
            if (File.Exists(path))
            {
                return path;
            }
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate the repository's .env.example template.");
    }
}
