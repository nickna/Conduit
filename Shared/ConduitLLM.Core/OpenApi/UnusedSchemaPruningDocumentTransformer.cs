using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Core.OpenApi;

/// <summary>Drops component schemas that no published operation can reach.</summary>
public sealed class UnusedSchemaPruningDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        Prune(document);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes every component schema unreachable from the document's operations. The framework
    /// hoists a component for each type it walks, including types that later transformers inline
    /// or drop, and orphans make generated clients carry contract no route can return.
    /// </summary>
    public static void Prune(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null)
            return;

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(RootReferences(document));
        var references = new List<string>();
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!reachable.Add(name) || !schemas.TryGetValue(name, out var schema))
                continue;

            references.Clear();
            CollectSchema(schema, references);
            foreach (var reference in references)
                pending.Enqueue(reference);
        }

        foreach (var name in schemas.Keys.Where(name => !reachable.Contains(name)).ToList())
            schemas.Remove(name);
    }

    private static List<string> RootReferences(OpenApiDocument document)
    {
        var references = new List<string>();
        foreach (var pathItem in Values(document.Paths).Concat(Values(document.Webhooks)))
            CollectPathItem(pathItem, references);

        // Reusable components other than schemas are roots too: an operation reaches their
        // schemas through a reference this walk deliberately does not follow.
        var components = document.Components;
        foreach (var pathItem in Values(components?.PathItems))
            CollectPathItem(pathItem, references);
        foreach (var parameter in Values(components?.Parameters))
            CollectParameter(parameter, references);
        foreach (var requestBody in Values(components?.RequestBodies))
            CollectContent(requestBody.Content, references);
        foreach (var response in Values(components?.Responses))
            CollectResponse(response, references);
        foreach (var header in Values(components?.Headers))
            CollectHeader(header, references);

        return references;
    }

    private static void CollectPathItem(IOpenApiPathItem pathItem, ICollection<string> sink)
    {
        foreach (var parameter in pathItem.Parameters ?? [])
            CollectParameter(parameter, sink);

        foreach (var operation in Values(pathItem.Operations))
        {
            foreach (var parameter in operation.Parameters ?? [])
                CollectParameter(parameter, sink);

            CollectContent(operation.RequestBody?.Content, sink);

            foreach (var response in Values(operation.Responses))
                CollectResponse(response, sink);

            foreach (var callback in Values(operation.Callbacks))
                foreach (var callbackPathItem in Values(callback.PathItems))
                    CollectPathItem(callbackPathItem, sink);
        }
    }

    private static void CollectParameter(IOpenApiParameter parameter, ICollection<string> sink)
    {
        CollectSchema(parameter.Schema, sink);
        CollectContent(parameter.Content, sink);
    }

    private static void CollectResponse(IOpenApiResponse response, ICollection<string> sink)
    {
        CollectContent(response.Content, sink);
        foreach (var header in Values(response.Headers))
            CollectHeader(header, sink);
    }

    private static void CollectHeader(IOpenApiHeader header, ICollection<string> sink)
    {
        CollectSchema(header.Schema, sink);
        CollectContent(header.Content, sink);
    }

    private static void CollectContent(IDictionary<string, OpenApiMediaType>? content, ICollection<string> sink)
    {
        foreach (var mediaType in Values(content))
        {
            CollectSchema(mediaType.Schema, sink);
            foreach (var encoding in Values(mediaType.Encoding))
                foreach (var header in Values(encoding.Headers))
                    CollectHeader(header, sink);
        }
    }

    private static void CollectSchema(IOpenApiSchema? schema, ICollection<string> sink)
    {
        if (schema is null)
            return;

        // A reference names its component and stops the walk: the component's own definition is
        // visited once, from the queue, so recursive schemas cannot loop here.
        if (schema is OpenApiSchemaReference reference)
        {
            if (reference.Reference?.Id is { Length: > 0 } referenceId)
                sink.Add(referenceId);
            return;
        }

        CollectSchema(schema.Items, sink);
        CollectSchema(schema.Not, sink);
        CollectSchema(schema.AdditionalProperties, sink);
        foreach (var composed in (schema.AllOf ?? []).Concat(schema.AnyOf ?? []).Concat(schema.OneOf ?? []))
            CollectSchema(composed, sink);
        foreach (var property in Values(schema.Properties).Concat(Values(schema.PatternProperties)))
            CollectSchema(property, sink);
        foreach (var mapped in Values(schema.Discriminator?.Mapping))
            CollectSchema(mapped, sink);
    }

    private static IEnumerable<TValue> Values<TKey, TValue>(IDictionary<TKey, TValue>? source) =>
        source?.Values ?? Enumerable.Empty<TValue>();
}
