using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;

namespace ConduitLLM.Admin.OpenApi;

/// <summary>Publishes one envelope and query-parameter convention for collection reads.</summary>
public sealed class CollectionPaginationOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Description.HttpMethod != "GET" ||
            operation.Description?.Contains("intentionally not a paged collection", StringComparison.Ordinal) == true ||
            operation.Responses is null ||
            !operation.Responses.TryGetValue("200", out var success) ||
            success is not OpenApiResponse response ||
            response.Content is null ||
            !response.Content.TryGetValue("application/json", out var mediaType) ||
            mediaType.Schema is not OpenApiSchema { Type: JsonSchemaType.Array } array)
        {
            return Task.CompletedTask;
        }

        var responseMedia = ConduitLLM.Core.OpenApi.OpenApiMediaTypeNormalizer.CopyForMutation(mediaType);
        response.Content["application/json"] = responseMedia;
        responseMedia.Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["data"] = array,
                ["pagination"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["page"] = Integer(),
                        ["pageSize"] = Integer(),
                        ["totalItems"] = Integer(),
                        ["totalPages"] = Integer()
                    },
                    Required = new HashSet<string> { "page", "pageSize", "totalItems", "totalPages" }
                }
            },
            Required = new HashSet<string> { "data", "pagination" }
        };

        operation.Parameters ??= [];
        AddQueryParameter(operation, "page", "1-based page number.", 1);
        AddQueryParameter(operation, "pageSize", "Items per page (maximum 100).", 50);
        return Task.CompletedTask;
    }

    private static OpenApiSchema Integer() => new()
    {
        Type = JsonSchemaType.Integer,
        Format = "int32"
    };

    private static void AddQueryParameter(OpenApiOperation operation, string name, string description, int defaultValue)
    {
        operation.Parameters ??= [];
        if (operation.Parameters.OfType<OpenApiParameter>().Any(parameter => parameter.Name == name))
            return;

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Description = description,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int32",
                Default = JsonNode.Parse(defaultValue.ToString())
            }
        });
    }
}
