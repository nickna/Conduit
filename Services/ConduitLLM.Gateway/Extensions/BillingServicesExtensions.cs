using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Gateway.Extensions;

/// <summary>
/// Extension methods for registering billing and pricing services
/// </summary>
public static class BillingServicesExtensions
{
    /// <summary>
    /// Adds billing and pricing services including cost calculation, billing audit, and pricing rules engine
    /// </summary>
    public static IServiceCollection AddBillingAndPricingServices(this IServiceCollection services)
    {
        // Native request billing uses the fixed-shape routing persistence contract.
        // JIT retains the full EF management service and FusionCache composition.
#if CONDUIT_NATIVE_AOT
        services.AddScoped<IModelCostService, StoreBackedModelCostService>();
#else
        services.AddModelCostCache();
#endif

        // Cost calculation service
        services.AddScoped<ICostCalculationService, CostCalculationService>();
        services.AddScoped<IChatSpendEstimator, ChatSpendEstimator>();
        services.AddScoped<ISpendReservationService, SpendReservationService>();
        services.AddScoped<IRequestAccountingContext, RequestAccountingContext>();

        // Tool cost calculation service for provider tool billing
        // Singleton: uses IDbContextFactory for database access and optional IProviderToolCache
        services.AddSingleton<IToolCostCalculationService>(sp =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<ConduitDbContext>>();
            var logger = sp.GetRequiredService<ILogger<ToolCostCalculationService>>();
            var cache = sp.GetService<IProviderToolCache>(); // Optional
            return new ToolCostCalculationService(contextFactory, logger, cache);
        });

        // Ephemeral key service for direct browser-to-API authentication (used for all direct access including SignalR)
        services.AddScoped<IEphemeralKeyService, EphemeralKeyService>();

        // Each host flushes its own in-memory billing audit queue.
#if CONDUIT_NATIVE_AOT
        services.AddSingleton<IBillingAuditRuntimeWriter, NpgsqlBillingAuditRuntimeWriter>();
        services.AddSingleton<IBillingAuditService>(provider => BillingAuditService.CreateWithRuntimePersistence(
            provider,
            provider.GetRequiredService<IBillingAuditRuntimeWriter>(),
            provider.GetRequiredService<ILogger<BillingAuditService>>()));
        services.AddSingleton<IHostedService>(provider => (BillingAuditService)provider.GetRequiredService<IBillingAuditService>());
#else
        services.AddSingleton<IBillingAuditService, BillingAuditService>();
        services.AddSingleton<IHostedService>(provider => new BatchAuditQueueHostedService(
            (BillingAuditService)provider.GetRequiredService<IBillingAuditService>()));
        services.AddLeaderElectedHostedService<BatchAuditRetentionHostedService>(
            provider => new BatchAuditRetentionHostedService(
                ((BillingAuditService)provider.GetRequiredService<IBillingAuditService>()).RunDataRetentionAsync,
                provider.GetRequiredService<ILogger<BatchAuditRetentionHostedService>>()),
            "BillingAuditService", ownsInnerService: true);
#endif

        services.AddOptions<ConduitLLM.Configuration.Options.BillingReconciliationOptions>()
            .BindConfiguration(ConduitLLM.Configuration.Options.BillingReconciliationOptions.SectionName)
            .Validate(options =>
                    options.WindowHours is >= 1 and <= 24 &&
                    options.GracePeriodMinutes is >= 0 and <= 360 &&
                    options.RelativeThreshold is >= 0m and <= 1m &&
                    options.AbsoluteThresholdUsd is >= 0m and <= 1_000_000m &&
                    options.MaxCatchUpWindowsPerRun is >= 1 and <= 168,
                "BillingReconciliation settings are outside their supported ranges.")
            .ValidateOnStart();
#if !CONDUIT_NATIVE_AOT
        services.AddSingleton<BillingReconciliationService>();
        services.AddLeaderElectedHostedService<BillingReconciliationService>(
            provider => provider.GetRequiredService<BillingReconciliationService>(),
            "BillingReconciliationService", ownsInnerService: false);
#endif

        // Pricing rules engine services for flexible rules-based pricing
        services.AddScoped<IPricingRulesEvaluator, PricingRulesEvaluator>();
        services.AddScoped<IPricingRulesValidator, PricingRulesValidator>();

        // Cached pricing rules service for parsed configuration caching (uses the shared application FusionCache)
        services.AddPricingRulesCache();

#if !CONDUIT_NATIVE_AOT
        // Local pricing audit flushing is independent of shared retention leadership.
        services.AddSingleton<IPricingAuditService, PricingAuditService>();
        services.AddSingleton<IHostedService>(provider => new BatchAuditQueueHostedService(
            (PricingAuditService)provider.GetRequiredService<IPricingAuditService>()));
        services.AddLeaderElectedHostedService<BatchAuditRetentionHostedService>(
            provider => new BatchAuditRetentionHostedService(
                ((PricingAuditService)provider.GetRequiredService<IPricingAuditService>()).RunDataRetentionAsync,
                provider.GetRequiredService<ILogger<BatchAuditRetentionHostedService>>()),
            "PricingAuditService", ownsInnerService: true);
#endif

        return services;
    }
}
