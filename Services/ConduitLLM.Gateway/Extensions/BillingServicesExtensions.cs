using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.UsageTracking;
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

        // Billing audit service for comprehensive billing event tracking - with leader election
        services.AddSingleton<IBillingAuditService, BillingAuditService>();
        services.AddLeaderElectedHostedService<BillingAuditService>(
            provider => (BillingAuditService)provider.GetRequiredService<IBillingAuditService>(),
            "BillingAuditService");

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
        services.AddSingleton<BillingReconciliationService>();
        services.AddLeaderElectedHostedService<BillingReconciliationService>(
            provider => provider.GetRequiredService<BillingReconciliationService>(),
            "BillingReconciliationService");

        // Pricing rules engine services for flexible rules-based pricing
        services.AddScoped<IPricingRulesEvaluator, PricingRulesEvaluator>();
        services.AddScoped<IPricingRulesValidator, PricingRulesValidator>();

        // Cached pricing rules service for parsed configuration caching (uses the shared application FusionCache)
        services.AddPricingRulesCache();

        // Pricing audit service for rules-based pricing evaluation tracking - with leader election
        services.AddSingleton<IPricingAuditService, PricingAuditService>();
        services.AddLeaderElectedHostedService<PricingAuditService>(
            provider => (PricingAuditService)provider.GetRequiredService<IPricingAuditService>(),
            "PricingAuditService");

        return services;
    }
}
