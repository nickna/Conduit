using System.Net.Http;
using System.Text.Json.Nodes;
using ConduitLLM.Core.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Tests.OpenApi;

[Trait("Category", "Unit")]
[Trait("Component", "OpenApi")]
public sealed class DocumentationLineEndingDocumentTransformerTests
{
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    public async Task Normalize_ProducesIdenticalJsonForPlatformDocumentationLineEndings(string newline)
    {
        var document = CreateDocument(newline);
        var expected = CreateDocument("\n");

        DocumentationLineEndingDocumentTransformer.Normalize(document);

        Assert.Equal(await Serialize(expected), await Serialize(document));
        // A second pass must remain stable, including recursive schema references.
        DocumentationLineEndingDocumentTransformer.Normalize(document);
        Assert.Equal(await Serialize(expected), await Serialize(document));
    }

    [Fact]
    public void Normalize_PreservesLiteralPayloadsDefaultsAndPatterns()
    {
        var document = CreateDocument("\r\n");
        var schema = Assert.IsType<OpenApiSchema>(document.Components!.Schemas!["Node"]);
        var example = Assert.IsType<OpenApiExample>(document.Components.Examples!["Payload"]);

        DocumentationLineEndingDocumentTransformer.Normalize(document);

        Assert.Equal("literal\r\nvalue", schema.Default!.GetValue<string>());
        Assert.Equal("literal\r\nvalue", schema.Example!.GetValue<string>());
        Assert.Equal("literal\r\nvalue", schema.Examples![0].GetValue<string>());
        Assert.Equal("literal\r\nvalue", schema.Pattern);
        Assert.Equal("literal\r\nvalue", example.Value!["description"]!.GetValue<string>());
        Assert.Equal("line one\n  line two", schema.Description);
        Assert.Equal("line one\n  line two", example.Description);
    }

    [Fact]
    public async Task Normalize_NormalizesOwnReferenceMetadataAndPreservesPlainReferences()
    {
        var document = CreateDocument("\r\n");
        var schema = Assert.IsType<OpenApiSchemaReference>(document.Components!.Schemas!["NodeOverride"]);

        DocumentationLineEndingDocumentTransformer.Normalize(document);

        var json = JsonNode.Parse(await Serialize(document))!;
        foreach (var (section, plain, overridden) in new[]
        {
            ("schemas", "PlainNode", "NodeOverride"),
            ("parameters", "PlainQuery", "QueryOverride"),
            ("responses", "PlainResult", "ResultOverride"),
            ("examples", "PlainPayload", "PayloadOverride")
        })
        {
            var plainReference = json["components"]![section]![plain]!.AsObject();
            Assert.Single(plainReference);
            Assert.True(plainReference.ContainsKey("$ref"));
            Assert.Equal("line one\n  line two", json["components"]![section]![overridden]!["description"]!.GetValue<string>());
        }

        Assert.Equal("line one\n  line two", json["components"]!["examples"]!["PayloadOverride"]!["summary"]!.GetValue<string>());
        Assert.Equal("literal\r\nvalue", schema.Reference.Default!.GetValue<string>());
        Assert.Equal("literal\r\nvalue", schema.Reference.Examples![0].GetValue<string>());
    }

    private static OpenApiDocument CreateDocument(string newline)
    {
        var documentation = $"line one{newline}  line two";
        var document = new OpenApiDocument
        {
            Info = new() { Title = "Fixture", Version = "v1", Summary = documentation, Description = documentation },
            ExternalDocs = new() { Description = documentation, Url = new Uri("https://example.com/docs") },
            Tags = new HashSet<OpenApiTag> { new() { Name = "Nodes", Description = documentation } },
            Servers = [new()
            {
                Url = "https://{host}", Description = documentation,
                Variables = new Dictionary<string, OpenApiServerVariable>
                {
                    ["host"] = new() { Default = "example.com", Description = documentation }
                }
            }],
            Components = new()
            {
                Schemas = new Dictionary<string, IOpenApiSchema>(),
                Examples = new Dictionary<string, IOpenApiExample>
                {
                    ["Payload"] = new OpenApiExample
                    {
                        Summary = documentation, Description = documentation,
                        Value = new JsonObject { ["description"] = "literal\r\nvalue" }
                    }
                },
                Parameters = new Dictionary<string, IOpenApiParameter>
                {
                    ["Query"] = new OpenApiParameter
                    {
                        Name = "query", In = ParameterLocation.Query, Description = documentation,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    }
                },
                Responses = new Dictionary<string, IOpenApiResponse>
                {
                    ["Result"] = new OpenApiResponse { Description = documentation }
                },
                SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["Key"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey, Name = "X-Key",
                        In = ParameterLocation.Header, Description = documentation
                    }
                }
            }
        };
        document.Components.Schemas["Node"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object, Description = documentation,
            Default = JsonValue.Create("literal\r\nvalue"),
            Example = JsonValue.Create("literal\r\nvalue"),
            Examples = [JsonValue.Create("literal\r\nvalue")],
            Pattern = "literal\r\nvalue",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["child"] = new OpenApiSchemaReference("Node", document),
                ["name"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = documentation }
            }
        };
        document.Components.Schemas["PlainNode"] = new OpenApiSchemaReference("Node", document);
        document.Components.Schemas["NodeOverride"] = new OpenApiSchemaReference("Node", document)
        {
            Description = documentation,
            Default = JsonValue.Create("literal\r\nvalue"),
            Examples = [JsonValue.Create("literal\r\nvalue")]
        };
        document.Components.Parameters["PlainQuery"] = new OpenApiParameterReference("Query", document);
        document.Components.Parameters["QueryOverride"] = new OpenApiParameterReference("Query", document) { Description = documentation };
        document.Components.Responses["PlainResult"] = new OpenApiResponseReference("Result", document);
        document.Components.Responses["ResultOverride"] = new OpenApiResponseReference("Result", document) { Description = documentation };
        document.Components.Examples["PlainPayload"] = new OpenApiExampleReference("Payload", document);
        document.Components.Examples["PayloadOverride"] = new OpenApiExampleReference("Payload", document)
        {
            Description = documentation, Summary = documentation
        };
        document.Paths = new OpenApiPaths
        {
            ["/nodes"] = new OpenApiPathItem
            {
                Summary = documentation, Description = documentation,
                Operations = new Dictionary<HttpMethod, OpenApiOperation>
                {
                    [HttpMethod.Post] = new()
                    {
                        Summary = documentation, Description = documentation,
                        Parameters = [new OpenApiParameter
                        {
                            Name = "query", In = ParameterLocation.Query, Description = documentation,
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                        }],
                        RequestBody = new OpenApiRequestBody
                        {
                            Description = documentation,
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new() { Schema = new OpenApiSchemaReference("Node", document) }
                            }
                        },
                        Responses = new OpenApiResponses
                        {
                            ["200"] = new OpenApiResponse
                            {
                                Description = documentation,
                                Headers = new Dictionary<string, IOpenApiHeader>
                                {
                                    ["X-Result"] = new OpenApiHeader
                                    {
                                        Description = documentation,
                                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                                    }
                                },
                                Links = new Dictionary<string, IOpenApiLink>
                                {
                                    ["next"] = new OpenApiLink { OperationId = "Nodes_Post", Description = documentation }
                                }
                            }
                        }
                    }
                }
            }
        };
        return document;
    }

    private static async Task<byte[]> Serialize(OpenApiDocument document)
    {
        await using var output = new MemoryStream();
        await document.SerializeAsJsonAsync(output, OpenApiSpecVersion.OpenApi3_1, default);
        return output.ToArray();
    }
}
