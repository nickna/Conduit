using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Services;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

namespace ConduitLLM.Gateway.Extensions;

/// <summary>
/// Extension methods for registering function services
/// </summary>
public static class FunctionServicesExtensions
{
    /// <summary>
    /// Adds function services including repositories, execution, cost calculation, and agentic orchestration
    /// </summary>
    public static IServiceCollection AddFunctionServices(this IServiceCollection services)
    {
        // The same fixed-shape durable claim and ledger adapter serves both runtimes.
        services.AddSingleton<IFunctionExecutionIdempotencyStore>(provider =>
            new NpgsqlFunctionExecutionIdempotencyStore(
                provider.GetRequiredService<NpgsqlDataSource>(),
                new NpgsqlVirtualKeyRuntimeStore(provider.GetRequiredService<NpgsqlDataSource>())));
        // Register Function repositories
        services.AddScoped<IFunctionConfigurationRepository, FunctionConfigurationRepository>();

        // Register Function services
#if CONDUIT_NATIVE_AOT
        services.AddScoped<IFunctionCostService, StoreBackedFunctionCostService>();
#else
        services.AddScoped<IFunctionCostService, FunctionCostService>();
#endif
        services.AddScoped<IFunctionCostCalculationService, FunctionCostCalculationService>();
        services.AddSingleton<IFunctionCredentialProtector, FunctionCredentialProtector>();
#if CONDUIT_NATIVE_AOT
        // Explicit construction preserves the legacy constructor without permitting
        // DI to choose its longer repository-backed signature for native requests.
        services.AddScoped<IFunctionClientFactory>(provider => new FunctionClientFactory(
            provider.GetRequiredService<IFunctionExecutionConfigurationStore>(),
            provider.GetRequiredService<IFunctionCredentialProtector>(),
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetService<IHttpClientFactory>()));
#else
        services.AddScoped<IFunctionClientFactory, FunctionClientFactory>();
#endif
#if CONDUIT_NATIVE_AOT
        services.AddScoped<IFunctionExecutionService>(provider =>
            FunctionExecutionService.CreateWithRuntimePersistence(
                provider.GetRequiredService<IFunctionExecutionConfigurationStore>(),
                provider.GetRequiredService<IFunctionExecutionRuntimeWriter>(),
                provider.GetRequiredService<IFunctionExecutionRuntimeReader>(),
                provider.GetRequiredService<IFunctionCostCalculationService>(),
                provider.GetRequiredService<IFunctionClientFactory>(),
                provider.GetRequiredService<IFunctionCredentialProtector>(),
                provider.GetRequiredService<ILogger<FunctionExecutionService>>(),
                provider.GetRequiredService<IFunctionExecutionIdempotencyStore>()));
#else
        services.AddScoped<IFunctionExecutionService>(provider =>
            FunctionExecutionService.CreateWithIdempotency(
                provider.GetRequiredService<IFunctionConfigurationRepository>(),
                provider.GetRequiredService<IFunctionCredentialRepository>(),
                provider.GetRequiredService<IFunctionExecutionRepository>(),
                provider.GetRequiredService<IFunctionCostCalculationService>(),
                provider.GetRequiredService<IFunctionClientFactory>(),
                provider.GetRequiredService<IFunctionCredentialProtector>(),
                provider.GetRequiredService<ILogger<FunctionExecutionService>>(),
                provider.GetRequiredService<IFunctionExecutionIdempotencyStore>()));
#endif
        services.AddScoped<FunctionParameterValidationService>();

        // Register Agentic Function Calling services
#if CONDUIT_NATIVE_AOT
        services.AddScoped<IFunctionDiscoveryService>(provider =>
            FunctionDiscoveryService.CreateWithRuntimePersistence(
                provider.GetRequiredService<IFunctionExecutionConfigurationStore>(),
                provider.GetRequiredService<IFunctionClientFactory>(),
                provider.GetService<IFunctionDiscoveryCacheService>(),
                provider.GetRequiredService<ILogger<FunctionDiscoveryService>>()));
#else
        services.AddScoped<IFunctionDiscoveryService, FunctionDiscoveryService>();
#endif
        services.AddScoped<IAgenticOrchestrationService, AgenticOrchestrationService>();

        return services;
    }
}
