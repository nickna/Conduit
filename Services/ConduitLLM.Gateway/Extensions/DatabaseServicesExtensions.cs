using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interceptors;
using ConduitLLM.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ConduitLLM.Gateway.Extensions;

/// <summary>
/// Extension methods for registering database services
/// </summary>
public static class DatabaseServicesExtensions
{
    /// <summary>
    /// Adds database services including connection management, DbContext factory, and query monitoring
    /// </summary>
    public static IServiceCollection AddDatabaseServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Get connection string from environment variables
        var connectionStringManager = new ConnectionStringManager();
        // Pass "CoreAPI" to get Gateway API-specific connection pool settings
        var (dbProvider, dbConnectionString) = connectionStringManager.GetProviderAndConnectionString("CoreAPI");

        // Only PostgreSQL is supported
        if (dbProvider != "postgres")
        {
            throw new InvalidOperationException($"Only PostgreSQL is supported. Invalid provider: {dbProvider}");
        }

        // Schema readiness and native fixed-shape repositories share a host-owned pool.
        services.AddSingleton(_ => NpgsqlDataSource.Create(dbConnectionString));

        // Register DbContext Factory with query monitoring interceptor
        services.AddDbContextFactory<ConduitDbContext>((sp, options) =>
        {
            var interceptor = sp.GetRequiredService<QueryMonitoringInterceptor>();
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

        // Also add scoped registration from factory for services that need direct injection
        services.AddScoped<ConduitDbContext>(provider =>
        {
            var factory = provider.GetService<IDbContextFactory<ConduitDbContext>>();
            if (factory == null)
            {
                throw new InvalidOperationException("IDbContextFactory<ConfigurationDbContext> is not registered");
            }
            return factory.CreateDbContext();
        });

        return services;
    }
}
