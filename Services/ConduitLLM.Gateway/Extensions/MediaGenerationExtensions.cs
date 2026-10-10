using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Services;

namespace ConduitLLM.Gateway.Extensions;

/// <summary>
/// Extension methods for registering media generation services
/// </summary>
public static class MediaGenerationExtensions
{
    /// <summary>
    /// Adds media generation services including video generation, retry configuration, metrics, and orchestrators
    /// </summary>
    public static IServiceCollection AddMediaGenerationServices(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Configure Video Generation Retry Settings
        services.Configure<VideoGenerationRetryConfiguration>(options =>
        {
            options.MaxRetries = configuration.GetValue<int>("VideoGeneration:MaxRetries", 3);
            options.BaseDelaySeconds = configuration.GetValue<int>("VideoGeneration:BaseDelaySeconds", 30);
            options.MaxDelaySeconds = configuration.GetValue<int>("VideoGeneration:MaxDelaySeconds", 3600);
            options.EnableRetries = configuration.GetValue<bool>("VideoGeneration:EnableRetries", true);
            options.RetryCheckIntervalSeconds = configuration.GetValue<int>("VideoGeneration:RetryCheckIntervalSeconds", 30);
            options.JitterPercentage = configuration.GetValue<int>("VideoGeneration:JitterPercentage", 20);
        });

        // Messaging handlers need these collaborators in every environment. Registering
        // them does not start background workers.
        services.AddSingleton<MediaGenerationMetrics>();
        services.AddScoped<ImageGenerationOrchestrator>();
        services.AddScoped<VideoGenerationOrchestrator>();

        return services;
    }
}
