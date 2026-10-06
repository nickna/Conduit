using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Security.Extensions;
using ConduitLLM.Security.Middleware;

#if CONDUIT_WOLVERINE_CODEGEN
using JasperFx;
#endif
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

using Scalar.AspNetCore;

namespace ConduitLLM.Admin;

/// <summary>
/// Entry point for the Admin API application
/// </summary>
public partial class Program
{
    /// <summary>
    /// Application entry point that configures and starts the web application
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <returns>Process exit code</returns>
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Create a startup logger for structured logging during service registration
        using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var startupLogger = startupLoggerFactory.CreateLogger("ConduitLLM.Admin.Startup");

        // Add services to the container
        // Keep Minimal API JSON aligned with the established Admin contract.
        builder.Services.ConfigureHttpJsonOptions(options =>
            AdminJsonOptions.Configure(options.SerializerOptions));
        builder.Services.AddValidation();
        builder.Services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                if (context.ProblemDetails is HttpValidationProblemDetails validation)
                {
                    context.ProblemDetails.Detail = string.Join(
                        "; ",
                        validation.Errors.SelectMany(static entry =>
                            entry.Value.Select(message => string.IsNullOrEmpty(entry.Key)
                                ? message
                                : $"{entry.Key}: {message}")));
                }
                context.ProblemDetails.Extensions["code"] =
                    AdminErrorCodes.ForStatus(context.ProblemDetails.Status);
                context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
            });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<AnalyticsEndpoints>();
        builder.Services.AddScoped<FunctionConfigurationsEndpoints>();
        builder.Services.AddScoped<ProviderErrorsEndpoints>();
        builder.Services.AddScoped<MediaRetentionEndpoints>();
        builder.Services.AddScoped<ProviderToolsEndpoints>();
        builder.Services.AddScoped<PricingEndpoints>();
        builder.Services.AddScoped<ModelProviderMappingEndpoints>();
        builder.Services.AddScoped<ModelCostsEndpoints>();
        builder.Services.AddScoped<ProviderCredentialsEndpoints>();
        builder.Services.AddScoped<ModelEndpoints>();
        builder.Services.AddScoped<VirtualKeyGroupsEndpoints>();
        builder.Services.AddScoped<VirtualKeysEndpoints>();
        builder.Services.AddScoped<IpFilterEndpoints>();

        builder.Services.AddEndpointsApiExplorer();

        // Add HttpClient factory for provider connection testing
        builder.Services.AddHttpClient();

        // Configure built-in OpenAPI support
        builder.Services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer<ConduitLLM.Admin.OpenApi.AdminApiDocumentTransformer>();
            options.AddOperationTransformer<ConduitLLM.Core.OpenApi.OperationMetadataTransformer>();
            options.AddOperationTransformer<ConduitLLM.Admin.OpenApi.ApiKeySecurityOperationTransformer>();
            options.AddOperationTransformer<ConduitLLM.Admin.OpenApi.ConditionalRequestOperationTransformer>();
            options.AddOperationTransformer<ConduitLLM.Admin.OpenApi.CollectionPaginationOperationTransformer>();
            // Tier 2b (#905): document the universal 500 once, so controllers can drop the per-action
            // per-endpoint 500-response boilerplate.
            options.AddOperationTransformer<ConduitLLM.Admin.OpenApi.DefaultErrorResponsesOperationTransformer>();
            options.AddOperationTransformer<ConduitLLM.Admin.OpenApi.ResponseContractOperationTransformer>();
            options.AddSchemaTransformer<ConduitLLM.Admin.OpenApi.NumericSchemaTransformer>();
            options.AddSchemaTransformer<ConduitLLM.Admin.OpenApi.TemporalSchemaTransformer>();
            options.AddSchemaTransformer<ConduitLLM.Admin.OpenApi.ModelCostResponseSchemaTransformer>();
            options.AddSchemaTransformer<ConduitLLM.Admin.OpenApi.GlobalSettingsResponseSchemaTransformer>();
            options.AddSchemaTransformer<ConduitLLM.Admin.OpenApi.IpFilterResponseSchemaTransformer>();
            options.AddSchemaTransformer<ConduitLLM.Admin.OpenApi.StructuredJsonSchemaTransformer>();
            options.AddSchemaTransformer<ConduitLLM.Admin.OpenApi.FunctionExecutionSchemaTransformer>();
            options.AddDocumentTransformer<ConduitLLM.Core.OpenApi.OperationIdValidationDocumentTransformer>();
        });

        // The build-time exporter needs endpoint metadata, not infrastructure. Avoid Postgres,
        // Redis, messaging, migrations, and hosted services on this codegen-only path.
        if (Environment.GetEnvironmentVariable("CONDUIT_OPENAPI_GENERATION") == "true")
        {
            builder.Services.AddScoped<ConduitLLM.Configuration.Interfaces.IModelAuthorRepository,
                ConduitLLM.Configuration.Repositories.ModelAuthorRepository>();
            builder.Services.AddAuthorization(options =>
                options.AddPolicy("MasterKeyPolicy", policy => policy.RequireAssertion(_ => true)));
            var openApiApp = builder.Build();
            openApiApp.MapModelAuthorEndpoints();
            openApiApp.MapModelSeriesEndpoints();
            openApiApp.MapNotificationsEndpoints();
            openApiApp.MapAdminTasksEndpoints();
            openApiApp.MapAdminAuthEndpoints();
            openApiApp.MapSystemInfoEndpoints();
            openApiApp.MapAdminMetricsEndpoints();
            openApiApp.MapConfigurationEndpoints();
            openApiApp.MapBundledModelCatalogEndpoints();
            openApiApp.MapFunctionExecutionsEndpoints();
            openApiApp.MapFunctionCostsEndpoints();
            openApiApp.MapFunctionCredentialsEndpoints();
            openApiApp.MapBatchSpendingEndpoints();
            openApiApp.MapPromptCachingEndpoints();
            openApiApp.MapProviderSyncEndpoints();
            openApiApp.MapGlobalSettingsEndpoints();
            openApiApp.MapMediaEndpoints();
            ProviderCredentialsEndpoints.MapProviderCredentialsEndpoints(openApiApp);
            ModelEndpoints.MapModelEndpoints(openApiApp);
            VirtualKeyGroupsEndpoints.MapVirtualKeyGroupsEndpoints(openApiApp);
            VirtualKeysEndpoints.MapVirtualKeysEndpoints(openApiApp);
            IpFilterEndpoints.MapIpFilterEndpoints(openApiApp);
            openApiApp.MapHealthMonitoringEndpoints();
            AnalyticsEndpoints.MapAnalyticsEndpoints(openApiApp);
            FunctionConfigurationsEndpoints.MapFunctionConfigurationsEndpoints(openApiApp);
            ProviderErrorsEndpoints.MapProviderErrorsEndpoints(openApiApp);
            MediaRetentionEndpoints.MapMediaRetentionEndpoints(openApiApp);
            ProviderToolsEndpoints.MapProviderToolsEndpoints(openApiApp);
            PricingEndpoints.MapPricingEndpoints(openApiApp);
            ModelProviderMappingEndpoints.MapModelProviderMappingEndpoints(openApiApp);
            ModelCostsEndpoints.MapModelCostsEndpoints(openApiApp);

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

        // Configure services (partial class methods)
        ConfigureCoreServices(builder, startupLogger);
        ConfigureMessagingServices(builder, startupLogger);
        ConfigureMonitoringServices(builder, startupLogger);

        // Configure trusted-proxy forwarded-header processing so the client IP is derived
        // securely (spoof-resistant). No-op unless CONDUIT_TRUSTED_PROXY_ENABLED=true.
        builder.Services.AddTrustedProxyForwardedHeaders(builder.Configuration);

        var app = builder.Build();

        // Log deprecation warnings and validate Redis URL
        using (var scope = app.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            ConduitLLM.Configuration.Extensions.DeprecationWarnings.LogEnvironmentVariableDeprecations(logger);

            // Validate Redis URL if provided
            var envRedisUrl = Environment.GetEnvironmentVariable("REDIS_URL");
            if (!string.IsNullOrEmpty(envRedisUrl))
            {
                ConduitLLM.Configuration.Services.RedisUrlValidator.ValidateAndLog(envRedisUrl, logger, "Admin Service");
            }
        }

        // Normal startup never mutates the database. MigrationWaitService gates
        // readiness until the explicit "migrate" release step has completed.
        // Resolve the real client IP via trusted proxies. Must run before any IP-reading middleware:
        // HTTPS redirection honors X-Forwarded-Proto, and the /metrics gate reads the client IP.
        // No-op unless CONDUIT_TRUSTED_PROXY_ENABLED=true.
        app.UseTrustedProxyForwardedHeaders();

        // Configure the HTTP request pipeline
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi("/openapi/v1.json");
            app.MapScalarApiReference();
            app.Logger.LogInformation("Scalar UI available at /scalar/v1");
        }

        // Only use HTTPS redirection if explicitly enabled
        var enableHttpsRedirection = Environment.GetEnvironmentVariable("CONDUIT_ENABLE_HTTPS_REDIRECTION") != "false";
        if (enableHttpsRedirection)
        {
            app.UseHttpsRedirection();
        }

        // Add health endpoint authorization (early in pipeline, before authentication)
        app.UseHealthEndpointAuthorization();

        // Add middleware for authentication and request tracking
        app.UseAdminMiddleware();

        app.UseAuthentication();
        // Run before authorization so private-network scrapes are not captured by the
        // authenticated JSON API route at /metrics/.
        app.UseConduitPrometheusMetricsEndpoint();
        app.UseAuthorization();


        // ModelAuthor is served by Minimal API endpoints.
        app.MapModelAuthorEndpoints();
        app.MapModelSeriesEndpoints();
        app.MapNotificationsEndpoints();
        app.MapAdminTasksEndpoints();
        app.MapAdminAuthEndpoints();
        app.MapSystemInfoEndpoints();
        app.MapAdminMetricsEndpoints();
        app.MapConfigurationEndpoints();
        app.MapBundledModelCatalogEndpoints();
        app.MapFunctionExecutionsEndpoints();
        app.MapFunctionCostsEndpoints();
        app.MapFunctionCredentialsEndpoints();
        app.MapBatchSpendingEndpoints();
        app.MapPromptCachingEndpoints();
        app.MapProviderSyncEndpoints();
        app.MapGlobalSettingsEndpoints();
        app.MapMediaEndpoints();
        ProviderCredentialsEndpoints.MapProviderCredentialsEndpoints(app);
        ModelEndpoints.MapModelEndpoints(app);
        VirtualKeyGroupsEndpoints.MapVirtualKeyGroupsEndpoints(app);
        VirtualKeysEndpoints.MapVirtualKeysEndpoints(app);
        IpFilterEndpoints.MapIpFilterEndpoints(app);
        app.MapHealthMonitoringEndpoints();
        AnalyticsEndpoints.MapAnalyticsEndpoints(app);
        FunctionConfigurationsEndpoints.MapFunctionConfigurationsEndpoints(app);
        ProviderErrorsEndpoints.MapProviderErrorsEndpoints(app);
        MediaRetentionEndpoints.MapMediaRetentionEndpoints(app);
        ProviderToolsEndpoints.MapProviderToolsEndpoints(app);
        PricingEndpoints.MapPricingEndpoints(app);
        ModelProviderMappingEndpoints.MapModelProviderMappingEndpoints(app);
        ModelCostsEndpoints.MapModelCostsEndpoints(app);

        // Map monitoring endpoints (health, metrics, Prometheus)
        MapMonitoringEndpoints(app);

        // app.Urls throws when no server is present (e.g. under build-time OpenAPI
        // document generation, which builds the host without Kestrel) — read the
        // address feature null-safely instead.
        var serverAddresses = ((Microsoft.AspNetCore.Builder.IApplicationBuilder)app).ServerFeatures
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
        app.Logger.LogInformation(
            "Admin API started — Environment: {Environment}, URLs: {Urls}",
            app.Environment.EnvironmentName,
            string.Join(", ", serverAddresses ?? Array.Empty<string>()));

#if CONDUIT_WOLVERINE_CODEGEN
        // Generation-only command path, enabled by scripts/generate-wolverine-code.ps1.
        return await app.RunJasperFxCommands(args);
#else
        await app.RunAsync();
        return 0;
#endif
    }

}

// Make Program accessible for testing
public partial class Program { }
