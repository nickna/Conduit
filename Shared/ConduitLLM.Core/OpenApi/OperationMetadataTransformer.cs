using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Core.OpenApi;

/// <summary>Assigns stable operation IDs, tags, and explicitly supplied parameter documentation.</summary>
public sealed class OperationMetadataTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var routeValues = context.Description.ActionDescriptor.RouteValues;
        routeValues.TryGetValue("controller", out var controller);
        routeValues.TryGetValue("action", out var action);

        if (string.IsNullOrWhiteSpace(operation.OperationId) &&
            !string.IsNullOrWhiteSpace(controller) &&
            !string.IsNullOrWhiteSpace(action))
        {
            operation.OperationId = $"{controller}_{action}";
        }

        if ((operation.Tags is null || operation.Tags.Count == 0) &&
            !string.IsNullOrWhiteSpace(controller))
        {
            operation.Tags = new HashSet<OpenApiTagReference>
            {
                new(controller, context.Document, null)
            };
        }

        foreach (var documentation in context.Description.ActionDescriptor.EndpointMetadata
            .OfType<EndpointParameterDescription>())
        {
            foreach (var parameter in (operation.Parameters?.OfType<OpenApiParameter>() ?? [])
                .Where(parameter => string.Equals(parameter.Name, documentation.Name, StringComparison.Ordinal)))
            {
                parameter.Description = documentation.Description;
            }
        }

        return Task.CompletedTask;
    }
}
