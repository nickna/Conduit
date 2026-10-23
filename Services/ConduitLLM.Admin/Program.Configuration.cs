namespace ConduitLLM.Admin;

public partial class Program
{
    /// <summary>Restricts deploy-time application configuration to environment variables.</summary>
    internal static void ConfigureBasicSettings(WebApplicationBuilder builder)
    {
        // Remove JSON, user secrets, and command-line providers before any services bind settings.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddEnvironmentVariables();
    }
}
