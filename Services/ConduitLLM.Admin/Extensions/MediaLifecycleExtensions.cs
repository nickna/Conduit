using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;

namespace ConduitLLM.Admin.Extensions
{
    /// <summary>
    /// Extension methods for configuring media lifecycle services in the Admin API.
    /// </summary>
    public static class MediaLifecycleExtensions
    {
        /// <summary>
        /// Adds media lifecycle management services to the Admin API.
        /// This includes the cleanup service and related infrastructure.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configuration">The application configuration</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMediaLifecycleServices(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            // Configure media lifecycle options
            services.Configure<MediaLifecycleOptions>(
                configuration.GetSection(MediaLifecycleOptions.SectionName));

            var options = configuration
                .GetSection(MediaLifecycleOptions.SectionName)
                .Get<MediaLifecycleOptions>() ?? new MediaLifecycleOptions();

            // Register distributed lock service (PostgreSQL-based, works without Redis)
            services.AddConduitDistributedLocks();

            // Use the same media registration and environment-variable contract as Gateway.
            services.AddMediaServices(configuration);
            services.AddSingleton<IMediaStorageHealthProbe>(serviceProvider =>
                (IMediaStorageHealthProbe)serviceProvider.GetRequiredService<IMediaStorageService>());

            // Register media deletion budget tracking service
            RegisterBudgetTrackingService(services, configuration, options);

            // Register media cleanup status service for tracking and management
            services.AddSingleton<IMediaCleanupStatusService, MediaCleanupStatusService>();
            services.AddScoped<IMediaCleanupApprovalService, MediaCleanupApprovalService>();
            services.AddScoped<IMediaDeletionEngine, MediaDeletionEngine>();
            services.AddScoped<IMediaReconciliationService, MediaReconciliationService>();

            // Validate the storage backend before any cleanup background work starts.
            services.AddSingleton<MediaStorageConfigurationGuard>();
            services.AddSingleton<IMediaStorageConfigurationGuard>(
                provider => provider.GetRequiredService<MediaStorageConfigurationGuard>());
            services.AddHostedService<MediaStorageConfigurationGuard>(
                provider => provider.GetRequiredService<MediaStorageConfigurationGuard>());

            // Register the unified cleanup service - it will check IsSchedulerEnabled internally
            // Uses distributed locking to ensure only one instance runs across a cluster
            services.AddSingleton<MediaCleanupService>();
            services.AddHostedService(
                provider => provider.GetRequiredService<MediaCleanupService>());

            return services;
        }

        private static void RegisterBudgetTrackingService(
            IServiceCollection services,
            IConfiguration configuration,
            MediaLifecycleOptions options)
        {
            // Check if Redis is configured
            var redisConnectionString = ConduitLLM.Configuration.Utilities.RedisUrlParser.ResolveConnectionString();

            if (!string.IsNullOrEmpty(redisConnectionString))
            {
                // Redis is available - use Redis-based budget tracking
                // Note: IConnectionMultiplexer should already be registered by Admin API
                services.AddSingleton<IMediaDeletionBudgetService, RedisMediaDeletionBudgetService>();
            }
            else
            {
                // No Redis - use in-memory tracking (development mode)
                services.AddSingleton<IMediaDeletionBudgetService, InMemoryMediaDeletionBudgetService>();
                // stdout like the other startup warnings: stderr here fails design-time
                // hosts (build-time OpenAPI export treats stderr output as errors)
                Console.WriteLine("[ConduitLLM.Admin] WARNING: Budget tracking will not persist across restarts or be shared across instances");
            }
        }

    }
}
