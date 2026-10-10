using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Functions.Interfaces;

namespace ConduitLLM.Gateway.Extensions;

/// <summary>
/// Extension methods for registering audit services
/// </summary>
public static class AuditServicesExtensions
{
    /// <summary>
    /// Adds audit services including request log and function call audit services with leader election
    /// </summary>
    public static IServiceCollection AddAuditServices(this IServiceCollection services)
    {
        // Request Log Service - uses batch processing like other audit services
        services.AddSingleton<IRequestLogService, RequestLogService>();
        services.AddSingleton<IRequestLogRuntimeWriter>(provider =>
            provider.GetRequiredService<IRequestLogService>());
        services.AddLeaderElectedHostedService<RequestLogService>(
            provider => (RequestLogService)provider.GetRequiredService<IRequestLogService>(),
            "RequestLogService");

        // Register Function Call Audit service with leader election
        services.AddSingleton<IFunctionCallAuditService, FunctionCallAuditService>();
        services.AddLeaderElectedHostedService<FunctionCallAuditService>(
            provider => (FunctionCallAuditService)provider.GetRequiredService<IFunctionCallAuditService>(),
            "FunctionCallAuditService");

        return services;
    }
}
