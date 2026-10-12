using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Gateway.OpenApi;

/// <summary>Registers named content alternatives and documents provider extensions on the Conduit route.</summary>
public sealed class ProviderChatDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null || !schemas.TryGetValue("Message", out var message) ||
            !schemas.TryGetValue("ChatCompletionRequest", out var request) ||
            !document.Paths.TryGetValue("/v1/conduit/chat/completions", out var path))
            return Task.CompletedTask;

        message.Properties!["content"] = ChatUnionSchemas.Content(document);
        var providerMessage = CopyObject(message);
        providerMessage.Properties!["content"] = ChatUnionSchemas.Content(document, providerExtensions: true);
        schemas["ProviderChatMessage"] = providerMessage;
        var providerRequest = CopyObject(request);
        providerRequest.Properties!["messages"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            Items = new OpenApiSchemaReference("ProviderChatMessage", document)
        };
        schemas["ProviderChatCompletionRequest"] = providerRequest;
        path.Operations![HttpMethod.Post].RequestBody!.Content!["application/json"].Schema =
            new OpenApiSchemaReference("ProviderChatCompletionRequest", document);
        return Task.CompletedTask;
    }

    private static OpenApiSchema CopyObject(IOpenApiSchema schema) => new()
    {
        Type = schema.Type,
        Description = schema.Description,
        Required = schema.Required?.ToHashSet(),
        Properties = new Dictionary<string, IOpenApiSchema>(schema.Properties!),
        AdditionalPropertiesAllowed = schema.AdditionalPropertiesAllowed,
        AdditionalProperties = schema.AdditionalProperties
    };
}
