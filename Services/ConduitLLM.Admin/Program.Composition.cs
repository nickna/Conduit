using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Security.Extensions;

namespace ConduitLLM.Admin;

public partial class Program
{
    /// <summary>Registers Admin endpoint contracts for runtime and OpenAPI export.</summary>
    internal static void ConfigureHttpServices(WebApplicationBuilder builder)
    {
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
            options.AddDocumentTransformer<ConduitLLM.Core.OpenApi.DocumentationLineEndingDocumentTransformer>();
        });
    }

    /// <summary>Registers the complete normal Admin host graph.</summary>
    internal static void ConfigureRuntimeServices(WebApplicationBuilder builder, ILogger startupLogger)
    {
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        ConfigureCoreServices(builder, startupLogger);
        ConfigureMessagingServices(builder, startupLogger);
        ConfigureMonitoringServices(builder, startupLogger);
        builder.Services.AddTrustedProxyForwardedHeaders(builder.Configuration);
    }
}
