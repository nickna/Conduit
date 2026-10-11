using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Security.Middleware;
using ConduitLLM.Security.Options;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Tests.Security;

[Trait("Category", "Unit")]
[Trait("Component", "Security")]
public sealed class SecurityHeadersConfigurationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AggregateDisabled_OverridesIndividualSwitchesAndPreservesExistingHeaders(
        bool gateway, bool existingHeaders)
    {
        var prefix = Prefix(gateway);
        using var provider = CreateProvider(gateway, new Dictionary<string, string>
        {
            [$"{prefix}SECURITY_HEADERS_ENABLED"] = "false",
            [$"{prefix}SECURITY_HEADERS_X_CONTENT_TYPE_OPTIONS_ENABLED"] = "true",
            [$"{prefix}SECURITY_HEADERS_X_XSS_PROTECTION_ENABLED"] = "true",
            [$"{prefix}SECURITY_HEADERS_HSTS_ENABLED"] = "true",
            [$"{prefix}SECURITY_HEADERS_HSTS_MAX_AGE"] = "13"
        });
        var context = CreateContext(https: true);
        if (existingHeaders)
        {
            context.Response.Headers["Server"] = "upstream-server";
            context.Response.Headers["X-Powered-By"] = "upstream-framework";
            context.Response.Headers["X-Content-Type-Options"] = "upstream-value";
        }
        var originalHeaders = context.Response.Headers.ToDictionary(pair => pair.Key, pair => pair.Value);
        var nextCalled = false;
        var pipeline = CreatePipeline(provider, gateway, ctx =>
        {
            nextCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            return Task.CompletedTask;
        });

        await pipeline(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Equal(originalHeaders.Count, context.Response.Headers.Count);
        foreach (var header in originalHeaders)
        {
            Assert.Equal(header.Value, context.Response.Headers[header.Key]);
        }
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
        Assert.False(context.Response.Headers.ContainsKey("X-XSS-Protection"));
        Assert.False(context.Response.Headers.ContainsKey("X-Custom-Security"));
        Assert.False(context.Response.Headers.ContainsKey("X-Content-Type"));
        Assert.False(context.Response.Headers.ContainsKey("X-API-Version"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AggregateEnabled_RespectsDisabledIndividualHeaders(bool gateway)
    {
        var prefix = Prefix(gateway);
        using var provider = CreateProvider(gateway, new Dictionary<string, string>
        {
            [$"{prefix}SECURITY_HEADERS_ENABLED"] = "true",
            [$"{prefix}SECURITY_HEADERS_X_CONTENT_TYPE_OPTIONS_ENABLED"] = "false",
            [$"{prefix}SECURITY_HEADERS_X_XSS_PROTECTION_ENABLED"] = "false",
            [$"{prefix}SECURITY_HEADERS_HSTS_ENABLED"] = "false"
        });
        var context = CreateContext(https: true);
        context.Response.Headers["Server"] = "upstream-server";
        context.Response.Headers["X-Powered-By"] = "upstream-framework";

        await CreatePipeline(provider, gateway)(context);

        Assert.False(context.Response.Headers.ContainsKey("X-Content-Type-Options"));
        Assert.False(context.Response.Headers.ContainsKey("X-XSS-Protection"));
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
        Assert.False(context.Response.Headers.ContainsKey("Server"));
        Assert.False(context.Response.Headers.ContainsKey("X-Powered-By"));
        Assert.Equal("custom-value", context.Response.Headers["X-Custom-Security"].ToString());
        Assert.Equal("application/json", context.Response.Headers["X-Content-Type"].ToString());
        Assert.Equal("v1", context.Response.Headers["X-API-Version"].ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AggregateEnabled_BindsIndividualOverridesAndHstsMaxAge(bool gateway)
    {
        var prefix = Prefix(gateway);
        using var provider = CreateProvider(gateway, new Dictionary<string, string>
        {
            [$"{prefix}SECURITY_HEADERS_ENABLED"] = "true",
            [$"{prefix}SECURITY_HEADERS_X_CONTENT_TYPE_OPTIONS_ENABLED"] = "false",
            [$"{prefix}SECURITY_HEADERS_X_XSS_PROTECTION_ENABLED"] = "true",
            [$"{prefix}SECURITY_HEADERS_HSTS_ENABLED"] = "true",
            [$"{prefix}SECURITY_HEADERS_HSTS_MAX_AGE"] = "13"
        });
        var context = CreateContext(https: true);

        await CreatePipeline(provider, gateway)(context);

        Assert.False(context.Response.Headers.ContainsKey("X-Content-Type-Options"));
        Assert.Equal("1; mode=block", context.Response.Headers["X-XSS-Protection"].ToString());
        Assert.Equal("max-age=13; includeSubDomains", context.Response.Headers["Strict-Transport-Security"].ToString());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AggregateOmitted_PreservesDefaultsAndHttpsOnlyHsts(bool gateway, bool https)
    {
        using var provider = CreateProvider(gateway, new Dictionary<string, string>());
        var context = CreateContext(https);

        await CreatePipeline(provider, gateway)(context);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal(!gateway, context.Response.Headers.ContainsKey("X-XSS-Protection"));
        Assert.Equal(https, context.Response.Headers.ContainsKey("Strict-Transport-Security"));
        if (https)
        {
            Assert.Equal("max-age=31536000; includeSubDomains",
                context.Response.Headers["Strict-Transport-Security"].ToString());
        }
        Assert.Equal("custom-value", context.Response.Headers["X-Custom-Security"].ToString());
        Assert.Equal("application/json", context.Response.Headers["X-Content-Type"].ToString());
        Assert.Equal("v1", context.Response.Headers["X-API-Version"].ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidAggregateValue_RejectsConfiguration(bool gateway)
    {
        using var provider = CreateProvider(gateway, new Dictionary<string, string>
        {
            [$"{Prefix(gateway)}SECURITY_HEADERS_ENABLED"] = "invalid"
        });

        Assert.Throws<FormatException>(() =>
        {
            if (gateway)
            {
                _ = provider.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value;
            }
            else
            {
                _ = provider.GetRequiredService<IOptions<AdminSecurityOptions>>().Value;
            }
        });
    }

    private static string Prefix(bool gateway) => gateway ? "CONDUIT_CORE_" : "CONDUIT_ADMIN_";

    private static ServiceProvider CreateProvider(bool gateway, Dictionary<string, string> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddLogging();
        if (gateway)
        {
            services.AddCoreApiSecurity(configuration);
            services.PostConfigure<GatewaySecurityOptions>(options =>
                options.Headers.CustomHeaders["X-Custom-Security"] = "custom-value");
        }
        else
        {
            services.ConfigureAdminSecurityOptions(configuration);
            services.PostConfigure<AdminSecurityOptions>(options =>
                options.Headers.CustomHeaders["X-Custom-Security"] = "custom-value");
        }
        return services.BuildServiceProvider();
    }

    private static RequestDelegate CreatePipeline(ServiceProvider provider, bool gateway, RequestDelegate next = null)
    {
        var app = new ApplicationBuilder(provider);
        if (gateway)
        {
            app.UseGatewaySecurityHeaders();
        }
        else
        {
            app.UseAdminSecurityHeaders();
        }
        app.Run(next ?? (_ => Task.CompletedTask));
        return app.Build();
    }

    private static DefaultHttpContext CreateContext(bool https)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = https ? "https" : "http";
        return context;
    }
}
