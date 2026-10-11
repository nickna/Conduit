using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Services;
#if CONDUIT_NATIVE_AOT
using ConduitLLM.Persistence.Interfaces;
#endif

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
        services.AddScoped<IFunctionExecutionService, FunctionExecutionService>();
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
