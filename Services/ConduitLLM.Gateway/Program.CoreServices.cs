using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Providers.Extensions;
using Microsoft.Extensions.Caching.Distributed;

public partial class Program
{
    public static void ConfigureCoreServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ConduitLLM.Core.Services.IEventPublisher,
            ConduitLLM.Core.Services.EventPublisher>();
        builder.Services.AddGatewayEndpointHandlers();
        // ========== Core Infrastructure ==========

        // Add leader election service for distributed background service coordination
        builder.Services.AddLeaderElection();

        // Shared application services (GlobalSettingsCache, ProviderService,
        // ModelProviderMapping+decorator)
        builder.Services.AddSharedApplicationServices();

        // ========== Caching Infrastructure ==========

        builder.Services.AddMemoryCache();
        // ConfigureCachingServices registers the legacy manager once, alongside the application cache.

        // ========== Correlation Context ==========

        builder.Services.AddCorrelationContext();

        // ========== Observability ==========

        builder.Services.AddObservabilityServices(builder.Configuration);

        // ========== Database Services ==========

        builder.Services.AddDatabaseServices(builder.Configuration);

        // ========== Security ==========

        builder.Services.AddCoreApiSecurity(builder.Configuration);

        // ========== HTTP Infrastructure ==========

        builder.Services.AddHttpClient();
        builder.Services.AddLLMProviderHttpClients();
        builder.Services.AddHttpClientServices(builder.Configuration);

        // Register operation timeout provider for operation-aware timeout policies
        builder.Services.AddSingleton<IOperationTimeoutProvider, ConduitLLM.Core.Configuration.OperationTimeoutProvider>();

        // ========== Provider Services ==========

        // Register LLM client factory and provider services from shared extension
        builder.Services.AddProviderServices();

        // Provider error tracking service
        builder.Services.AddSingleton<IRedisErrorStore, RedisErrorStore>();
        builder.Services.AddSingleton<IProviderErrorTrackingService, ProviderErrorTrackingService>();
        builder.Services.Configure<ProviderKeyReprobeOptions>(
            builder.Configuration.GetSection(ProviderKeyReprobeOptions.SectionName));
        builder.Services.AddHostedService<ProviderKeyReprobeService>();

        // Add performance metrics service
        builder.Services.AddSingleton<IPerformanceMetricsService, PerformanceMetricsService>();

        // ========== Billing & Pricing ==========

        builder.Services.AddBillingAndPricingServices();

        // ========== Token Management ==========

        // Parameter validation service for minimal, provider-agnostic validation
        builder.Services.AddScoped<ConduitLLM.Core.Validation.MinimalParameterValidator>();

        ConfigureContextManagementServices(builder);

        // ========== Repositories ==========

        builder.Services.AddRepositories();

        // ========== Model Services ==========

        // Note: ModelProviderMappingService+decorator and ProviderService registered via AddSharedApplicationServices() above

        // Register System Notification Service
        builder.Services.AddSingleton<ISystemNotificationService, SystemNotificationService>();

        // Register Model Metadata Service
        builder.Services.AddScoped<IModelMetadataService, ModelMetadataService>();

        // ========== Audit Services ==========

        builder.Services.AddAuditServices();
#if CONDUIT_NATIVE_AOT
        builder.Services.UseNativeRuntimePersistence();
#endif

        // ========== Webhook Services ==========

        builder.Services.AddWebhookServices(builder.Configuration);

        // ========== Async Task Services ==========

        // Register cancellable task registry
        builder.Services.AddSingleton<ICancellableTaskRegistry, CancellableTaskRegistry>();

        builder.Services.AddAsyncTaskServices();
        builder.Services.AddOptions<ConduitLLM.Core.Options.AsyncTaskRetentionOptions>()
            .BindConfiguration(ConduitLLM.Core.Options.AsyncTaskRetentionOptions.SectionName);
        builder.Services.AddHostedService<AsyncTaskRetentionService>();
        builder.Services.AddHostedService<MediaTaskLeaseRecoveryService>();

        // ========== Conduit Service ==========

        builder.Services.AddScoped<Conduit>();

        // ========== Model Capability Services ==========

        builder.Services.AddModelCapabilityServices(builder.Configuration);

        // ========== Function Services ==========

        builder.Services.AddFunctionServices();

        // ========== Cache Services ==========

        // Register Batch Cache Invalidation service
        builder.Services.AddBatchCacheInvalidation(builder.Configuration);

        // Register Discovery Cache service for model discovery endpoint caching
        builder.Services.AddDiscoveryCache(builder.Configuration);

        // Register Discovery Cache warming as a hosted service (runs on startup)
        builder.Services.AddLeaderElectedHostedService<DiscoveryCacheWarmingService>("DiscoveryCacheWarmingService");

        // Register Function Discovery Cache service for function tool definition caching
        builder.Services.AddFunctionDiscoveryCache(builder.Configuration);

        // ========== Media Generation Services ==========

        builder.Services.AddMediaGenerationServices(builder.Configuration, builder.Environment);
    }

    /// <summary>
    /// Registers the complete context-management dependency graph used by chat requests.
    /// Kept as a separate method so controller activation can be covered without booting
    /// infrastructure such as PostgreSQL, Redis, and Wolverine.
    /// </summary>
    public static void ConfigureContextManagementServices(WebApplicationBuilder builder)
    {
        builder.Services.AddConduitTokenization();
        builder.Services.AddConduitContextManagement(builder.Configuration);
    }
}
