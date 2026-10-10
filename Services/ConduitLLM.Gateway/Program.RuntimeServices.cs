using ConduitLLM.Gateway.Extensions;

public partial class Program
{
    /// <summary>Registers the complete normal Gateway host graph.</summary>
    public static void ConfigureRuntimeServices(WebApplicationBuilder builder)
    {
        RequireRedisConfiguration();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        ConfigureCoreServices(builder);
        ConfigureSecurityServices(builder);
        ConfigureCachingServices(builder);
        ConfigureMessagingServices(builder);
        ConfigureSignalRServices(builder);
        ConfigureMediaServices(builder);
        ConfigureMonitoringServices(builder);
        builder.Services.AddConduitRateLimiting(builder.Configuration);
    }
}
