using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Events;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Admin.OpenApi;

/// <summary>Retains the published JSON enum names for the existing enum query contracts.</summary>
public sealed class EnumQueryOperationTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
        {
            if (parameter.In != ParameterLocation.Query)
                continue;

            var type = context.Description.ParameterDescriptions
                .FirstOrDefault(description => description.Name == parameter.Name)?.Type;
            string name;
            IOpenApiSchema schema;
            if (type == typeof(FlushPriority) || type == typeof(FlushPriority?))
            {
                name = nameof(FlushPriority);
                schema = await context.GetOrCreateSchemaAsync(typeof(FlushPriority), null, cancellationToken);
            }
            else if (type == typeof(ProviderType) || type == typeof(ProviderType?))
            {
                name = nameof(ProviderType);
                schema = await context.GetOrCreateSchemaAsync(typeof(ProviderType), null, cancellationToken);
            }
            else
            {
                continue;
            }

            var document = context.Document
                ?? throw new InvalidOperationException("Enum query contracts require their owning OpenAPI document.");
            document.Components ??= new OpenApiComponents();
            document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
            if (schema is not OpenApiSchemaReference)
                document.Components.Schemas[name] = schema;

            var reference = new OpenApiSchemaReference(name, document);
            if (parameter.Schema?.Default is JsonValue value && value.TryGetValue<string>(out var defaultName))
                reference.Default = JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(defaultName));
            parameter.Schema = reference;
        }
    }
}
