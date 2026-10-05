using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Options;
using ConduitLLM.Core.Policies;
using ConduitLLM.Core.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Extensions
{
    /// <summary>
    /// Extension methods for configuring ConduitLLM Core services in an IServiceCollection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the ConduitLLM Context Window Management services to the service collection.
        /// </summary>
        /// <param name="services">The service collection to add services to.</param>
        /// <param name="configuration">The configuration instance.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddConduitContextManagement(this IServiceCollection services, IConfiguration configuration)
        {
            // Register configuration options
            services.Configure<ContextManagementOptions>(
                configuration.GetSection("ConduitLLM:ContextManagement"));

            // Register model capability service - use database-backed implementation
            services.TryAddScoped<IModelCapabilityService, DatabaseModelCapabilityService>();

            // Register image token calculator with retry-enabled HttpClient for accurate vision model billing
            services.AddHttpClient<IImageTokenCalculator, ImageTokenCalculator>()
                .AddPolicyHandler(HttpRetryPolicies.GetStandardRetryPolicy())
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30); // Reasonable timeout for image dimension checks
                });
            
            // Register usage estimation service for streaming responses without usage data
            services.AddScoped<IUsageEstimationService, UsageEstimationService>();

            // Register context manager
            services.AddScoped<IContextManager, ContextManager>();

            return services;
        }

        /// <summary>
        /// Adds model capability detection and caching services to the service collection.
        /// </summary>
        /// <param name="services">The service collection to add services to.</param>
        /// <param name="configuration">The configuration instance.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddModelCapabilityServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Register model capability service if not already registered - use database-backed implementation
            services.TryAddScoped<IModelCapabilityService, DatabaseModelCapabilityService>();

            // Register performance optimization services
            services.AddMemoryCache();

            return services;
        }

        /// <summary>
        /// Adds the ConduitLLM Batch Cache Invalidation services to the service collection.
        /// </summary>
        /// <param name="services">The service collection to add services to.</param>
        /// <param name="configuration">The configuration instance.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddBatchCacheInvalidation(
            this IServiceCollection services, 
            IConfiguration configuration)
        {
            // Register configuration options
            services.Configure<BatchInvalidationOptions>(
                configuration.GetSection("CacheInvalidation"));
            
            // Register batch service as singleton and hosted service
            services.AddSingleton<BatchCacheInvalidationService>();
            services.AddSingleton<IBatchCacheInvalidationService>(provider => 
                provider.GetRequiredService<BatchCacheInvalidationService>());
            services.AddHostedService(provider => 
                provider.GetRequiredService<BatchCacheInvalidationService>());
            
            return services;
        }

        /// <summary>
        /// Adds the ConduitLLM Discovery Cache services to the service collection.
        /// </summary>
        /// <param name="services">The service collection to add services to.</param>
        /// <param name="configuration">The configuration instance.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddDiscoveryCache(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            // Register configuration options
            services.Configure<DiscoveryCacheOptions>(
                configuration.GetSection("Discovery"));

            // Register discovery cache service as singleton for better performance
            services.AddSingleton<IDiscoveryCacheService>(provider =>
                provider.GetService<ConduitLLM.Core.Caching.ApplicationCacheOptions>()?.UsesFusionCache(
                    ConduitLLM.Core.Caching.ApplicationCacheDomain.Discovery) == true
                    ? ActivatorUtilities.CreateInstance<FusionDiscoveryCacheService>(provider)
                    : ActivatorUtilities.CreateInstance<DiscoveryCacheService>(provider));

            // Ensure memory cache is registered
            services.AddMemoryCache();

            return services;
        }

        /// <summary>
        /// Adds the ConduitLLM Function Discovery Cache services to the service collection.
        /// Caches function tool definitions with per-function TTL and global enable/disable toggle.
        /// </summary>
        /// <param name="services">The service collection to add services to.</param>
        /// <param name="configuration">The configuration instance.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddFunctionDiscoveryCache(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<CacheManagerOptions>(configuration.GetSection("CacheManager"));
            // Register function discovery cache service as scoped (depends on scoped repositories)
            services.AddScoped<IFunctionDiscoveryCacheService>(provider =>
                provider.GetService<ConduitLLM.Core.Caching.ApplicationCacheOptions>()?.UsesFusionCache(
                    ConduitLLM.Core.Caching.ApplicationCacheDomain.Functions) == true
                    ? ActivatorUtilities.CreateInstance<FusionFunctionDiscoveryCacheService>(provider)
                    : ActivatorUtilities.CreateInstance<FunctionDiscoveryCacheService>(provider));

            // Ensure memory cache is registered
            services.AddMemoryCache();

            return services;
        }

        /// <summary>
        /// Registers application services shared by both Admin API and Gateway API.
        /// Centralizes registrations that were previously duplicated across both services.
        /// </summary>
        public static IServiceCollection AddSharedApplicationServices(this IServiceCollection services)
        {
            // Global settings cache — loads settings at startup and provides fast access
            services.AddSingleton<IGlobalSettingsCacheService, GlobalSettingsCacheService>();
            services.AddHostedService(provider =>
                provider.GetRequiredService<IGlobalSettingsCacheService>() as GlobalSettingsCacheService
                ?? throw new InvalidOperationException("GlobalSettingsCacheService must be registered as singleton"));

            // Provider service
            services.AddScoped<IProviderService, ProviderService>();

            // Model provider mapping with caching decorator
            services.AddScoped<ModelProviderMappingService>();
            services.AddScoped<IModelProviderMappingService>(provider =>
            {
                var innerService = provider.GetRequiredService<ModelProviderMappingService>();
                var cacheManager = provider.GetRequiredService<ICacheManager>();
                var logger = provider.GetRequiredService<ILogger<CachedModelProviderMappingService>>();
                return new CachedModelProviderMappingService(innerService, cacheManager, logger);
            });

            return services;
        }

    }
}
