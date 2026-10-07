using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.OpenApi;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Authentication;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.EventHandlers;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using AdminConfigurationEndpoints = ConduitLLM.Admin.Endpoints.ConfigurationEndpoints;

namespace ConduitLLM.Tests.Architecture;

/// <summary>
/// Guards the optional dependency seams used by trimmed and Native AOT hosts.
/// These assertions intentionally inspect emitted assembly references: a package
/// may exist in the repository without becoming part of a service's runtime graph.
/// </summary>
public sealed class AotDependencyBoundaryTests
{
    [Fact]
    public void AdminEfQueryTrimExceptionsRemainMethodScopedAndAllowlisted()
    {
        var expected = new[]
        {
            "ConduitLLM.Admin.Endpoints.ConfigurationEndpoints.GetProviderEndpoints",
            "ConduitLLM.Admin.Endpoints.ConfigurationEndpoints.GetRoutingConfig",
            "ConduitLLM.Admin.Endpoints.ConfigurationEndpoints.GetRoutingStatistics",
            "ConduitLLM.Admin.Endpoints.HealthMonitoringEndpoints.QueryErrorSpikes",
            "ConduitLLM.Admin.Endpoints.HealthMonitoringEndpoints.QueryHealthIntervals",
            "ConduitLLM.Admin.Endpoints.MediaEndpoints.QueryPruneCandidatesAsync",
            "ConduitLLM.Admin.Endpoints.MediaRetentionEndpoints.GetPolicies",
            "ConduitLLM.Admin.Endpoints.PromptCachingEndpoints.GetAnalytics",
            "ConduitLLM.Admin.Endpoints.VirtualKeyGroupsEndpoints.GetTransactionHistory",
            "ConduitLLM.Admin.Endpoints.VirtualKeyGroupsEndpoints.InvalidateGroupKeyCachesAsync",
            "ConduitLLM.Admin.Services.AdminVirtualKeyService.PerformMaintenanceAsync",
            "ConduitLLM.Admin.Services.MediaCleanupService.ProcessPagedMediaAsync",
            "ConduitLLM.Admin.Services.MediaCleanupService.ProcessPurgeAsync",
            "ConduitLLM.Admin.Services.MediaCleanupService.ProcessQuotaMediaAsync",
            "ConduitLLM.Admin.Services.MediaCleanupStatusService.GetStatusAsync",
            "ConduitLLM.Configuration.Repositories.MediaRecordRepository.GetAggregateStorageStatsAsync",
            "ConduitLLM.Configuration.Repositories.MediaRecordRepository.GetStorageStatsByMediaTypeAsync",
            "ConduitLLM.Configuration.Repositories.MediaRecordRepository.GetStorageStatsByProviderAsync",
            "ConduitLLM.Configuration.Repositories.RequestLogRepository.GetAggregatedByModelAsync",
            "ConduitLLM.Configuration.Repositories.RequestLogRepository.GetAggregatedByModelForVirtualKeyAsync",
            "ConduitLLM.Configuration.Repositories.RequestLogRepository.GetAggregatedByVirtualKeyAsync",
            "ConduitLLM.Configuration.Repositories.RequestLogRepository.GetCostsByDateAsync",
            "ConduitLLM.Configuration.Repositories.RequestLogRepository.GetDailyStatisticsAsync",
            "ConduitLLM.Configuration.Repositories.RequestLogRepository.GetSummaryAsync",
            "ConduitLLM.Configuration.Repositories.RequestLogRepository.GetSummaryForVirtualKeyAsync"
        };
        var assemblies = new[]
        {
            typeof(AdminConfigurationEndpoints).Assembly,
            typeof(MediaRecordRepository).Assembly
        }
            .Distinct()
            .ToArray();
        var types = assemblies.SelectMany(assembly => assembly.GetTypes()).ToArray();
        var actual = types
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly))
            .Where(method => method
                .GetCustomAttributes<UnconditionalSuppressMessageAttribute>()
                .Any(attribute =>
                    attribute.Category == "Trimming" &&
                    attribute.CheckId == "IL2026" &&
                    attribute.Justification?.StartsWith(
                        "Admin EF queries are outside the supported NativeAOT data-plane contract",
                        StringComparison.Ordinal) == true))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), actual);
        Assert.DoesNotContain(
            assemblies.SelectMany(assembly =>
                assembly.GetCustomAttributes<UnconditionalSuppressMessageAttribute>()),
            IsAdminEfQueryException);
        Assert.DoesNotContain(
            types.SelectMany(type =>
                type.GetCustomAttributes<UnconditionalSuppressMessageAttribute>()),
            IsAdminEfQueryException);

        static bool IsAdminEfQueryException(UnconditionalSuppressMessageAttribute attribute) =>
            attribute.Category == "Trimming" &&
            attribute.CheckId == "IL2026" &&
            attribute.Justification?.StartsWith(
                "Admin EF queries are outside the supported NativeAOT data-plane contract",
                StringComparison.Ordinal) == true;
    }

    [Fact]
    public void ContractsRemainTransportAndPersistenceNeutral()
    {
        AssertDoesNotReference(
            typeof(IEventBus).Assembly,
            "ConduitLLM.Functions",
            "Microsoft.EntityFrameworkCore",
            "Wolverine",
            "Microsoft.AspNetCore.SignalR",
            "AWSSDK",
            "Amazon.",
            "Microsoft.OpenApi");
    }

    [Fact]
    public void CoreDoesNotOwnOptionalRuntimeAdapters()
    {
        AssertDoesNotReference(
            typeof(MediaLifecycleService).Assembly,
            "Wolverine",
            "Microsoft.AspNetCore.SignalR",
            "Microsoft.AspNetCore.SignalR.Protocols.MessagePack",
            "MessagePack",
            "AWSSDK",
            "Amazon.",
            "Microsoft.OpenApi",
            "Microsoft.ML.Tokenizers");
    }

    [Fact]
    public void PersistenceDoesNotOwnMessagingOrBuildTooling()
    {
        AssertDoesNotReference(
            typeof(ConduitLLM.Configuration.ConduitDbContext).Assembly,
            "Wolverine",
            "RabbitMQ.Client",
            "Microsoft.Build");
    }

    [Fact]
    public void PersistenceAbstractionsRemainBackendNeutral()
    {
        AssertDoesNotReference(
            typeof(IGlobalSettingRepository).Assembly,
            "ConduitLLM.Configuration",
            "ConduitLLM.Functions",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "Wolverine",
            "Microsoft.AspNetCore.SignalR");
    }

    [Fact]
    public void TypedNpgsqlPersistenceDoesNotRootEfOrServiceImplementations()
    {
        AssertDoesNotReference(
            typeof(NpgsqlGlobalSettingRepository).Assembly,
            "ConduitLLM.Configuration",
            "ConduitLLM.Functions",
            "ConduitLLM.Admin",
            "ConduitLLM.Gateway",
            "Microsoft.EntityFrameworkCore",
            "Wolverine");
    }

#if !CONDUIT_NATIVE_AOT
    [Fact]
    public void JitGatewayDoesNotRootTypedNpgsqlRuntimeAdapter()
    {
        AssertDoesNotReference(
            Assembly.Load("ConduitLLM.Gateway"),
            "ConduitLLM.Persistence.Npgsql");
    }

    [Fact]
    public void JitRepositoryGraphRegistersTheEfVirtualKeyRuntimeStore()
    {
        var services = new ServiceCollection();

        services.AddRepositories();

        var descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(IVirtualKeyRuntimeStore));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(EfVirtualKeyRuntimeStore), descriptor.ImplementationType);

        var asyncTaskDescriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(IAsyncTaskRuntimeStore));
        Assert.Equal(ServiceLifetime.Scoped, asyncTaskDescriptor.Lifetime);
        Assert.Equal(typeof(EfAsyncTaskRuntimeStore), asyncTaskDescriptor.ImplementationType);

        var mediaDescriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(IMediaRuntimeStore));
        Assert.Equal(ServiceLifetime.Scoped, mediaDescriptor.Lifetime);
        Assert.Equal(typeof(EfMediaRuntimeStore), mediaDescriptor.ImplementationType);

        var metricsDescriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(IGatewayMetricsStore));
        Assert.Equal(ServiceLifetime.Scoped, metricsDescriptor.Lifetime);
        Assert.Equal(typeof(EfGatewayMetricsStore), metricsDescriptor.ImplementationType);
    }
#endif

#if CONDUIT_NATIVE_AOT
    [Fact]
    public void NativeNonGatewayRepositoryGraphRetainsEfRuntimeStores()
    {
        var services = new ServiceCollection();

        services.AddRepositories();

        var descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(IAsyncTaskRuntimeStore));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(EfAsyncTaskRuntimeStore), descriptor.ImplementationType);

        var mediaDescriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(IMediaRuntimeStore));
        Assert.Equal(ServiceLifetime.Scoped, mediaDescriptor.Lifetime);
        Assert.Equal(typeof(EfMediaRuntimeStore), mediaDescriptor.ImplementationType);
    }

    [Fact]
    public void NativeGatewayReplacesEveryExtractedRuntimeRepository()
    {
        var services = new ServiceCollection();
        services.AddScoped<IGlobalSettingRepository>(_ => null!);
        services.AddScoped<IIpFilterRepository>(_ => null!);
        services.AddScoped<IProviderRepository>(_ => null!);
        services.AddScoped<IProviderKeyCredentialRepository>(_ => null!);
        services.AddScoped<IVirtualKeyRuntimeStore>(_ => null!);
        services.AddScoped<IGatewayMetricsStore>(_ => null!);
        services.AddScoped<IAsyncTaskRuntimeStore>(_ => null!);
        services.AddScoped<IMediaRuntimeStore>(_ => null!);
        services.AddScoped<IRequestLogRuntimeStore>(_ => null!);
        services.AddScoped<IRequestLogRuntimeWriter>(_ => null!);
        services.AddScoped<IModelProviderMappingRuntimeStore>(_ => null!);
        services.AddScoped<IModelProviderMappingRepository>(_ => null!);

        services.AddBillingAndPricingServices();
        services.UseNativeRuntimePersistence();

        AssertNativeSingleton<IGlobalSettingRepository, NpgsqlGlobalSettingRepository>(services);
        AssertNativeSingleton<IIpFilterRepository, NpgsqlIpFilterRepository>(services);
        AssertNativeSingleton<IProviderRepository, NpgsqlProviderRepository>(services);
        AssertNativeSingleton<IProviderKeyCredentialRepository, NpgsqlProviderKeyCredentialRepository>(services);
        AssertNativeSingleton<IVirtualKeyRuntimeStore, NpgsqlVirtualKeyRuntimeStore>(services);
        AssertNativeSingleton<IGatewayMetricsStore, NpgsqlGatewayMetricsStore>(services);
        AssertNativeSingleton<IAsyncTaskRuntimeStore, NpgsqlAsyncTaskRuntimeStore>(services);
        AssertNativeSingleton<IMediaRuntimeStore, NpgsqlMediaRuntimeStore>(services);
        AssertNativeSingleton<IRequestLogRuntimeStore, NpgsqlRequestLogRuntimeStore>(services);
        AssertNativeSingleton<IRequestLogRuntimeWriter, StoreBackedRequestLogRuntimeWriter>(services);
        AssertNativeSingleton<IModelProviderMappingRuntimeStore, NpgsqlModelProviderMappingRuntimeStore>(services);
        AssertNativeSingleton<IModelProviderMappingRepository, StoreBackedModelProviderMappingRepository>(services);
        var modelCost = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(IModelCostService));
        Assert.Equal(ServiceLifetime.Scoped, modelCost.Lifetime);
        Assert.Equal(typeof(StoreBackedModelCostService), modelCost.ImplementationType);
    }
#endif

    [Fact]
    public void AdminDoesNotDirectlyOwnGatewayOnlyAdapters()
    {
        AssertDoesNotReference(
            typeof(ConduitLLM.Admin.Services.MediaCleanupStatusService).Assembly,
            "ConduitLLM.SignalR",
            "ConduitLLM.Tokenization",
            "Microsoft.AspNetCore.SignalR",
            "MessagePack",
            "Microsoft.ML.Tokenizers",
            "AWSSDK",
            "Amazon.");
    }

    [Fact]
    public void OptionalFeaturesAreOwnedByDedicatedAssemblies()
    {
        Assert.Equal("ConduitLLM.Messaging.Wolverine", typeof(WolverineEventBus).Assembly.GetName().Name);
        Assert.Equal("ConduitLLM.SignalR", typeof(SignalRConfigurationExtensions).Assembly.GetName().Name);
        Assert.Equal("ConduitLLM.OpenApi", typeof(OperationMetadataTransformer).Assembly.GetName().Name);
        Assert.Equal("ConduitLLM.Tokenization", typeof(TiktokenCounter).Assembly.GetName().Name);
        Assert.Equal("ConduitLLM.Media", typeof(S3MediaStorageService).Assembly.GetName().Name);
    }

    [Fact]
    public void WebServicesCannotReferenceSchemaMutationExecutable()
    {
        AssertDoesNotReference(typeof(ConduitLLM.Admin.Program).Assembly, "ConduitLLM.Migrator");
        AssertDoesNotReference(Assembly.Load("ConduitLLM.Gateway"), "ConduitLLM.Migrator");
        Assert.Equal("ConduitLLM.Migrator", typeof(ConduitLLM.Configuration.Data.SimpleMigrationService).Assembly.GetName().Name);
    }

    [Fact]
    public void PersistenceAbstractionsDoNotExposeQueryable()
    {
        var repositoryInterfaces = new[]
            {
                typeof(IProviderRepository).Assembly,
                typeof(IGlobalSettingRepository).Assembly
            }
            .Distinct()
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.IsInterface &&
                (type.Name.EndsWith("Repository", StringComparison.Ordinal) ||
                 type.Name.EndsWith("Store", StringComparison.Ordinal)));

        foreach (var repositoryInterface in repositoryInterfaces)
        {
            foreach (var method in repositoryInterface.GetMethods())
            {
                Assert.False(ContainsQueryable(method.ReturnType), $"{repositoryInterface.Name}.{method.Name} returns IQueryable");
                Assert.DoesNotContain(method.GetParameters(), parameter => ContainsQueryable(parameter.ParameterType));
            }
        }
    }

    [Fact]
    public void GatewayRequestPathUsesRuntimeVirtualKeyContract()
    {
        Assert.True(typeof(IVirtualKeyRuntimeService).IsAssignableFrom(typeof(IVirtualKeyService)));

        var runtimeMethods = typeof(IVirtualKeyRuntimeService).GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();
        Assert.Equal(
            [
                nameof(IVirtualKeyRuntimeService.GetVirtualKeyInfoForValidationAsync),
                nameof(IVirtualKeyRuntimeService.UpdateSpendAsync),
                nameof(IVirtualKeyRuntimeService.ValidateVirtualKeyAsync),
                nameof(IVirtualKeyRuntimeService.ValidateVirtualKeyForAuthenticationAsync)
            ],
            runtimeMethods);

        var requestPathTypes = new[]
        {
            typeof(VirtualKeyAuthenticationHandler),
            typeof(VirtualKeySignalRAuthenticationHandler),
            typeof(VirtualKeyHubFilter),
            typeof(RequireBalanceEndpointFilter),
            typeof(DiscoveryEndpoints),
            typeof(UsageTrackingMiddleware),
            typeof(SpendUpdatedHandler)
        };

        foreach (var requestPathType in requestPathTypes)
        {
            var parameters = requestPathType.GetConstructors()
                .Cast<MethodBase>()
                .Concat(requestPathType.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly))
                .SelectMany(method => method.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .ToArray();

            Assert.Contains(typeof(IVirtualKeyRuntimeService), parameters);
            Assert.DoesNotContain(typeof(IVirtualKeyService), parameters);
        }
    }

    [Fact]
    public void GatewayAccountingPathsUseFixedShapeRuntimeContracts()
    {
        Assert.Contains(
            typeof(IVirtualKeyRuntimeStore),
            typeof(BatchSpendUpdateService).GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType));

        var requestPathTypes = new[]
        {
            typeof(UsageTrackingMiddleware)
        };
        foreach (var requestPathType in requestPathTypes)
        {
            var parameterTypes = requestPathType.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .ToArray();
            Assert.Contains(typeof(IRequestLogRuntimeWriter), parameterTypes);
            Assert.DoesNotContain(typeof(IRequestLogService), parameterTypes);
        }
    }

    [Fact]
    public void StoreBackedVirtualKeyRuntimeServiceUsesOnlyFixedShapePersistence()
    {
        var constructorParameters = typeof(StoreBackedVirtualKeyRuntimeService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IVirtualKeyRuntimeStore), constructorParameters);
        Assert.DoesNotContain(typeof(IVirtualKeyRepository), constructorParameters);
        Assert.DoesNotContain(typeof(IVirtualKeyGroupRepository), constructorParameters);
        Assert.DoesNotContain(typeof(IVirtualKeySpendHistoryRepository), constructorParameters);
    }

    [Fact]
    public void AsyncTaskRuntimeServiceUsesOnlyFixedShapePersistence()
    {
        var constructorParameters = typeof(HybridAsyncTaskService)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IAsyncTaskRuntimeStore), constructorParameters);
        Assert.DoesNotContain(typeof(IAsyncTaskRepository), constructorParameters);
    }

    [Fact]
    public void GatewayMediaPathsUseOnlyFixedShapePersistence()
    {
        var requestPathTypes = new[]
        {
            typeof(MediaLifecycleService),
            typeof(MediaQuotaService),
            typeof(MediaEndpoints),
            typeof(DownloadsEndpoints)
        };

        foreach (var requestPathType in requestPathTypes)
        {
            var constructorParameters = requestPathType.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .ToArray();
            Assert.Contains(typeof(IMediaRuntimeStore), constructorParameters);
            Assert.DoesNotContain(typeof(IMediaRecordRepository), constructorParameters);
            Assert.DoesNotContain(typeof(IConfigurationDbContext), constructorParameters);
        }
    }

    [Fact]
    public void StoreBackedModelRoutingAdapterUsesOnlyFixedShapePersistence()
    {
        var constructorParameters = typeof(StoreBackedModelProviderMappingRepository)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal([typeof(IModelProviderMappingRuntimeStore)], constructorParameters);
    }

    [Fact]
    public void CompiledSchemaVersionMatchesLatestMigration()
    {
        var options = new DbContextOptionsBuilder<ConduitLLM.Configuration.ConduitDbContext>()
            .UseNpgsql("Host=localhost;Database=schema_inventory;Username=unused;Password=unused")
            .Options;
        using var context = new ConduitLLM.Configuration.ConduitDbContext(options);

        Assert.Equal(ConduitLLM.Configuration.Data.ConduitSchemaVersion.Current, context.Database.GetMigrations().Last());
    }

    [Fact]
    public void ProductionAssembliesDoNotUseReflectionBasedMaxLengthValidation()
    {
        var assemblyNames = new[]
        {
            "ConduitLLM.Admin",
            "ConduitLLM.Configuration",
            "ConduitLLM.Core",
            "ConduitLLM.Functions",
            "ConduitLLM.Gateway",
            "ConduitLLM.Providers",
            "ConduitLLM.Security"
        };
        var offenders = assemblyNames
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMembers(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(member => member.CustomAttributes.Any(attribute =>
                attribute.AttributeType == typeof(MaxLengthAttribute)))
            .Select(member => $"{member.DeclaringType?.FullName}.{member.Name}")
            .OrderBy(name => name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void StringLengthValidationPreservesEfMaximumLengthMetadata()
    {
        var options = new DbContextOptionsBuilder<ConduitLLM.Configuration.ConduitDbContext>()
            .UseNpgsql("Host=localhost;Database=schema_inventory;Username=unused;Password=unused")
            .Options;
        using var context = new ConduitLLM.Configuration.ConduitDbContext(options);

        var keyName = context.Model.FindEntityType(typeof(VirtualKey))!
            .FindProperty(nameof(VirtualKey.KeyName));

        Assert.NotNull(keyName);
        Assert.Equal(100, keyName.GetMaxLength());
    }

    private static bool ContainsQueryable(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>))
        {
            return true;
        }

        return type.HasElementType && ContainsQueryable(type.GetElementType()!)
            || type.IsGenericType && type.GetGenericArguments().Any(ContainsQueryable);
    }

#if CONDUIT_NATIVE_AOT
    private static void AssertNativeSingleton<TService, TImplementation>(IServiceCollection services)
    {
        var descriptor = Assert.Single(services, value => value.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(TImplementation), descriptor.ImplementationType);
    }
#endif

    private static void AssertDoesNotReference(Assembly assembly, params string[] forbiddenPrefixes)
    {
        var references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        foreach (var forbiddenPrefix in forbiddenPrefixes)
        {
            Assert.DoesNotContain(
                references,
                reference => reference.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase));
        }
    }
}
