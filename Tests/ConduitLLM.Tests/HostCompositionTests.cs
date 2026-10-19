using System.Text.Json;

using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interceptors;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core;
using ConduitLLM.Core.Data;
using ConduitLLM.Core.Data.Extensions;
using ConduitLLM.Core.Data.Health;
using ConduitLLM.Core.Data.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Services;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Interfaces;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.RateLimiting;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.Services.SpendNotification;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    [Fact]
    public void GenericDatabaseFactory_DoesNotBorrowAnUnrelatedRegisteredDataSource()
    {
        const string genericConnection = "Host=localhost;Database=generic;Username=generic;Password=test-only;Minimum Pool Size=1;Maximum Pool Size=100";
        var manager = new Moq.Mock<IConnectionStringManager>();
        manager.Setup(value => value.GetProviderAndConnectionString(Moq.It.IsAny<Action<string>>()))
            .Returns(("postgres", genericConnection));
        using var unrelatedSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=unrelated;Username=unrelated;Password=test-only;Minimum Pool Size=7;Maximum Pool Size=13");
        var descriptors = new ServiceCollection();
        descriptors.AddLogging();
        descriptors.AddSingleton(manager.Object);
        descriptors.AddSingleton(unrelatedSource);
        descriptors.AddDatabaseConnectionFactory();
        using var provider = descriptors.BuildServiceProvider();

        var factory = Assert.IsType<DatabaseConnectionFactory>(provider.GetRequiredService<IDatabaseConnectionFactory>());
        using var connection = factory.CreateConnection();
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);

        Assert.Equal("generic", settings.Database);
        Assert.Equal(1, settings.MinPoolSize);
        Assert.Equal(100, settings.MaxPoolSize);
        Assert.Null(typeof(DatabaseConnectionFactory).GetField("_dataSource",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(factory));
    }

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
        AssertWorkerProfile(builder.Services, app.Services, false);
        AssertBillingAuditComposition(builder.Services, app.Services);
        AssertAdminGlobalSettingComposition(builder.Services, app.Services);
        AssertAdminIpFilterComposition(builder.Services, app.Services);
        AssertFunctionClientComposition(builder.Services, app.Services, false);
        AssertFunctionDiscoveryComposition(builder.Services, app.Services, false);
        AssertFunctionExecutionComposition(builder.Services, app.Services, false);
        AssertFunctionsEndpointComposition(builder.Services, app.Services, false);
        AssertChatEndpointComposition(builder.Services, app.Services, false);
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
        AssertWorkerProfile(builder.Services, app.Services, gateway);
        AssertBillingAuditComposition(builder.Services, app.Services);
        AssertFunctionClientComposition(builder.Services, app.Services, gateway);
        AssertFunctionDiscoveryComposition(builder.Services, app.Services, gateway);
        AssertFunctionExecutionComposition(builder.Services, app.Services, gateway);
        AssertFunctionsEndpointComposition(builder.Services, app.Services, gateway);
        AssertChatEndpointComposition(builder.Services, app.Services, gateway);
        await AssertFunctionCostCompositionAsync(builder.Services, app.Services, gateway);
        if (!gateway)
        {
            AssertAdminGlobalSettingComposition(builder.Services, app.Services);
            AssertAdminIpFilterComposition(builder.Services, app.Services);
            Assert.Null(app.Services.GetService<IVirtualKeyManagementAudienceService>());
        }
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(StackExchange.Redis.IConnectionMultiplexer));
        if (gateway)
        {
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(SignalRMetrics));
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(ISignalRMetrics));
            Assert.Same(app.Services.GetRequiredService<SignalRMetrics>(), app.Services.GetRequiredService<ISignalRMetrics>());
            var audiences = app.Services.GetRequiredService<VirtualKeyManagementAudienceService>();
            Assert.Same(audiences, app.Services.GetRequiredService<IVirtualKeyManagementAudienceService>());
            Assert.Same(audiences, Assert.Single(app.Services.GetServices<IHostedService>()
                .OfType<VirtualKeyManagementAudienceService>()));
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
        AssertModelCostComposition(descriptors, scope.ServiceProvider);
        AssertProviderSelectionComposition(descriptors, scope.ServiceProvider);
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

    /// <summary>Verifies the actual factory receives the fresh Native provider reader and JIT retains its fallback.</summary>
    private static void AssertProviderSelectionComposition(IServiceCollection descriptors, IServiceProvider provider)
    {
        var factory = Assert.IsType<ConduitLLM.Providers.DatabaseAwareLLMClientFactory>(
            provider.GetRequiredService<ILLMClientFactory>());
        var reader = provider.GetService<IProviderRuntimeSelectionReader>();
        var field = typeof(ConduitLLM.Providers.DatabaseAwareLLMClientFactory).GetField(
            "_runtimeProviderSelectionReader",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var nativeRepository = descriptors.Any(descriptor =>
            descriptor.ServiceType == typeof(IProviderRepository) &&
            descriptor.ImplementationType == typeof(NpgsqlProviderRepository));
        if (nativeRepository)
        {
            Assert.Same(provider.GetRequiredService<IProviderRepository>(), reader);
            Assert.Same(reader, field.GetValue(factory));
        }
        else
        {
            Assert.Null(reader);
            Assert.Null(field.GetValue(factory));
        }
    }

    /// <summary>Resolves the billing factory and verifies the Native scalar profile and loader.</summary>
    private static void AssertModelCostComposition(IServiceCollection descriptors, IServiceProvider provider)
    {
        var service = Assert.IsType<FusionModelCostService>(
            provider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelCostService>());
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var nativeProfile = typeof(FusionModelCostService).GetField("_runtimeReadOnly", flags);
        Assert.NotNull(nativeProfile);
#if CONDUIT_NATIVE_AOT
        if (descriptors.Any(value => value.ServiceType == typeof(ChatEndpoints)))
        {
            Assert.True((bool)nativeProfile.GetValue(service)!);
            Assert.Same(provider.GetRequiredService<StoreBackedModelCostService>(),
                typeof(FusionModelCostService).GetField("_inner", flags)!.GetValue(service));
            return;
        }
#endif
        Assert.False((bool)nativeProfile.GetValue(service)!);
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

    private static void AssertFunctionDiscoveryComposition(IServiceCollection descriptors, IServiceProvider provider, bool gateway)
    {
        if (!gateway)
        {
            Assert.DoesNotContain(descriptors, value => value.ServiceType == typeof(IFunctionDiscoveryService));
            return;
        }

        var descriptor = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionDiscoveryService));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var service = Assert.IsType<ConduitLLM.Core.Services.FunctionDiscoveryService>(
            first.ServiceProvider.GetRequiredService<IFunctionDiscoveryService>());
        var other = Assert.IsType<ConduitLLM.Core.Services.FunctionDiscoveryService>(
            second.ServiceProvider.GetRequiredService<IFunctionDiscoveryService>());
        Assert.Same(service, first.ServiceProvider.GetRequiredService<IFunctionDiscoveryService>());
        Assert.NotSame(service, other);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var serviceType = typeof(ConduitLLM.Core.Services.FunctionDiscoveryService);
        var storeField = serviceType.GetField("_runtimeConfigurationStore", flags);
        var repositoryField = serviceType.GetField("_functionConfigRepository", flags);
        Assert.NotNull(storeField);
        Assert.NotNull(repositoryField);
        Assert.Same(first.ServiceProvider.GetRequiredService<IFunctionClientFactory>(),
            serviceType.GetField("_clientFactory", flags)!.GetValue(service));
        Assert.Same(first.ServiceProvider.GetService<IFunctionDiscoveryCacheService>(),
            serviceType.GetField("_cacheService", flags)!.GetValue(service));
#if CONDUIT_NATIVE_AOT
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.Null(descriptor.ImplementationType);
        var store = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionExecutionConfigurationStore));
        Assert.Equal(ServiceLifetime.Singleton, store.Lifetime);
        Assert.Equal(typeof(NpgsqlFunctionExecutionConfigurationStore), store.ImplementationType);
        var resolvedStore = first.ServiceProvider.GetRequiredService<IFunctionExecutionConfigurationStore>();
        Assert.Same(resolvedStore, second.ServiceProvider.GetRequiredService<IFunctionExecutionConfigurationStore>());
        Assert.Same(resolvedStore, storeField.GetValue(service));
        Assert.Same(resolvedStore, storeField.GetValue(other));
        Assert.Null(repositoryField.GetValue(service));
        Assert.Null(repositoryField.GetValue(other));
#else
        Assert.Equal(serviceType, descriptor.ImplementationType);
        Assert.Null(descriptor.ImplementationFactory);
        Assert.Null(storeField.GetValue(service));
        Assert.Null(storeField.GetValue(other));
        Assert.Same(first.ServiceProvider.GetRequiredService<IFunctionConfigurationRepository>(), repositoryField.GetValue(service));
        Assert.Same(second.ServiceProvider.GetRequiredService<IFunctionConfigurationRepository>(), repositoryField.GetValue(other));
#endif
    }

    private static void AssertFunctionExecutionComposition(IServiceCollection descriptors, IServiceProvider provider, bool gateway)
    {
        var descriptor = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionExecutionService));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var service = Assert.IsType<FunctionExecutionService>(first.ServiceProvider.GetRequiredService<IFunctionExecutionService>());
        var other = Assert.IsType<FunctionExecutionService>(second.ServiceProvider.GetRequiredService<IFunctionExecutionService>());
        Assert.Same(service, first.ServiceProvider.GetRequiredService<IFunctionExecutionService>());
        Assert.NotSame(service, other);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Field(FunctionExecutionService target, string name) =>
            typeof(FunctionExecutionService).GetField(name, flags)!.GetValue(target);
        Assert.Same(first.ServiceProvider.GetRequiredService<IFunctionCostCalculationService>(), Field(service, "_costCalculationService"));
        Assert.Same(first.ServiceProvider.GetRequiredService<IFunctionClientFactory>(), Field(service, "_clientFactory"));
        Assert.Same(first.ServiceProvider.GetRequiredService<ConduitLLM.Functions.Security.IFunctionCredentialProtector>(),
            Field(service, "_credentialProtector"));
        if (gateway)
        {
            var claimDescriptor = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionExecutionIdempotencyStore));
            Assert.Equal(ServiceLifetime.Singleton, claimDescriptor.Lifetime);
            Assert.Null(claimDescriptor.ImplementationType);
            Assert.NotNull(claimDescriptor.ImplementationFactory);
            var claims = Assert.IsType<NpgsqlFunctionExecutionIdempotencyStore>(
                first.ServiceProvider.GetRequiredService<IFunctionExecutionIdempotencyStore>());
            Assert.Same(claims, second.ServiceProvider.GetRequiredService<IFunctionExecutionIdempotencyStore>());
            Assert.Same(claims, Field(service, "_idempotencyStore"));
            Assert.Same(claims, Field(other, "_idempotencyStore"));
        }
        else
        {
            Assert.Null(Field(service, "_idempotencyStore"));
            Assert.Null(Field(other, "_idempotencyStore"));
        }
#if CONDUIT_NATIVE_AOT
        if (gateway)
        {
            Assert.NotNull(descriptor.ImplementationFactory);
            Assert.Null(descriptor.ImplementationType);
            foreach (var (contract, implementation, field) in new[]
                     {
                         (typeof(IFunctionExecutionConfigurationStore), typeof(NpgsqlFunctionExecutionConfigurationStore), "_runtimeConfigurationStore"),
                         (typeof(IFunctionExecutionRuntimeWriter), typeof(NpgsqlFunctionExecutionRuntimeWriter), "_runtimeWriter"),
                         (typeof(IFunctionExecutionRuntimeReader), typeof(NpgsqlFunctionExecutionRuntimeReader), "_runtimeReader")
                     })
            {
                var port = Assert.Single(descriptors, value => value.ServiceType == contract);
                Assert.Equal(ServiceLifetime.Singleton, port.Lifetime);
                Assert.Equal(implementation, port.ImplementationType);
                var resolved = first.ServiceProvider.GetRequiredService(contract);
                Assert.IsType(implementation, resolved);
                Assert.Same(resolved, second.ServiceProvider.GetRequiredService(contract));
                Assert.Same(resolved, Field(service, field));
                Assert.Same(resolved, Field(other, field));
            }
            foreach (var field in new[] { "_functionConfigurationRepository", "_credentialRepository", "_executionRepository" })
            {
                Assert.Null(Field(service, field));
                Assert.Null(Field(other, field));
            }
            return;
        }
#endif
        if (gateway)
        {
            Assert.Null(descriptor.ImplementationType);
            Assert.NotNull(descriptor.ImplementationFactory);
        }
        else
        {
            Assert.Equal(typeof(FunctionExecutionService), descriptor.ImplementationType);
            Assert.Null(descriptor.ImplementationFactory);
        }
        foreach (var field in new[] { "_runtimeConfigurationStore", "_runtimeWriter", "_runtimeReader" })
        {
            Assert.Null(Field(service, field));
            Assert.Null(Field(other, field));
        }
        foreach (var (contract, field) in new[]
                 {
                     (typeof(IFunctionConfigurationRepository), "_functionConfigurationRepository"),
                     (typeof(IFunctionCredentialRepository), "_credentialRepository"),
                     (typeof(IFunctionExecutionRepository), "_executionRepository")
                 })
        {
            Assert.Same(first.ServiceProvider.GetRequiredService(contract), Field(service, field));
            Assert.Same(second.ServiceProvider.GetRequiredService(contract), Field(other, field));
        }
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
        var memory = provider.GetRequiredService<IMemoryCache>();
        var cache = service.GetType().GetField("_cache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
        var capture = (Task)cache.GetType().GetMethod("CaptureAsync")!.Invoke(cache, [CancellationToken.None])!;
        await capture;
        var snapshot = capture.GetType().GetProperty("Result")!.GetValue(capture)!;
        var key = (string)snapshot.GetType().GetMethod("BuildKey",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(snapshot, [$"Config:{configurationId}"])!;
        var cost = new FunctionCost
        {
            Id = 91, CostName = "Composition pricing", PricingModel = FunctionPricingModel.FlatRate,
            CostPerExecution = 0.12500001m, BaseCost = 0.12500001m
        };
        try
        {
            await (Task)snapshot.GetType().GetMethod("SetAsync")!.MakeGenericMethod(typeof(FunctionCost))
                .Invoke(snapshot, [$"Config:{configurationId}", cost, FunctionsJsonContext.Default.FunctionCost, CancellationToken.None])!;
            Assert.True(memory.TryGetValue(key, out _));
            Assert.Equal(0.12500001m, await calculator.EstimateCostAsync(configurationId,
                new Dictionary<string, object> { ["query"] = "snow" }));
            Assert.Equal(0.12500001m, await calculator.CalculateCostAsync(configurationId, new FunctionExecutionUsage()));
        }
        finally
        {
            memory.Remove(key);
            if (provider.GetService<IDistributedCache>() is { } distributed)
                await distributed.RemoveAsync(key);
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

    private static void AssertChatEndpointComposition(IServiceCollection descriptors, IServiceProvider provider, bool gateway)
    {
        if (!gateway)
        {
            Assert.DoesNotContain(descriptors, value => value.ServiceType == typeof(ChatEndpoints));
            return;
        }

        var descriptor = Assert.Single(descriptors, value => value.ServiceType == typeof(ChatEndpoints));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var handler = Assert.IsType<ChatEndpoints>(first.ServiceProvider.GetRequiredService<ChatEndpoints>());
        var other = Assert.IsType<ChatEndpoints>(second.ServiceProvider.GetRequiredService<ChatEndpoints>());
        Assert.Same(handler, first.ServiceProvider.GetRequiredService<ChatEndpoints>());
        Assert.NotSame(handler, other);
#if CONDUIT_NATIVE_AOT
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.Null(descriptor.ImplementationType);
        var store = Assert.Single(descriptors, value => value.ServiceType == typeof(IFunctionExecutionConfigurationStore));
        Assert.Equal(ServiceLifetime.Singleton, store.Lifetime);
        Assert.Equal(typeof(NpgsqlFunctionExecutionConfigurationStore), store.ImplementationType);
        var resolvedStore = first.ServiceProvider.GetRequiredService<IFunctionExecutionConfigurationStore>();
        Assert.Same(resolvedStore, second.ServiceProvider.GetRequiredService<IFunctionExecutionConfigurationStore>());
        Assert.Same(resolvedStore, Field(typeof(ChatEndpoints), "_runtimeConfigurationStore", handler));
        Assert.Same(resolvedStore, Field(typeof(ChatEndpoints), "_runtimeConfigurationStore", other));
        Assert.Null(Field(typeof(ChatEndpoints), "_functionConfigRepository", handler));
        Assert.Null(Field(typeof(ChatEndpoints), "_functionConfigRepository", other));
#else
        Assert.Equal(typeof(ChatEndpoints), descriptor.ImplementationType);
        Assert.Null(descriptor.ImplementationFactory);
        Assert.Null(Field(typeof(ChatEndpoints), "_runtimeConfigurationStore", handler));
        Assert.Null(Field(typeof(ChatEndpoints), "_runtimeConfigurationStore", other));
        Assert.Same(first.ServiceProvider.GetRequiredService<IFunctionConfigurationRepository>(),
            Field(typeof(ChatEndpoints), "_functionConfigRepository", handler));
        Assert.Same(second.ServiceProvider.GetRequiredService<IFunctionConfigurationRepository>(),
            Field(typeof(ChatEndpoints), "_functionConfigRepository", other));
#endif
        AssertDependencies(first.ServiceProvider, handler);
        AssertDependencies(second.ServiceProvider, other);

        static void AssertDependencies(IServiceProvider scope, ChatEndpoints endpoint)
        {
            Assert.Same(scope.GetRequiredService<Conduit>(), Field(typeof(ChatEndpoints), "_conduit", endpoint));
            Assert.Same(scope.GetRequiredService<ILogger<ChatEndpoints>>(), Field(typeof(ChatEndpoints), "_logger", endpoint));
            Assert.Same(scope.GetRequiredService<IModelProviderMappingService>(),
                Field(typeof(ChatEndpoints), "_modelMappingService", endpoint));
            Assert.Same(scope.GetRequiredService<JsonSerializerOptions>(),
                Field(typeof(ChatEndpoints), "_jsonSerializerOptions", endpoint));
            Assert.Same(scope.GetRequiredService<IGlobalSettingsCacheService>(),
                Field(typeof(ChatEndpoints), "_globalSettingsCacheService", endpoint));
            Assert.Same(scope.GetRequiredService<IUsageEstimationService>(),
                Field(typeof(ChatEndpoints), "_usageEstimationService", endpoint));
            Assert.Same(scope.GetRequiredService<IOptions<UsageTrackingOptions>>().Value,
                Field(typeof(ChatEndpoints), "_usageTrackingOptions", endpoint));
            Assert.Same(scope.GetRequiredService<IChatSpendEstimator>(),
                Field(typeof(ChatEndpoints), "_chatSpendEstimator", endpoint));
            Assert.Same(scope.GetRequiredService<ISpendReservationService>(),
                Field(typeof(ChatEndpoints), "_spendReservationService", endpoint));
            Assert.Same(scope.GetRequiredService<IOptions<BillingAdmissionOptions>>().Value,
                Field(typeof(ChatEndpoints), "_billingAdmissionOptions", endpoint));
            Assert.Same(scope.GetRequiredService<IProviderErrorTranslator>(),
                Field(typeof(ChatEndpoints), "_providerErrorTranslator", endpoint));
            Assert.Same(scope.GetRequiredService<IHttpContextAccessor>(),
                Field(typeof(GatewayEndpointHandlerBase), "_httpContextAccessor", endpoint));
            Assert.Same(scope.GetRequiredService<IEventPublisher>(),
                Field(typeof(GatewayEndpointHandlerBase), "_eventPublisher", endpoint));
        }

        static object? Field(Type owner, string name, ChatEndpoints endpoint)
        {
            var field = owner.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            return field.GetValue(endpoint);
        }
    }

    private static void AssertAdminIpFilterComposition(IServiceCollection descriptors, IServiceProvider provider)
    {
        var descriptor = Assert.Single(descriptors, item => item.ServiceType == typeof(IIpFilterRepository));
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var firstRepository = first.ServiceProvider.GetRequiredService<IIpFilterRepository>();
        var secondRepository = second.ServiceProvider.GetRequiredService<IIpFilterRepository>();
#if CONDUIT_NATIVE_AOT
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(NpgsqlIpFilterRepository), descriptor.ImplementationType);
        var repository = Assert.IsType<NpgsqlIpFilterRepository>(firstRepository);
        Assert.Same(repository, secondRepository);
        var dataSourceField = typeof(NpgsqlIpFilterRepository).GetField("_dataSource",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(dataSourceField);
        Assert.Same(provider.GetRequiredService<NpgsqlDataSource>(), dataSourceField.GetValue(repository));
#else
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(IpFilterRepository), descriptor.ImplementationType);
        Assert.IsType<IpFilterRepository>(firstRepository);
        Assert.IsType<IpFilterRepository>(secondRepository);
        Assert.NotSame(firstRepository, secondRepository);
#endif
        var serviceDescriptor = Assert.Single(descriptors, item => item.ServiceType == typeof(IAdminIpFilterService));
        Assert.Equal(ServiceLifetime.Scoped, serviceDescriptor.Lifetime);
        Assert.Equal(typeof(AdminIpFilterService), serviceDescriptor.ImplementationType);
        var firstService = Assert.IsType<AdminIpFilterService>(first.ServiceProvider.GetRequiredService<IAdminIpFilterService>());
        var secondService = Assert.IsType<AdminIpFilterService>(second.ServiceProvider.GetRequiredService<IAdminIpFilterService>());
        Assert.NotSame(firstService, secondService);
        var repositoryField = typeof(AdminIpFilterService).GetField("_ipFilterRepository",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(repositoryField);
        Assert.Same(firstRepository, repositoryField.GetValue(firstService));
        Assert.Same(secondRepository, repositoryField.GetValue(secondService));
    }

    private static void AssertAdminGlobalSettingComposition(IServiceCollection descriptors, IServiceProvider provider)
    {
        var repositoryDescriptor = Assert.Single(descriptors,
            descriptor => descriptor.ServiceType == typeof(IGlobalSettingRepository));
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var firstRepository = first.ServiceProvider.GetRequiredService<IGlobalSettingRepository>();
        var secondRepository = second.ServiceProvider.GetRequiredService<IGlobalSettingRepository>();
        Assert.Same(firstRepository, first.ServiceProvider.GetRequiredService<IGlobalSettingRepository>());
        Assert.Same(secondRepository, second.ServiceProvider.GetRequiredService<IGlobalSettingRepository>());
#if CONDUIT_NATIVE_AOT
        Assert.Equal(ServiceLifetime.Singleton, repositoryDescriptor.Lifetime);
        Assert.Equal(typeof(NpgsqlGlobalSettingRepository), repositoryDescriptor.ImplementationType);
        var repository = Assert.IsType<NpgsqlGlobalSettingRepository>(firstRepository);
        Assert.Same(repository, secondRepository);
        var dataSourceField = typeof(NpgsqlGlobalSettingRepository).GetField("_dataSource",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(dataSourceField);
        Assert.Same(provider.GetRequiredService<NpgsqlDataSource>(), dataSourceField.GetValue(repository));
#else
        Assert.Equal(ServiceLifetime.Scoped, repositoryDescriptor.Lifetime);
        Assert.Equal(typeof(GlobalSettingRepository), repositoryDescriptor.ImplementationType);
        Assert.IsType<GlobalSettingRepository>(firstRepository);
        Assert.IsType<GlobalSettingRepository>(secondRepository);
        Assert.NotSame(firstRepository, secondRepository);
#endif
        var serviceDescriptor = Assert.Single(descriptors,
            descriptor => descriptor.ServiceType == typeof(IAdminGlobalSettingService));
        Assert.Equal(ServiceLifetime.Scoped, serviceDescriptor.Lifetime);
        Assert.Equal(typeof(AdminGlobalSettingService), serviceDescriptor.ImplementationType);
        var firstService = Assert.IsType<AdminGlobalSettingService>(
            first.ServiceProvider.GetRequiredService<IAdminGlobalSettingService>());
        var secondService = Assert.IsType<AdminGlobalSettingService>(
            second.ServiceProvider.GetRequiredService<IAdminGlobalSettingService>());
        Assert.Same(firstService, first.ServiceProvider.GetRequiredService<IAdminGlobalSettingService>());
        Assert.Same(secondService, second.ServiceProvider.GetRequiredService<IAdminGlobalSettingService>());
        Assert.NotSame(firstService, secondService);
        var repositoryField = typeof(AdminGlobalSettingService).GetField("_globalSettingRepository",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(repositoryField);
        Assert.Same(firstRepository, repositoryField.GetValue(firstService));
        Assert.Same(secondRepository, repositoryField.GetValue(secondService));
    }

    private static void AssertWorkerProfile(IServiceCollection descriptors, IServiceProvider provider, bool gateway)
    {
        var hosted = provider.GetServices<IHostedService>().ToArray();
        var innerField = typeof(LeaderElectedServiceWrapper).GetField("_innerService",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(innerField);
        var queueInnerField = typeof(BatchAuditQueueHostedService).GetField("_innerService",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(queueInnerField);
        var workers = hosted.Select(service => service is LeaderElectedServiceWrapper
            ? Assert.IsAssignableFrom<IHostedService>(innerField.GetValue(service))
            : service is BatchAuditQueueHostedService
                ? Assert.IsAssignableFrom<IHostedService>(queueInnerField.GetValue(service))
                : service).ToArray();
        Type[] unsupportedWorkers = gateway
            ? [typeof(RequestLogService), typeof(FunctionCallAuditService), typeof(BillingReconciliationService),
                typeof(PricingAuditService), typeof(DiscoveryCacheWarmingService)]
            : [typeof(ModelCostCanaryHostedService), typeof(PricingConfigurationAuditHostedService),
                typeof(PricingAuditService), typeof(AdminOperationsMetricsService),
                typeof(OpenRouterMetadataSyncService), typeof(MediaStorageConfigurationGuard), typeof(MediaCleanupService)];
        using var scope = provider.CreateScope();
        var evaluator = Assert.IsType<PricingRulesEvaluator>(
            scope.ServiceProvider.GetRequiredService<IPricingRulesEvaluator>());
        var auditField = typeof(PricingRulesEvaluator).GetField("_auditService",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(auditField);
#if CONDUIT_NATIVE_AOT
        foreach (var type in unsupportedWorkers)
        {
            Assert.DoesNotContain(workers, worker => worker.GetType() == type);
            // Media endpoints retain safe construction of their manual helpers.
            if (type != typeof(MediaStorageConfigurationGuard) && type != typeof(MediaCleanupService))
                Assert.DoesNotContain(descriptors, descriptor => descriptor.ServiceType == type);
        }
        Assert.DoesNotContain(descriptors, descriptor => descriptor.ServiceType == typeof(IPricingAuditService));
        Assert.Empty(hosted.OfType<BatchAuditQueueHostedService>());
        Assert.Empty(workers.OfType<BatchAuditRetentionHostedService>());
        Assert.Null(auditField.GetValue(evaluator));
        if (gateway)
        {
            Assert.DoesNotContain(descriptors, descriptor => descriptor.ServiceType == typeof(IRequestLogService));
            Assert.DoesNotContain(descriptors, descriptor => descriptor.ServiceType == typeof(IFunctionCallAuditService));
            var writer = Assert.IsType<StoreBackedRequestLogRuntimeWriter>(
                provider.GetRequiredService<IRequestLogRuntimeWriter>());
            Assert.Equal(ServiceLifetime.Singleton, Assert.Single(descriptors,
                descriptor => descriptor.ServiceType == typeof(IRequestLogRuntimeWriter)).Lifetime);
            Assert.Same(writer, scope.ServiceProvider.GetRequiredService<IRequestLogRuntimeWriter>());
            var store = Assert.IsType<NpgsqlBatchingRequestLogRuntimeStore>(provider.GetRequiredService<IRequestLogRuntimeStore>());
            var storeField = typeof(StoreBackedRequestLogRuntimeWriter).GetField("_store",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(storeField);
            Assert.Same(store, storeField.GetValue(writer));
            Assert.Same(store, scope.ServiceProvider.GetRequiredService<IRequestLogRuntimeStore>());
            var drain = Assert.Single(hosted, worker => worker.GetType().Name == "NativeRequestLogDrainHostedService");
            Assert.False(drain is IDisposable);
            Assert.False(drain is IAsyncDisposable);
        }
        else
        {
            Assert.DoesNotContain(descriptors,
                descriptor => descriptor.ServiceType == typeof(ConduitLLM.Admin.Endpoints.PricingEndpoints));
            Assert.Empty(hosted.OfType<LeaderElectedServiceWrapper>());
        }
#else
        foreach (var type in unsupportedWorkers)
            Assert.Single(workers, worker => worker.GetType() == type);
        var audit = Assert.IsType<PricingAuditService>(provider.GetRequiredService<IPricingAuditService>());
        Assert.Same(audit, auditField.GetValue(evaluator));
        Assert.Same(audit, Assert.Single(workers.OfType<PricingAuditService>()));
        AssertAuditQueueComposition(hosted, audit, nameof(PricingAuditService));
        Assert.Equal(gateway ? 4 : 2, hosted.OfType<BatchAuditQueueHostedService>().Count());
        if (gateway)
        {
            var requestLog = Assert.IsType<RequestLogService>(provider.GetRequiredService<IRequestLogService>());
            Assert.Same(requestLog, provider.GetRequiredService<IRequestLogRuntimeWriter>());
            Assert.Same(requestLog, Assert.Single(workers.OfType<RequestLogService>()));
            AssertAuditQueueComposition(hosted, requestLog, nameof(RequestLogService));
            Assert.Same(provider.GetRequiredService<IFunctionCallAuditService>(),
                Assert.Single(workers.OfType<FunctionCallAuditService>()));
            AssertAuditQueueComposition(hosted, provider.GetRequiredService<IFunctionCallAuditService>(),
                nameof(FunctionCallAuditService));
            Assert.Same(provider.GetRequiredService<BillingReconciliationService>(),
                Assert.Single(workers.OfType<BillingReconciliationService>()));
        }
        else
        {
            Assert.Single(descriptors,
                descriptor => descriptor.ServiceType == typeof(ConduitLLM.Admin.Endpoints.PricingEndpoints));
        }
#endif
        // These workers serve the supported native slices and must survive profile selection.
        var cache = Assert.IsType<GlobalSettingsCacheService>(provider.GetRequiredService<IGlobalSettingsCacheService>());
        Assert.Same(cache, Assert.Single(workers.OfType<GlobalSettingsCacheService>()));
        Assert.Single(workers.OfType<ConduitLLM.Configuration.Data.MigrationWaitService>());
        Assert.Single(workers.OfType<CoordinatedConnectionPoolWarmer>());
        if (gateway)
        {
            Assert.Single(workers.OfType<BusinessMetricsService>());
            Assert.Single(workers.OfType<GatewayOperationsMetricsService>());
            Assert.Single(workers.OfType<TaskProcessingMetricsService>());
            Assert.Single(workers.OfType<AsyncTaskRetentionService>());
            Assert.Single(workers.OfType<MediaTaskLeaseRecoveryService>());
            Assert.Single(workers.OfType<ProviderKeyReprobeService>());
            Assert.Single(workers.OfType<GatewayHeartbeatPublisher>());
            var batch = Assert.IsType<BatchSpendUpdateService>(provider.GetRequiredService<IBatchSpendUpdateService>());
            var batchWorker = Assert.Single(workers.OfType<BatchSpendLeaderWorker>());
            Assert.Same(provider.GetRequiredService<BatchSpendLeaderWorker>(), batchWorker);
            AssertBorrowedSingleton(batchWorker, batch);
            AssertBorrowedSingleton(Assert.Single(hosted.OfType<BatchSpendLifecycleHostedService>()), batch);
            Assert.DoesNotContain(workers, worker => worker is BatchSpendUpdateService);

            var spend = Assert.IsType<DistributedSpendNotificationService>(provider.GetRequiredService<ISpendNotificationService>());
            Assert.Same(spend, Assert.Single(workers.OfType<DistributedSpendNotificationService>()));
            AssertBorrowedSingleton(Assert.Single(hosted.OfType<SpendNotificationLifecycleHostedService>()), spend);

            var webhook = Assert.IsType<WebhookDeliveryNotificationService>(provider.GetRequiredService<IWebhookDeliveryNotificationService>());
            Assert.Same(webhook, Assert.Single(workers.OfType<WebhookDeliveryNotificationService>()));
        }
        else
        {
            Assert.Single(workers.OfType<AdminHeartbeatPublisher>());
            var guard = Assert.IsType<MediaStorageConfigurationGuard>(provider.GetRequiredService<IMediaStorageConfigurationGuard>());
            Assert.Same(guard, provider.GetRequiredService<MediaStorageConfigurationGuard>());
            var cleanup = provider.GetRequiredService<MediaCleanupService>();
#if !CONDUIT_NATIVE_AOT
            Assert.Same(guard, Assert.Single(workers.OfType<MediaStorageConfigurationGuard>()));
            Assert.Same(cleanup, Assert.Single(workers.OfType<MediaCleanupService>()));
#endif
        }
    }

    private static void AssertBorrowedSingleton(IHostedService adapter, object singleton)
    {
        var captured = Assert.Single(adapter.GetType().GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == singleton.GetType());
        Assert.Same(singleton, captured.GetValue(adapter));
    }

    private static void AssertBillingAuditComposition(IServiceCollection descriptors, IServiceProvider provider)
    {
        var registration = Assert.Single(descriptors, descriptor => descriptor.ServiceType == typeof(IBillingAuditService));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        var audit = Assert.IsType<BillingAuditService>(provider.GetRequiredService<IBillingAuditService>());
        using var scope = provider.CreateScope();
        Assert.Same(audit, scope.ServiceProvider.GetRequiredService<IBillingAuditService>());
        var hosted = provider.GetServices<IHostedService>().ToArray();
        var runtimeField = typeof(BillingAuditService).GetField("_runtimeWriter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(runtimeField);
        var innerField = typeof(LeaderElectedServiceWrapper).GetField("_innerService",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(innerField);
#if CONDUIT_NATIVE_AOT
        var writerRegistration = Assert.Single(descriptors,
            descriptor => descriptor.ServiceType == typeof(IBillingAuditRuntimeWriter));
        Assert.Equal(ServiceLifetime.Singleton, writerRegistration.Lifetime);
        Assert.Equal(typeof(NpgsqlBillingAuditRuntimeWriter), writerRegistration.ImplementationType);
        var writer = Assert.IsType<NpgsqlBillingAuditRuntimeWriter>(provider.GetRequiredService<IBillingAuditRuntimeWriter>());
        Assert.Same(writer, scope.ServiceProvider.GetRequiredService<IBillingAuditRuntimeWriter>());
        Assert.Same(writer, runtimeField.GetValue(audit));
        Assert.Same(audit, Assert.Single(hosted.OfType<BillingAuditService>()));
        Assert.DoesNotContain(hosted.OfType<LeaderElectedServiceWrapper>(),
            wrapper => ReferenceEquals(innerField.GetValue(wrapper), audit));
#else
        Assert.Null(runtimeField.GetValue(audit));
        Assert.DoesNotContain(descriptors, descriptor => descriptor.ServiceType == typeof(IBillingAuditRuntimeWriter));
        AssertAuditQueueComposition(hosted, audit, nameof(BillingAuditService));
#endif
    }

    private static void AssertAuditQueueComposition(IHostedService[] hosted, object audit, string serviceName)
    {
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var queueField = typeof(BatchAuditQueueHostedService).GetField("_innerService", fields)!;
        var queue = Assert.Single(hosted.OfType<BatchAuditQueueHostedService>(),
            adapter => ReferenceEquals(queueField.GetValue(adapter), audit));
        Assert.False(typeof(IDisposable).IsAssignableFrom(queue.GetType()));
        var wrapper = Assert.Single(hosted.OfType<LeaderElectedServiceWrapper>(),
            service => service.ServiceName == serviceName);
        var innerField = typeof(LeaderElectedServiceWrapper).GetField("_innerService", fields)!;
        var retention = Assert.IsType<BatchAuditRetentionHostedService>(innerField.GetValue(wrapper));
        var runnerField = Assert.Single(typeof(BatchAuditRetentionHostedService).GetFields(fields),
            field => field.FieldType == typeof(Func<CancellationToken, Task>));
        Assert.Same(audit, ((Delegate)runnerField.GetValue(retention)!).Target);
        Assert.True((bool)typeof(LeaderElectedServiceWrapper).GetField("_ownsInnerService", fields)!.GetValue(wrapper)!);
        // Reverse hosted shutdown joins retention before draining this process's queue.
        Assert.True(Array.IndexOf(hosted, queue) < Array.IndexOf(hosted, wrapper));
        Assert.DoesNotContain(hosted, service => ReferenceEquals(service, audit));
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
        if (descriptors.Any(descriptor => descriptor.ServiceType == typeof(IDatabaseConnectionFactory)))
        {
            var factoryRegistration = Assert.Single(descriptors,
                descriptor => descriptor.ServiceType == typeof(IDatabaseConnectionFactory));
            Assert.Equal(ServiceLifetime.Singleton, factoryRegistration.Lifetime);
            var factory = Assert.IsType<DatabaseConnectionFactory>(provider.GetRequiredService<IDatabaseConnectionFactory>());
            Assert.Same(factory, scope.ServiceProvider.GetRequiredService<IDatabaseConnectionFactory>());
            Assert.Same(dataSource, typeof(DatabaseConnectionFactory).GetField("_dataSource",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(factory));
            using var rawConnection = factory.CreateConnection();
            Assert.IsType<NpgsqlConnection>(rawConnection);
            Assert.Equal(System.Data.ConnectionState.Closed, rawConnection.State);
            var poolSettings = new NpgsqlConnectionStringBuilder(rawConnection.ConnectionString);
            var (_, adminConnectionString) = new ConnectionStringManager().GetProviderAndConnectionString("AdminAPI");
            var adminSettings = new NpgsqlConnectionStringBuilder(adminConnectionString);
            Assert.Equal(adminSettings.MinPoolSize, poolSettings.MinPoolSize);
            Assert.Equal(adminSettings.MaxPoolSize, poolSettings.MaxPoolSize);

            var healthRegistration = Assert.Single(provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
                .Value.Registrations, value => value.Name == "database_health");
            var health = Assert.IsType<DatabaseHealthCheck>(healthRegistration.Factory(provider));
            Assert.Same(factory, typeof(DatabaseHealthCheck).GetField("_connectionFactory",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(health));
        }
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
