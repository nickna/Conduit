using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Data
{
    /// <summary>
    /// Registration for read-only database migration readiness handling.
    /// Behavior is governed by CONDUIT_MIGRATION_MODE — see <see cref="MigrationMode"/>.
    /// </summary>
    public static class MigrationExtensions
    {
        /// <summary>
        /// Add migration services to DI container.
        /// </summary>
        public static IServiceCollection AddDatabaseMigration(this IServiceCollection services)
        {
            services.AddSingleton(sp => MigrationStartupOptions.FromEnvironment(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Conduit.MigrationStartup")));
            services.AddSingleton<MigrationReadinessState>();
            services.AddSingleton<ISchemaVersionProbe, SchemaVersionProbe>();
            services.AddHostedService<MigrationWaitService>();
            return services;
        }
    }
}
