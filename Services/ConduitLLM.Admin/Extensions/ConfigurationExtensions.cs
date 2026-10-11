using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.ModelCatalogs;
using ConduitLLM.Core.Extensions;
#if CONDUIT_NATIVE_AOT
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Persistence.Npgsql;
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif

namespace ConduitLLM.Admin.Extensions
{
    /// <summary>
    /// Extension methods for configuring Configuration services in the Admin API
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Adds the Configuration services to the DI container
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configuration">The application configuration</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddConfigurationServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Add repositories
            services.AddRepositories();
#if CONDUIT_NATIVE_AOT
            // Use the parity-tested fixed-query stores for native settings and
            // IP-filter administration, including their shared runtime caches.
            services.RemoveAll<IGlobalSettingRepository>();
            services.AddSingleton<IGlobalSettingRepository, NpgsqlGlobalSettingRepository>();
            services.RemoveAll<IIpFilterRepository>();
            services.AddSingleton<IIpFilterRepository, NpgsqlIpFilterRepository>();
#endif

            // Add caching services
            services.AddCachingServices(configuration);

            // Add read-only database migration readiness handling.
            services.AddDatabaseMigration();

            // The Admin import endpoint can explicitly merge the embedded catalog at
            // runtime. This is application behavior, not schema migration.
            services.AddSingleton<BundledModelCatalog>();
            services.AddScoped<IBundledModelCatalogImporter, BundledModelCatalogImporter>();

            // Customer error mode (CONDUIT_CUSTOMER_MODE) — Admin reports it on System Info;
            // its own error middleware stays operator-facing and does not use the translator.
            services.AddCustomerErrorTranslation();

            // Shared application services (GlobalSettingsCache, ProviderService,
            // ModelProviderMapping+decorator)
            services.AddSharedApplicationServices();

            return services;
        }
    }
}
