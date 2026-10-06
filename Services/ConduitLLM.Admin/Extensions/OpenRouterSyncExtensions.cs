using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.Options;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Admin.Extensions
{
    /// <summary>
    /// Registration for the OpenRouter metadata sync (drift detection + review + scheduled job).
    /// </summary>
    public static class OpenRouterSyncExtensions
    {
        /// <summary>
        /// Registers the OpenRouter drift-detection + apply services, the named HTTP client, and the
        /// scheduled sync hosted service. Relies on <c>IDistributedLockProvider</c> being registered
        /// (by AddMediaLifecycleServices).
        /// </summary>
        public static IServiceCollection AddOpenRouterSyncServices(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<OpenRouterSyncOptions>(configuration.GetSection(OpenRouterSyncOptions.SectionName));
            services.AddHttpClient("OpenRouterSync");
            services.AddScoped<IOpenRouterDriftDetectionService, OpenRouterDriftDetectionService>();
            services.AddScoped<IAdminProviderSyncService, AdminProviderSyncService>();
            services.AddHostedService<OpenRouterMetadataSyncService>();
            return services;
        }
    }
}
