using ConduitLLM.Configuration;
using ConduitLLM.Gateway.Options;
using Microsoft.Extensions.Options;

public partial class Program
{
    public static void ConfigureBasicSettings(WebApplicationBuilder builder)
    {
        // Use environment variables ONLY for configuration
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddEnvironmentVariables();

        builder.Services.ConfigureHttpJsonOptions(options =>
            GatewayJsonOptions.Configure(options.SerializerOptions));

        // Surface request-binding failures as BadHttpRequestException so OpenAIErrorMiddleware can
        // answer with the OpenAI error envelope. Without this, a malformed or incomplete body returns
        // a bare 400 with an empty body, which is not a valid OpenAI-compatible error response (#1191).
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddSingleton(services =>
            services
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value
                .SerializerOptions);

        // 1. Configure Conduit Settings
        builder.Services.AddOptions<ConduitSettings>()
            .Bind(builder.Configuration.GetSection("Conduit"));

        builder.Services.AddOptions<UsageTrackingOptions>()
            .Bind(builder.Configuration.GetSection("UsageTracking"))
            .Validate(options =>
                    options.MaximumStreamingCompletionCharacters is >= 1024 and <= 16 * 1024 * 1024 &&
                    options.MaximumStreamingToolCallCharacters is >= 1024 and <= 16 * 1024 * 1024 &&
                    options.MaximumStreamingToolCalls is >= 1 and <= 1024 &&
                    options.AccountingFinalizationTimeoutSeconds is >= 1 and <= 120 &&
                    options.GracefulShutdownSeconds is >= 5 and <= 600,
                "UsageTracking settings are outside their supported ranges.")
            .ValidateOnStart();
        builder.Services.Configure<HostOptions>(options =>
        {
            var shutdownSeconds = builder.Configuration.GetValue<int?>(
                "UsageTracking:GracefulShutdownSeconds") ?? 45;
            options.ShutdownTimeout = TimeSpan.FromSeconds(shutdownSeconds);
        });

        builder.Services.AddOptions<BillingAdmissionOptions>()
            .Bind(builder.Configuration.GetSection(BillingAdmissionOptions.SectionName))
            .Validate(options =>
                    options.DefaultMaximumOutputTokens is >= 1 and <= 1_000_000 &&
                    options.MaximumOutputTokensCap is >= 1 and <= 1_000_000 &&
                    options.ApproximateVocabularyPromptBuffer is >= 0.0 and <= 2.0 &&
                    options.CharacterHeuristicPromptBuffer is >= 0.0 and <= 2.0,
                "BillingAdmission settings are outside their supported ranges.")
            .ValidateOnStart();

    }
}
