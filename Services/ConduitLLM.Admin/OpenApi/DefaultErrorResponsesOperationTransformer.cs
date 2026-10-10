using ConduitLLM.Admin.DTOs;
using ConduitLLM.Core.Models;
using ConduitLLM.Security.Serialization;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Admin.OpenApi;

/// <summary>Documents endpoint problem details, middleware security failures, rate limits, and request IDs.</summary>
public sealed class DefaultErrorResponsesOperationTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Responses ??= new OpenApiResponses();
        // These failures originate in SecurityService before endpoint execution.
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "The master key is missing, invalid, or expired." });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Access was denied by an authorization or IP access policy." });
        operation.Responses.TryAdd("429", new OpenApiResponse { Description = "The request exceeded an applicable rate limit." });
        operation.Responses.TryAdd("500", new OpenApiResponse { Description = "Internal Server Error" });
        if (operation.RequestBody is not null || operation.Parameters?.Count > 0)
            operation.Responses.TryAdd("400", new OpenApiResponse { Description = "Invalid request parameters or body. Parameter binding may return an empty body." });
        if (operation.RequestBody is not null)
            operation.Responses.TryAdd("415", new OpenApiResponse { Description = "The request Content-Type is not supported." });

        var schema = await context.GetOrCreateSchemaAsync(
            typeof(AdminProblemDetails),
            parameterDescription: null,
            cancellationToken);
        var document = context.Document ?? throw new InvalidOperationException("An OpenAPI document is required.");
        var components = document.Components ??= new OpenApiComponents();
        components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        components.Schemas["AdminProblemDetails"] = schema;
        var problemReference = new OpenApiSchemaReference("AdminProblemDetails", document);
        var securitySchema = await context.GetOrCreateSchemaAsync(typeof(SecurityErrorResponse), null, cancellationToken);
        components.Schemas["SecurityErrorResponse"] = securitySchema;
        var securityReference = new OpenApiSchemaReference("SecurityErrorResponse", document);
        var rateLimitSchema = await context.GetOrCreateSchemaAsync(typeof(OpenAIErrorResponse), null, cancellationToken);
        components.Schemas["OpenAIErrorResponse"] = rateLimitSchema;
        var rateLimitReference = new OpenApiSchemaReference("OpenAIErrorResponse", document);

        foreach (var responseEntry in operation.Responses)
        {
            if (responseEntry.Value is not OpenApiResponse response)
            {
                continue;
            }

            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["x-request-id"] = new OpenApiHeader
            {
                Description = "Request identifier for support and distributed tracing.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            };

            if (int.TryParse(responseEntry.Key, out var status) && status >= 400)
            {
                response.Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new() { Schema = problemReference }
                };
                if (status is 401 or 403)
                {
                    response.Content["application/json"] = new() { Schema = securityReference };
                    response.Description += " Security middleware returns application/json; endpoint errors use application/problem+json. Authorization may return an empty body.";
                }
                if (status == 429)
                {
                    response.Content["application/json"] = new() { Schema = rateLimitReference };
                    response.Headers["Retry-After"] = new OpenApiHeader
                    {
                        Description = "Minimum delay in seconds before retrying.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" }
                    };
                    response.Headers["X-RateLimit-Limit"] = new OpenApiHeader
                    {
                        Description = "Maximum requests in the applicable rate-limit window.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer }
                    };
                    response.Headers["X-RateLimit-Remaining"] = new OpenApiHeader
                    {
                        Description = "Requests remaining in the applicable window.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "0" }
                    };
                    response.Headers["X-RateLimit-Reset"] = new OpenApiHeader
                    {
                        Description = "UTC Unix timestamp in seconds when the window resets.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64" }
                    };
                    response.Headers["X-RateLimit-Scope"] = new OpenApiHeader
                    {
                        Description = "Scope of the exceeded limit (for example ip).",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    };
                }
            }
        }
    }
}
