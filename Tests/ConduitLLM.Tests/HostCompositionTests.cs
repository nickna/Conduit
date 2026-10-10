using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Interfaces;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests;

[Collection("AdminCompositionEnvironment")]
[Trait("Component", "HostComposition")]
public sealed class HostCompositionTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void GatewayWithoutRedis_FailsBeforeRegisteringRuntimeServices(string environment)
    {
        using var variables = new CompositionEnvironment(null);
        var builder = CreateBuilder(true, environment);
        global::Program.ConfigureBasicSettings(builder);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            global::Program.ConfigureRuntimeServices(builder));

        Assert.Contains("Redis is required", exception.Message);
        Assert.Contains("REDIS_URL", exception.Message);
        Assert.Contains("CONDUIT_REDIS_CONNECTION_STRING", exception.Message);
        Assert.DoesNotContain(builder.Services,
            descriptor => descriptor.ServiceType == typeof(TokenRateLimitFilter));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AdminWithoutRedis_ValidatesAndResolvesCompleteRuntimeGraph(string environment)
    {
        using var variables = new CompositionEnvironment(null);
        var builder = ConfigureHost(false, environment);
        await using var app = builder.Build();

        ResolveRuntimeServices(builder.Services, app.Services);
        Assert.Null(app.Services.GetService<IRedisErrorStore>());
        Assert.Null(app.Services.GetService<IProviderErrorTrackingService>());
        Assert.Null(app.Services.GetService<ConduitLLM.Core.Services.IVirtualKeyRateLimitService>());
    }

    [SkippableTheory]
    [InlineData(true, "Development")]
    [InlineData(true, "Production")]
    [InlineData(false, "Development")]
    [InlineData(false, "Production")]
    public async Task RedisConfiguredHosts_ValidateAndResolveCompleteRuntimeGraph(bool gateway, string environment)
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrEmpty(redis), "Set CONDUIT_CACHE_TEST_REDIS for real Redis host composition.");
        using var variables = new CompositionEnvironment(redis);
        var builder = ConfigureHost(gateway, environment);
        await using var app = builder.Build();

        ResolveRuntimeServices(builder.Services, app.Services);
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(StackExchange.Redis.IConnectionMultiplexer));
        if (gateway)
        {
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(SignalRMetrics));
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(ISignalRMetrics));
            Assert.Same(app.Services.GetRequiredService<SignalRMetrics>(), app.Services.GetRequiredService<ISignalRMetrics>());
            AssertRequestScopedRateLimiting(builder.Services, app.Services);
        }
    }

    private static WebApplicationBuilder ConfigureHost(bool gateway, string environment)
    {
        var builder = CreateBuilder(gateway, environment);
        if (gateway)
        {
            global::Program.ConfigureBasicSettings(builder);
            global::Program.ConfigureRuntimeServices(builder);
        }
        else
        {
            ConduitLLM.Admin.Program.ConfigureHttpServices(builder);
            ConduitLLM.Admin.Program.ConfigureRuntimeServices(builder, NullLogger.Instance);
        }
        return builder;
    }

    private static WebApplicationBuilder CreateBuilder(bool gateway, string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = gateway ? typeof(global::Program).Assembly.GetName().Name : typeof(ConduitLLM.Admin.Program).Assembly.GetName().Name,
            EnvironmentName = environment
        });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        return builder;
    }

    private static void ResolveRuntimeServices(IServiceCollection descriptors, IServiceProvider provider)
    {
        // Factory bodies are opaque to ValidateOnBuild. Resolve hosted services and
        // application factories explicitly, without starting background workers.
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(descriptors.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)), hostedServices.Length);
        Assert.All(hostedServices, service => Assert.NotNull(service));

        using var scope = provider.CreateScope();
        var endpoints = descriptors.Where(descriptor =>
            descriptor.ServiceType.Namespace is "ConduitLLM.Gateway.Endpoints" or "ConduitLLM.Admin.Endpoints")
            .Select(descriptor => descriptor.ServiceType).Distinct().ToArray();
        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            Assert.Single(descriptors, descriptor => descriptor.ServiceType == endpoint);
            Assert.NotNull(scope.ServiceProvider.GetRequiredService(endpoint));
        }

        foreach (var descriptor in descriptors.Where(descriptor =>
                     !descriptor.IsKeyedService && descriptor.ImplementationFactory is not null &&
                     descriptor.ServiceType.FullName?.StartsWith("ConduitLLM.", StringComparison.Ordinal) == true))
        {
            var services = descriptor.Lifetime == ServiceLifetime.Singleton ? provider : scope.ServiceProvider;
            Assert.NotNull(services.GetRequiredService(descriptor.ServiceType));
        }
    }

    private static void AssertRequestScopedRateLimiting(IServiceCollection descriptors, IServiceProvider provider)
    {
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(descriptors,
            descriptor => descriptor.ServiceType == typeof(RequestTokenEstimator)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(descriptors,
            descriptor => descriptor.ServiceType == typeof(TokenRateLimitFilter)).Lifetime);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        Assert.Same(first.ServiceProvider.GetRequiredService<TokenRateLimitFilter>(), first.ServiceProvider.GetRequiredService<TokenRateLimitFilter>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<TokenRateLimitFilter>(), second.ServiceProvider.GetRequiredService<TokenRateLimitFilter>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<ITokenCounter>(), second.ServiceProvider.GetRequiredService<ITokenCounter>());
    }

    private sealed class CompositionEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _originals;

        public CompositionEnvironment(string? redis)
        {
            var values = new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "postgresql://conduit:conduit@localhost:5432/conduit_tests",
                ["REDIS_URL"] = null,
                ["CONDUIT_REDIS_CONNECTION_STRING"] = redis,
                ["ConduitLLM__Messaging__Wolverine__Transport"] = "InMemory",
                ["CONDUIT_MIGRATION_MODE"] = "Skip",
                ["ApplicationCache__Environment"] = $"composition-{Guid.NewGuid():N}",
                ["Logging__EventLog__LogLevel__Default"] = "None"
            };
            _originals = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            foreach (var (name, value) in values) Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originals) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
