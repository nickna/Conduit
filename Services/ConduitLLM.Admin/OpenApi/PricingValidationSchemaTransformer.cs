using ConduitLLM.Core.Services;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Admin.OpenApi;

/// <summary>Preserves nullable validation collections from the published Admin contract.</summary>
public sealed class PricingValidationSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(PricingRulesValidationResult))
        {
            foreach (var name in new[] { "errors", "warnings" })
                if (schema.Properties?.TryGetValue(name, out var property) == true && property is OpenApiSchema collection)
                    collection.Type |= JsonSchemaType.Null;
        }
        return Task.CompletedTask;
    }
}
