using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interceptors;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Interfaces;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.RateLimiting;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;

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
    public void GatewayRedisConnectivityCheck_IsPartOfReadiness(string environment)
    {
        using var variables = new CompositionEnvironment("127.0.0.1:6379,abortConnect=false");
        var builder = CreateBuilder(true, environment);
        global::Program.ConfigureMonitoringServices(builder);
        using var services = builder.Services.BuildServiceProvider();

        var registrations = services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;
        var redis = Assert.Single(registrations, registration => registration.Name == "redis");

        Assert.Contains("ready", redis.Tags);
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
        AssertFunctionClientComposition(builder.Services, app.Services, false);
        AssertFunctionsEndpointComposition(builder.Services, app.Services, false);
        await AssertFunctionCostCompositionAsync(builder.Services, app.Services, false);
        Assert.Null(app.Services.GetService<IRedisErrorStore>());
        Assert.Null(app.Services.GetService<IProviderErrorTrackingService>());
        Assert.Null(app.Services.GetService<ConduitLLM.Core.Services.IVirtualKeyRateLimitService>());
        Assert.DoesNotContain(
            app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            registration => registration.Name == "redis");
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
        AssertFunctionClientComposition(builder.Services, app.Services, gateway);
        AssertFunctionsEndpointComposition(builder.Services, app.Services, gateway);
        await AssertFunctionCostCompositionAsync(builder.Services, app.Services, gateway);
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
        AssertDatabaseComposition(descriptors, provider);
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

    private static void AssertFunctionClientComposition(IServiceCollection descriptors, IServiceProvider provider, bool gateway)
    {
        var descriptor = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionClientFactory));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var factory = Assert.IsType<FunctionClientFactory>(first.ServiceProvider.GetRequiredService<IFunctionClientFactory>());
        Assert.Same(factory, first.ServiceProvider.GetRequiredService<IFunctionClientFactory>());
        Assert.NotSame(factory, second.ServiceProvider.GetRequiredService<IFunctionClientFactory>());
#if CONDUIT_NATIVE_AOT
        if (gateway)
        {
            Assert.NotNull(descriptor.ImplementationFactory);
            var store = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionExecutionConfigurationStore));
            Assert.Equal(ServiceLifetime.Singleton, store.Lifetime);
            Assert.Equal(typeof(NpgsqlFunctionExecutionConfigurationStore), store.ImplementationType);
            var resolvedStore = first.ServiceProvider.GetRequiredService<IFunctionExecutionConfigurationStore>();
            Assert.Same(resolvedStore,
                second.ServiceProvider.GetRequiredService<IFunctionExecutionConfigurationStore>());
            Assert.Same(resolvedStore, typeof(FunctionClientFactory).GetField("_runtimeStore",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(factory));
            return;
        }
#endif
        Assert.Equal(typeof(FunctionClientFactory), descriptor.ImplementationType);
    }

    private static async Task AssertFunctionCostCompositionAsync(IServiceCollection descriptors, IServiceProvider provider, bool gateway)
    {
        var descriptor = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionCostService));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var service = first.ServiceProvider.GetRequiredService<IFunctionCostService>();
        Assert.Same(service, first.ServiceProvider.GetRequiredService<IFunctionCostService>());
        Assert.NotSame(service, second.ServiceProvider.GetRequiredService<IFunctionCostService>());
        var expectedType = typeof(FunctionCostService);
#if CONDUIT_NATIVE_AOT
        if (gateway)
        {
            expectedType = typeof(StoreBackedFunctionCostService);
            var store = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionPricingRuntimeStore));
            Assert.Equal(ServiceLifetime.Singleton, store.Lifetime);
            Assert.Equal(typeof(NpgsqlFunctionPricingRuntimeStore), store.ImplementationType);
            Assert.Same(first.ServiceProvider.GetRequiredService<IFunctionPricingRuntimeStore>(),
                second.ServiceProvider.GetRequiredService<IFunctionPricingRuntimeStore>());
        }
#endif
        Assert.Equal(expectedType, descriptor.ImplementationType);
        Assert.IsType(expectedType, service);
        var calculator = Assert.IsType<FunctionCostCalculationService>(
            first.ServiceProvider.GetRequiredService<IFunctionCostCalculationService>());
        var configurationId = Random.Shared.Next(1, int.MaxValue);
        var key = $"FunctionCost:Config:{configurationId}";
        var memory = provider.GetRequiredService<IMemoryCache>();
        memory.Set(key, new FunctionCost
        {
            Id = 91, CostName = "Composition pricing", PricingModel = FunctionPricingModel.FlatRate,
            CostPerExecution = 0.12500001m, BaseCost = 0.12500001m
        });
        try
        {
            Assert.Equal(0.12500001m, await calculator.EstimateCostAsync(configurationId,
                new Dictionary<string, object> { ["query"] = "snow" }));
            Assert.Equal(0.12500001m, await calculator.CalculateCostAsync(configurationId, new FunctionExecutionUsage()));
        }
        finally
        {
            memory.Remove(key);
        }
    }

    private static void AssertFunctionsEndpointComposition(IServiceCollection descriptors, IServiceProvider provider, bool gateway)
    {
        if (!gateway)
        {
            Assert.DoesNotContain(descriptors, value => value.ServiceType == typeof(FunctionsEndpoints));
            return;
        }

        var descriptor = Assert.Single(descriptors, value => value.ServiceType == typeof(FunctionsEndpoints));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var handler = Assert.IsType<FunctionsEndpoints>(first.ServiceProvider.GetRequiredService<FunctionsEndpoints>());
        var other = Assert.IsType<FunctionsEndpoints>(second.ServiceProvider.GetRequiredService<FunctionsEndpoints>());
        Assert.Same(handler, first.ServiceProvider.GetRequiredService<FunctionsEndpoints>());
        Assert.NotSame(handler, other);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var storeField = typeof(FunctionsEndpoints).GetField("_runtimeConfigurationStore", flags);
        var repositoryField = typeof(FunctionsEndpoints).GetField("_configurationRepository", flags);
        Assert.NotNull(storeField);
        Assert.NotNull(repositoryField);
#if CONDUIT_NATIVE_AOT
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.Null(descriptor.ImplementationType);
        var store = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionExecutionConfigurationStore));
        Assert.Equal(ServiceLifetime.Singleton, store.Lifetime);
        Assert.Equal(typeof(NpgsqlFunctionExecutionConfigurationStore), store.ImplementationType);
        var resolvedStore = first.ServiceProvider.GetRequiredService<IFunctionExecutionConfigurationStore>();
        Assert.Same(resolvedStore, second.ServiceProvider.GetRequiredService<IFunctionExecutionConfigurationStore>());
        Assert.Same(resolvedStore, storeField.GetValue(handler));
        Assert.Same(resolvedStore, storeField.GetValue(other));
        Assert.Null(repositoryField.GetValue(handler));
        Assert.Null(repositoryField.GetValue(other));
#else
        Assert.Equal(typeof(FunctionsEndpoints), descriptor.ImplementationType);
        Assert.Null(descriptor.ImplementationFactory);
        Assert.Null(storeField.GetValue(handler));
        Assert.Null(storeField.GetValue(other));
        Assert.Same(first.ServiceProvider.GetRequiredService<IFunctionConfigurationRepository>(), repositoryField.GetValue(handler));
        Assert.Same(second.ServiceProvider.GetRequiredService<IFunctionConfigurationRepository>(), repositoryField.GetValue(other));
#endif
    }

    private static void AssertDatabaseComposition(IServiceCollection descriptors, IServiceProvider provider)
    {
        var registration = Assert.Single(descriptors,
            descriptor => descriptor.ServiceType == typeof(IConnectionPoolWarmup));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        Assert.Equal(typeof(NpgsqlConnectionPoolWarmup), registration.ImplementationType);
        var warmup = Assert.IsType<NpgsqlConnectionPoolWarmup>(provider.GetRequiredService<IConnectionPoolWarmup>());
        using var scope = provider.CreateScope();
        Assert.Same(warmup, scope.ServiceProvider.GetRequiredService<IConnectionPoolWarmup>());

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(descriptors,
            descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)).Lifetime);
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        var options = provider.GetRequiredService<DbContextOptions<ConduitDbContext>>();
#pragma warning disable EF1001 // Test-only inspection proves the pinned provider uses the exact host-owned pool.
        Assert.Same(dataSource, options.FindExtension<NpgsqlOptionsExtension>()!.DataSource);
#pragma warning restore EF1001
        Assert.Contains(provider.GetRequiredService<QueryMonitoringInterceptor>(),
            options.FindExtension<CoreOptionsExtension>()!.Interceptors!);

        using var context = provider.GetRequiredService<IDbContextFactory<ConduitDbContext>>().CreateDbContext();
        var retry = Assert.IsType<NpgsqlRetryingExecutionStrategy>(context.Database.CreateExecutionStrategy());
        Assert.Equal(5, retry.MaxRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(10), retry.MaxRetryDelay);
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
