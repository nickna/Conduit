#if CONDUIT_WOLVERINE_CODEGEN
using JasperFx;
#endif
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Extensions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

// prometheus-net's Meter adapter owns the /metrics representation. Configure its
// streaming histogram buckets before any application metrics can be initialized.
ConduitLLM.Gateway.Metrics.PrometheusMeterAdapterConfiguration.Configure();

// DatabaseAwareLLMClientFactory now in Providers namespace
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"
});

// Configure basic settings and environment
Program.ConfigureBasicSettings(builder);

// The build-time exporter creates a host to inspect endpoint metadata. Keep that host
// infrastructure-free: no database, Redis, messaging, migrations, or hosted services.
if (Environment.GetEnvironmentVariable("CONDUIT_OPENAPI_GENERATION") == "true")
{
    Program.ConfigureOpenApiServices(builder);
    builder.Services.AddAuthorization();
    var openApiApp = builder.Build();
    openApiApp.MapModelsEndpoints();
    openApiApp.MapGatewayApiEndpoints();

    var outputPath = Environment.GetEnvironmentVariable("CONDUIT_OPENAPI_OUTPUT");
    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        openApiApp.Urls.Add("http://127.0.0.1:0");
        await openApiApp.StartAsync();
        try
        {
            await using var output = File.Create(outputPath);
            var provider = openApiApp.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
            var document = await provider.GetOpenApiDocumentAsync(default);
            await document.SerializeAsJsonAsync(output, OpenApiSpecVersion.OpenApi3_1, default);
        }
        finally
        {
            await openApiApp.StopAsync();
        }
        return 0;
    }

    await openApiApp.RunAsync();
    return 0;
}

// Configure all service registrations
Program.ConfigureCoreServices(builder);
Program.ConfigureSecurityServices(builder);
Program.ConfigureCachingServices(builder);
Program.ConfigureMessagingServices(builder);
Program.ConfigureSignalRServices(builder);
Program.ConfigureMediaServices(builder);
Program.ConfigureMonitoringServices(builder);
builder.Services.AddConduitRateLimiting(builder.Configuration);

var app = builder.Build();

// Configure middleware pipeline
await Program.ConfigureMiddleware(app);

// Configure endpoints
Program.ConfigureEndpoints(app);

#if CONDUIT_WOLVERINE_CODEGEN
// Generation-only command path, enabled by scripts/generate-wolverine-code.ps1.
return await app.RunJasperFxCommands(args);
#else
await app.RunAsync();
return 0;
#endif

// Make Program class accessible for testing
public partial class Program { }
