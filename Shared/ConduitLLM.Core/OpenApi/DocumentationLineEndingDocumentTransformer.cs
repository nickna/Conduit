using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Core.OpenApi;

/// <summary>Canonicalizes documentation copied from platform-specific XML comments.</summary>
public sealed class DocumentationLineEndingDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        Normalize(document);
        return Task.CompletedTask;
    }

    public static void Normalize(OpenApiDocument document) =>
        new OpenApiWalker(new DocumentationVisitor()).Walk(document);

    // Normalize documentation fields only. Example payloads, defaults, patterns,
    // and extension values may intentionally contain CRLF and must stay unchanged.
    private sealed class DocumentationVisitor : OpenApiVisitorBase
    {
        private static string? Canonical(string? text) => text?.Replace("\r\n", "\n").Replace('\r', '\n');

        public override void Visit(IOpenApiReferenceHolder holder)
        {
            // Wrapper getters can fall back to target documentation. Normalize only
            // metadata owned by the reference, preserving plain $ref objects.
            OpenApiReferenceWithDescription? reference = holder switch
            {
                IOpenApiReferenceHolder<JsonSchemaReference> schema => schema.Reference,
                IOpenApiReferenceHolder<OpenApiReferenceWithDescriptionAndSummary> summarized => summarized.Reference,
                IOpenApiReferenceHolder<OpenApiReferenceWithDescription> described => described.Reference,
                _ => null
            };

            if (reference is not null)
                reference.Description = Canonical(reference.Description);
            if (reference is OpenApiReferenceWithDescriptionAndSummary summary)
                summary.Summary = Canonical(summary.Summary);
        }

        public override void Visit(OpenApiInfo info)
        {
            info.Summary = Canonical(info.Summary);
            info.Description = Canonical(info.Description);
        }

        public override void Visit(OpenApiOperation operation)
        {
            operation.Summary = Canonical(operation.Summary);
            operation.Description = Canonical(operation.Description);
        }

        public override void Visit(IOpenApiPathItem pathItem)
        {
            if (pathItem is OpenApiPathItem item)
            {
                item.Summary = Canonical(item.Summary);
                item.Description = Canonical(item.Description);
            }
        }

        public override void Visit(IOpenApiSchema schema)
        {
            if (schema is OpenApiSchema item)
                item.Description = Canonical(item.Description);
        }

        public override void Visit(IOpenApiParameter parameter)
        {
            if (parameter is OpenApiParameter item)
                item.Description = Canonical(item.Description);
        }

        public override void Visit(IOpenApiRequestBody requestBody)
        {
            if (requestBody is OpenApiRequestBody item)
                item.Description = Canonical(item.Description);
        }

        public override void Visit(IOpenApiResponse response)
        {
            if (response is OpenApiResponse item)
                item.Description = Canonical(item.Description);
        }

        public override void Visit(IOpenApiHeader header)
        {
            if (header is OpenApiHeader item)
                item.Description = Canonical(item.Description);
        }

        public override void Visit(IOpenApiExample example)
        {
            if (example is OpenApiExample item)
            {
                item.Summary = Canonical(item.Summary);
                item.Description = Canonical(item.Description);
            }
        }

        public override void Visit(IOpenApiLink link)
        {
            if (link is OpenApiLink item)
                item.Description = Canonical(item.Description);
        }

        public override void Visit(IOpenApiSecurityScheme securityScheme)
        {
            if (securityScheme is OpenApiSecurityScheme item)
                item.Description = Canonical(item.Description);
        }

        public override void Visit(OpenApiTag tag) => tag.Description = Canonical(tag.Description);
        public override void Visit(OpenApiExternalDocs docs) => docs.Description = Canonical(docs.Description);
        public override void Visit(OpenApiServer server) => server.Description = Canonical(server.Description);
        public override void Visit(OpenApiServerVariable variable) => variable.Description = Canonical(variable.Description);
    }
}
