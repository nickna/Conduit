using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Functions.Interfaces;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Configuration.Extensions
{
    /// <summary>
    /// Extension methods for configuring repository services
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds repository services to the service collection
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddRepositories(this IServiceCollection services)
        {
            // Register DbContext interface
            services.AddScoped<IConfigurationDbContext>(provider =>
                provider.GetRequiredService<ConduitDbContext>());

            // Register repositories
            services.AddScoped<IVirtualKeyRepository, VirtualKeyRepository>();
            services.AddScoped<IVirtualKeyGroupRepository, VirtualKeyGroupRepository>();
#if !CONDUIT_NATIVE_AOT
            services.AddSingleton<IVirtualKeyRuntimeStore, EfVirtualKeyRuntimeStore>();
            services.AddScoped<IGatewayMetricsStore, EfGatewayMetricsStore>();
#endif
            services.AddScoped<IProviderRepository, ProviderRepository>();
            services.AddScoped<IProviderKeyCredentialRepository, ProviderKeyCredentialRepository>();
            services.AddScoped<IGlobalSettingRepository, GlobalSettingRepository>();
            services.AddScoped<IModelProviderMappingRepository, ModelProviderMappingRepository>();
            services.AddScoped<IModelCostRepository, ModelCostRepository>();
            services.AddScoped<IModelRepository, ModelRepository>();
            services.AddScoped<IModelSeriesRepository, ModelSeriesRepository>();
            services.AddScoped<IModelAuthorRepository, ModelAuthorRepository>();
            services.AddScoped<IRequestLogRepository, RequestLogRepository>();
            
            // Register validator
            services.AddScoped<ProviderKeyCredentialValidator>();

            // Register new repositories
            services.AddScoped<INotificationRepository, NotificationRepository>();
            services.AddScoped<IVirtualKeySpendHistoryRepository, VirtualKeySpendHistoryRepository>();
            services.AddScoped<IIpFilterRepository, IpFilterRepository>();

            // Register async task repository
            services.AddScoped<IAsyncTaskRepository, AsyncTaskRepository>();
            services.AddScoped<IAsyncTaskRuntimeStore, EfAsyncTaskRuntimeStore>();

            // Register media record repository
            services.AddScoped<IMediaRecordRepository, MediaRecordRepository>();
            services.AddScoped<IMediaRuntimeStore, EfMediaRuntimeStore>();

            // Register Function repositories
            services.AddScoped<IFunctionConfigurationRepository, FunctionConfigurationRepository>();
            services.AddScoped<IFunctionCostRepository, FunctionCostRepository>();
            services.AddScoped<IFunctionCostMappingRepository, FunctionCostMappingRepository>();
            services.AddScoped<IFunctionCredentialRepository, FunctionCredentialRepository>();
            services.AddScoped<IFunctionExecutionRepository, FunctionExecutionRepository>();

            return services;
        }

        /// <summary>
        /// Adds caching services to the service collection
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configuration">The application configuration</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddCachingServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Register cache options
            services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));

            // Add memory cache
            services.AddMemoryCache(options =>
            {
                var cacheSection = configuration.GetSection(CacheOptions.SectionName);
                options.SizeLimit = cacheSection.GetValue<long?>("MaxCacheItems");
            });

            // Add Redis connection factory
            services.AddSingleton<RedisConnectionFactory>();

            // Register the appropriate distributed cache provider based on configuration
            var cacheType = configuration.GetSection(CacheOptions.SectionName)
                .GetValue<string>("CacheType")?.ToLowerInvariant();

            if (cacheType == "redis")
            {
                var redisConnectionString = configuration.GetSection(CacheOptions.SectionName)
                    .GetValue<string>("RedisConnectionString");

                var redisInstanceName = configuration.GetSection(CacheOptions.SectionName)
                    .GetValue<string>("RedisInstanceName") ?? "conduitllm-cache";

                if (!string.IsNullOrEmpty(redisConnectionString))
                {
                    services.AddStackExchangeRedisCache(options =>
                    {
                        options.Configuration = redisConnectionString;
                        options.InstanceName = redisInstanceName;
                    });
                }
                else
                {
                    // Fall back to memory cache if Redis connection string is not configured
                    services.AddDistributedMemoryCache();
                }
            }
            else
            {
                // Use memory cache if Redis is not specified
                services.AddDistributedMemoryCache();
            }

            return services;
        }

    }
}
