using ConduitLLM.Core.Data;
using ConduitLLM.Core.Data.Extensions;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Admin.Extensions
{
    /// <summary>
    /// Extension methods for configuring Core services in the Admin API
    /// </summary>
    public static class CoreExtensions
    {
        /// <summary>
        /// Adds the Core services to the DI container
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configuration">The application configuration</param>
        /// <param name="startupLogger">Optional logger for startup diagnostics</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddCoreServices(this IServiceCollection services, IConfiguration configuration, ILogger? startupLogger = null)
        {
            // Add database services - use ConfigurationDbContext
            services.AddDatabaseServices<ConduitLLM.Configuration.ConduitDbContext>();

            // Register DbContext Factory (using connection string from environment variables)
            var connectionStringManager = new ConnectionStringManager();
            // Pass "AdminAPI" to get Admin API-specific connection pool settings
            var (dbProvider, dbConnectionString) = connectionStringManager.GetProviderAndConnectionString("AdminAPI", msg => startupLogger?.LogInformation("{Message}", msg));

            // Log the connection pool settings for verification
            if (dbProvider == "postgres" && dbConnectionString.Contains("MaxPoolSize"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(dbConnectionString, @"MinPoolSize=(\d+);MaxPoolSize=(\d+)");
                if (match.Success)
                {
                    startupLogger?.LogInformation(
                        "Admin API database connection pool configured — MinPoolSize: {MinPoolSize}, MaxPoolSize: {MaxPoolSize}",
                        match.Groups[1].Value, match.Groups[2].Value);
                }
            }

            // Only PostgreSQL is supported
            if (dbProvider != "postgres")
            {
                throw new InvalidOperationException($"Only PostgreSQL is supported. Invalid provider: {dbProvider}");
            }

            // Configure query monitoring for performance tracking
            services.Configure<ConduitLLM.Configuration.Interceptors.QueryMonitoringOptions>(
                configuration.GetSection(ConduitLLM.Configuration.Interceptors.QueryMonitoringOptions.SectionName));
            services.AddSingleton<ConduitLLM.Configuration.Interceptors.QueryMonitoringInterceptor>();

            services.AddDbContextFactory<ConduitLLM.Configuration.ConduitDbContext>((sp, options) =>
            {
                var interceptor = sp.GetRequiredService<ConduitLLM.Configuration.Interceptors.QueryMonitoringInterceptor>();
                options.UseNpgsql(dbConnectionString, npgsql =>
                           // Transient-failure resilience. Explicit BeginTransaction calls are
                           // incompatible with a retrying strategy — use
                           // ExecuteInTransactionAsync (ExecutionStrategyExtensions) instead.
                           npgsql.EnableRetryOnFailure(
                               maxRetryCount: 5,
                               maxRetryDelay: TimeSpan.FromSeconds(10),
                               errorCodesToAdd: null))
                       .AddInterceptors(interceptor);
            });
            startupLogger?.LogInformation("Query monitoring interceptor configured for performance tracking");

            // Also add scoped registration from factory for services that need direct injection
            // Note: This creates contexts from the factory on demand
            services.AddScoped<ConduitLLM.Configuration.ConduitDbContext>(provider =>
            {
                var factory = provider.GetService<IDbContextFactory<ConduitLLM.Configuration.ConduitDbContext>>();
                if (factory == null)
                {
                    throw new InvalidOperationException("IDbContextFactory<ConfigurationDbContext> is not registered");
                }
                return factory.CreateDbContext();
            });

            // The Admin discovery preview uses the same pricing visibility option as Gateway,
            // without registering the cache implementation itself.
            services.Configure<ConduitLLM.Core.Services.DiscoveryCacheOptions>(
                configuration.GetSection("Discovery"));

            // Note: AddDiscoveryCache is not registered in Admin API as it's optional
            // The SystemInfo endpoint handler has IDiscoveryCacheService? as a nullable dependency.
            // The shared application cache is registered by the host; preview does not populate it.

            // Add Function Discovery Cache for function tool definition caching
            services.AddFunctionDiscoveryCache(configuration);
            startupLogger?.LogInformation("Function Discovery Cache registered — function tool definitions will be cached based on per-function TTL");

            // Add correlation context services for cross-service request tracing
            services.AddCorrelationContext();

            return services;
        }
    }
}
