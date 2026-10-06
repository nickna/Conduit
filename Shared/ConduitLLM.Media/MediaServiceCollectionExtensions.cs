using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Options;
using ConduitLLM.Core.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Core.Extensions;

/// <summary>Registers media lifecycle services and the selected storage implementation.</summary>
public static class MediaServiceCollectionExtensions
{
    public static IServiceCollection AddMediaServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var configProvider = configuration.GetValue<string>("ConduitLLM:Storage:Provider");
        var configEnvVar = configuration.GetValue<string>("CONDUIT_MEDIA_STORAGE_TYPE");
        var directEnvVar = Environment.GetEnvironmentVariable("CONDUIT_MEDIA_STORAGE_TYPE");
        var legacyServiceUrl = configuration["CONDUIT_S3_SERVICE_URL"]
            ?? Environment.GetEnvironmentVariable("CONDUIT_S3_SERVICE_URL");
        var storageProvider = configProvider
            ?? configEnvVar
            ?? directEnvVar
            ?? (!string.IsNullOrWhiteSpace(legacyServiceUrl) ? "S3" : "InMemory");

        if (storageProvider.Equals("S3", StringComparison.OrdinalIgnoreCase))
        {
            services.Configure<S3StorageOptions>(options =>
            {
                configuration.GetSection(S3StorageOptions.SectionName).Bind(options);
                ApplyConfigOrEnvVar(configuration, value => options.ServiceUrl = value,
                    "CONDUIT_S3_ENDPOINT", "CONDUIT_S3_SERVICE_URL");
                ApplyConfigOrEnvVar(configuration, value => options.AccessKey = value,
                    "CONDUIT_S3_ACCESS_KEY_ID", "CONDUIT_S3_ACCESS_KEY");
                ApplyConfigOrEnvVar(configuration, value => options.SecretKey = value,
                    "CONDUIT_S3_SECRET_ACCESS_KEY", "CONDUIT_S3_SECRET_KEY");
                ApplyConfigOrEnvVar(configuration, value => options.BucketName = value,
                    "CONDUIT_S3_BUCKET_NAME");
                ApplyConfigOrEnvVar(configuration, value => options.Region = value,
                    "CONDUIT_S3_REGION");
                ApplyConfigOrEnvVar(configuration, value => options.PublicBaseUrl = value,
                    "CONDUIT_S3_PUBLIC_BASE_URL");
                options.ForcePathStyle = true;
                options.AutoCreateBucket = true;
            });
            services.AddSingleton<IMediaStorageService, S3MediaStorageService>();
        }
        else
        {
            services.AddSingleton<IMediaStorageService, InMemoryMediaStorageService>();
        }

        services.AddScoped<IMediaLifecycleService, MediaLifecycleService>();
        services.AddScoped<IMediaQuotaService, MediaQuotaService>();
        services.AddSingleton<IMediaQuotaGuard, MediaQuotaGuard>();
        return services;
    }

    private static void ApplyConfigOrEnvVar(
        IConfiguration configuration,
        Action<string> setter,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key] ?? Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
            {
                setter(value);
                return;
            }
        }
    }
}
