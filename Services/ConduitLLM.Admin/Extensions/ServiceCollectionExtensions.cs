using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Security;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Security.Authorization;
using ConduitLLM.Security.Options;

using Microsoft.AspNetCore.Authorization;

namespace ConduitLLM.Admin.Extensions;

/// <summary>
/// Extension methods for configuring Admin API services in the dependency injection container
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Admin API services to the dependency injection container
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The application configuration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddAdminServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Configure security options from environment variables
        services.ConfigureAdminSecurityOptions(configuration);

        // Register security service as singleton for both shared and admin-specific interfaces
        services.AddSingleton<Admin.Services.SecurityService>();
        services.AddSingleton<ConduitLLM.Security.Interfaces.ISecurityService>(sp => sp.GetRequiredService<Admin.Services.SecurityService>());
        services.AddSingleton<IAdminSecurityService>(sp => sp.GetRequiredService<Admin.Services.SecurityService>());

        // Add memory cache if not already registered
        services.AddMemoryCache();

        // Register Ephemeral Master Key Service
        services.AddSingleton<IEphemeralMasterKeyService, EphemeralMasterKeyService>();

        // Add authentication with a custom scheme
        services.AddAuthentication("MasterKey")
            .AddScheme<MasterKeyAuthenticationSchemeOptions, MasterKeyAuthenticationHandler>("MasterKey", null);

        // Register authorization policy for master key
        services.AddSingleton<IAuthorizationHandler, MasterKeyAuthorizationHandler>();

        // Register health key authorization handler (shared from ConduitLLM.Security)
        services.AddSingleton<IAuthorizationHandler, HealthKeyAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            // Define the MasterKeyPolicy
            options.AddPolicy("MasterKeyPolicy", policy =>
                policy.Requirements.Add(new MasterKeyRequirement()));

            // Set MasterKeyPolicy as the default policy for all controllers
            // This ensures any controller with [Authorize] will use MasterKeyPolicy by default
            options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .AddRequirements(new MasterKeyRequirement())
                .Build();

            // Add policy for health endpoint access - allows private network OR valid health key
            options.AddPolicy("HealthMonitoring", policy =>
            {
                policy.Requirements.Add(new HealthKeyRequirement());
            });
        });

        // Register AdminVirtualKeyService (optional deps use default parameter values)
        services.AddScoped<IAdminVirtualKeyService, AdminVirtualKeyService>();
        // Register AdminModelProviderMappingService (optional deps use default parameter values)
        services.AddScoped<IAdminModelProviderMappingService, AdminModelProviderMappingService>();

        // Register Analytics services
        services.AddSingleton<IAnalyticsMetrics, AnalyticsMetricsService>();
        services.AddSingleton<AnalyticsCacheInvalidator>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        // Register AdminIpFilterService (optional deps use default parameter values)
        services.AddScoped<IAdminIpFilterService, AdminIpFilterService>();
        services.AddScoped<IAdminSystemInfoService, AdminSystemInfoService>();
        services.AddScoped<IAdminNotificationService, AdminNotificationService>();
        // Register AdminGlobalSettingService (optional deps use default parameter values)
        services.AddScoped<IAdminGlobalSettingService, AdminGlobalSettingService>();
        // Register AdminModelCostService (optional deps use default parameter values)
        services.AddScoped<IAdminModelCostService, AdminModelCostService>();

        // Register cost calculation dependencies with caching decorator pattern
        services.AddModelCostCache();
        services.AddScoped<ConduitLLM.Core.Interfaces.ICostCalculationService, ConduitLLM.Core.Services.CostCalculationService>();

        services.AddOptions<BillingCostCanaryOptions>()
            .BindConfiguration(BillingCostCanaryOptions.SectionName)
            .Validate(options => options.IntervalMinutes is >= 1 and <= 1440,
                "IntervalMinutes must be between 1 and 1440.")
            .ValidateOnStart();
        services.AddLeaderElectedHostedService<ModelCostCanaryHostedService>("ModelCostCanaryHostedService");

        // Register refund service
        services.AddScoped<ConduitLLM.Admin.Interfaces.IRefundService, ConduitLLM.Admin.Services.RefundService>();

        // Register media management service (requires IMediaLifecycleService and IMediaStorageService to be registered)
        services.AddScoped<IAdminMediaService>(serviceProvider =>
        {
            var mediaRepository = serviceProvider.GetRequiredService<IMediaRecordRepository>();
            var mediaLifecycleService = serviceProvider.GetService<IMediaLifecycleService>();
            var configurationContext = serviceProvider.GetRequiredService<IConfigurationDbContext>();
            var cleanupLockService = serviceProvider.GetRequiredService<IDistributedLockService>();
            var deletionEngine = serviceProvider.GetRequiredService<IMediaDeletionEngine>();
            var options = serviceProvider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<MediaLifecycleOptions>>();
            var logger = serviceProvider.GetRequiredService<ILogger<AdminMediaService>>();

            // Only register if media lifecycle service is available
            if (mediaLifecycleService == null)
            {
                throw new InvalidOperationException("IMediaLifecycleService must be registered to use AdminMediaService");
            }

            return new AdminMediaService(
                mediaRepository,
                mediaLifecycleService,
                configurationContext,
                cleanupLockService,
                deletionEngine,
                options,
                logger);
        });

        // ILLMClientFactory is registered via AddProviderServices() in the shared Providers extension
        // Do not duplicate here — the shared registration is the single source of truth

        // Register shared HTTP clients (DiscoveryProviders, ImageDownload, Exa, Tavily)
        services.AddSharedHttpClients();

        // Register Function services
        services.AddScoped<ConduitLLM.Functions.Interfaces.IFunctionCostService, ConduitLLM.Functions.Services.FunctionCostService>();
        services.AddScoped<ConduitLLM.Functions.Interfaces.IFunctionCostCalculationService, ConduitLLM.Functions.Services.FunctionCostCalculationService>();
        services.AddSingleton<ConduitLLM.Functions.Security.IFunctionCredentialProtector, ConduitLLM.Functions.Security.FunctionCredentialProtector>();
        services.AddScoped<ConduitLLM.Functions.Interfaces.IFunctionClientFactory, ConduitLLM.Functions.Services.FunctionClientFactory>();
        services.AddScoped<ConduitLLM.Functions.Interfaces.IFunctionExecutionService, ConduitLLM.Functions.Services.FunctionExecutionService>();

        // Register billing audit service for comprehensive billing event tracking - with leader election
        services.AddSingleton<ConduitLLM.Configuration.Interfaces.IBillingAuditService, ConduitLLM.Configuration.Services.BillingAuditService>();
        services.AddLeaderElectedHostedService<ConduitLLM.Configuration.Services.BillingAuditService>(
            provider => provider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IBillingAuditService>() as ConduitLLM.Configuration.Services.BillingAuditService
            ?? throw new InvalidOperationException("BillingAuditService must implement IHostedService"),
            "BillingAuditService");
        services.AddHostedService<ConduitLLM.Admin.Services.PricingConfigurationAuditHostedService>();

        // Register pricing rules engine services
        services.AddScoped<ConduitLLM.Core.Services.IPricingRulesEvaluator, ConduitLLM.Core.Services.PricingRulesEvaluator>();
        services.AddScoped<ConduitLLM.Core.Services.IPricingRulesValidator, ConduitLLM.Core.Services.PricingRulesValidator>();

        // Register cached pricing rules service for parsed configuration caching (uses the shared application FusionCache)
        services.AddPricingRulesCache();

        // Register pricing audit service for rules-based pricing event tracking - with leader election
        services.AddSingleton<ConduitLLM.Configuration.Interfaces.IPricingAuditService, ConduitLLM.Configuration.Services.PricingAuditService>();
        services.AddLeaderElectedHostedService<ConduitLLM.Configuration.Services.PricingAuditService>(
            provider => provider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IPricingAuditService>() as ConduitLLM.Configuration.Services.PricingAuditService
            ?? throw new InvalidOperationException("PricingAuditService must implement IHostedService"),
            "PricingAuditService");

        // The rate-limit usage endpoint reads windows the Gateway writes, so it needs the same
        // store. Registered only when Redis is present; the endpoint reports the ceilings and
        // flags the usage figures unavailable when it is not.
        services.AddSingleton<ConduitLLM.Core.Services.IVirtualKeyRateLimitService?>(serviceProvider =>
        {
            var redis = serviceProvider.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            if (redis is null)
            {
                return null;
            }

            return new ConduitLLM.Core.Services.RedisVirtualKeyRateLimitService(
                redis,
                serviceProvider.GetRequiredService<ILogger<ConduitLLM.Core.Services.RedisVirtualKeyRateLimitService>>());
        });

        // Register Redis error store with deferred resolution
        // IConnectionMultiplexer will be registered by AddRedisDataProtection in Program.cs after this method
        services.AddSingleton<ConduitLLM.Core.Interfaces.IRedisErrorStore>(serviceProvider =>
        {
            var redis = serviceProvider.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            var logger = serviceProvider.GetRequiredService<ILogger<ConduitLLM.Core.Services.RedisErrorStore>>();

            if (redis == null)
            {
                logger.LogError("[ConduitLLM.Admin] Redis connection not available. Redis error store will not function.");
                throw new InvalidOperationException("Redis error store requires Redis. Ensure REDIS_URL or CONDUIT_REDIS_CONNECTION_STRING is configured.");
            }

            logger.LogInformation("[ConduitLLM.Admin] Redis error store initialized");
            return new ConduitLLM.Core.Services.RedisErrorStore(redis, logger);
        });

        // Register provider error tracking service
        services.AddSingleton<ConduitLLM.Core.Interfaces.IProviderErrorTrackingService>(serviceProvider =>
        {
            var errorStore = serviceProvider.GetRequiredService<ConduitLLM.Core.Interfaces.IRedisErrorStore>();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var logger = serviceProvider.GetRequiredService<ILogger<ConduitLLM.Core.Services.ProviderErrorTrackingService>>();

            logger.LogInformation("[ConduitLLM.Admin] Provider error tracking service initialized with Redis backend");
            return new ConduitLLM.Core.Services.ProviderErrorTrackingService(errorStore, scopeFactory, logger);
        });

        // Configure CORS for the Admin API
        services.AddCors(options =>
        {
            options.AddPolicy("AdminCorsPolicy", policy =>
            {
                var allowedOrigins = configuration.GetSection("AdminApi:AllowedOrigins").Get<string[]>();
                if (allowedOrigins != null && allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins)
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials();
                }
                else
                {
                    policy.SetIsOriginAllowed(_ => true)
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials();
                }
            });
        });

        return services;
    }
}
