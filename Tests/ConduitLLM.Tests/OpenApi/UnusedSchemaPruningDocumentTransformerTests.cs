using System.Net.Http;

using ConduitLLM.Core.OpenApi;

using AwesomeAssertions;

using Microsoft.OpenApi;

namespace ConduitLLM.Tests.OpenApi;

[Trait("Category", "Unit")]
[Trait("Component", "OpenApi")]
public sealed class UnusedSchemaPruningDocumentTransformerTests
{
    [Fact]
    public void Prune_RemovesSchemasNoOperationCanReach()
    {
        var document = DocumentWithSchemas();
        document.Components!.Schemas!["Response"] = ObjectWith("thing", Reference(document, "Thing"));
        document.Components.Schemas["Thing"] = ObjectWith("child", Reference(document, "Child"));
        document.Components.Schemas["Child"] = new OpenApiSchema { Type = JsonSchemaType.String };
        document.Components.Schemas["Orphan"] = ObjectWith("child", Reference(document, "OrphanChild"));
        document.Components.Schemas["OrphanChild"] = new OpenApiSchema { Type = JsonSchemaType.String };
        document.Paths = new OpenApiPaths { ["/things"] = PathReturning(Reference(document, "Response")) };

        UnusedSchemaPruningDocumentTransformer.Prune(document);

        document.Components.Schemas.Keys.Should().BeEquivalentTo("Response", "Thing", "Child");
    }

    [Fact]
    public void Prune_KeepsSelfReferencingSchemasAndReusableComponentBodies()
    {
        var document = DocumentWithSchemas();
        document.Components!.Schemas!["Node"] = ObjectWith("parent", Reference(document, "Node"));
        document.Components.Schemas["SharedError"] = new OpenApiSchema { Type = JsonSchemaType.Object };
        document.Components.Responses = new Dictionary<string, IOpenApiResponse>
        {
            ["Error"] = ResponseReturning(Reference(document, "SharedError"))
        };
        document.Paths = new OpenApiPaths { ["/nodes"] = PathReturning(Reference(document, "Node")) };

        UnusedSchemaPruningDocumentTransformer.Prune(document);

        document.Components.Schemas.Keys.Should().BeEquivalentTo("Node", "SharedError");
    }

    private static OpenApiDocument DocumentWithSchemas() => new()
    {
        Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema>() }
    };

    private static OpenApiSchemaReference Reference(OpenApiDocument document, string schemaName) =>
        new(schemaName, document);

    private static OpenApiSchema ObjectWith(string propertyName, IOpenApiSchema property) => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema> { [propertyName] = property }
    };

    private static OpenApiPathItem PathReturning(IOpenApiSchema schema) => new()
    {
        Operations = new Dictionary<HttpMethod, OpenApiOperation>
        {
            [HttpMethod.Get] = new()
            {
                Responses = new OpenApiResponses { ["200"] = ResponseReturning(schema) }
            }
        }
    };

    private static OpenApiResponse ResponseReturning(IOpenApiSchema schema) => new()
    {
        Content = new Dictionary<string, OpenApiMediaType>
        {
            ["application/json"] = new() { Schema = schema }
        }
    };
}
