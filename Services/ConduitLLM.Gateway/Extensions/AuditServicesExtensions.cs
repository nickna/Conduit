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
    /// Hosts each local audit queue and coordinates only their shared retention work.
    /// </summary>
    public static IServiceCollection AddAuditServices(this IServiceCollection services)
    {
#if !CONDUIT_NATIVE_AOT
        // Request Log Service - uses batch processing like other audit services
        services.AddSingleton<IRequestLogService, RequestLogService>();
        services.AddSingleton<IRequestLogRuntimeWriter>(provider =>
            provider.GetRequiredService<IRequestLogService>());
        services.AddSingleton<IHostedService>(provider => new BatchAuditQueueHostedService(
            (RequestLogService)provider.GetRequiredService<IRequestLogService>()));
        services.AddLeaderElectedHostedService<BatchAuditRetentionHostedService>(
            provider => new BatchAuditRetentionHostedService(
                ((RequestLogService)provider.GetRequiredService<IRequestLogService>()).RunDataRetentionAsync,
                provider.GetRequiredService<ILogger<BatchAuditRetentionHostedService>>()),
            "RequestLogService", ownsInnerService: true);

        // Each process owns its queued events; only shared retention needs leadership.
        services.AddSingleton<IFunctionCallAuditService, FunctionCallAuditService>();
        services.AddSingleton<IHostedService>(provider => new BatchAuditQueueHostedService(
            (FunctionCallAuditService)provider.GetRequiredService<IFunctionCallAuditService>()));
        services.AddLeaderElectedHostedService<BatchAuditRetentionHostedService>(
            provider => new BatchAuditRetentionHostedService(
                ((FunctionCallAuditService)provider.GetRequiredService<IFunctionCallAuditService>()).RunDataRetentionAsync,
                provider.GetRequiredService<ILogger<BatchAuditRetentionHostedService>>()),
            "FunctionCallAuditService", ownsInnerService: true);
#endif

        return services;
    }
}
