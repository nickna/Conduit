using System.Text.Json;

using ConduitLLM.Gateway.OpenApi;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Tests.Gateway.Endpoints;

using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Tests.OpenApi;

[Trait("Category", "Integration")]
[Trait("Component", "OpenApi")]
public sealed class EmbeddingValueOpenApiTests
{
    [Fact]
    public async Task FreshDocument_DescribesNumericAndBase64EmbeddingResponses()
    {
        await using var host = await GatewayEndpointTestHost.StartAsync(services =>
        {
            services.ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions));
            services.AddEndpointsApiExplorer();
            services.AddOpenApi("v1", options => options.AddSchemaTransformer<StructuredJsonSchemaTransformer>());
        }, exposeOpenApi: true);
        using var response = await host.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var embedding = schemas.GetProperty("EmbeddingData").GetProperty("properties").GetProperty("embedding");
        if (embedding.TryGetProperty("$ref", out var reference))
            embedding = schemas.GetProperty(reference.GetString()!.Split('/')[^1]);

        var alternatives = embedding.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(2, alternatives.Length);
        var numeric = Assert.Single(alternatives, schema => schema.GetProperty("type").GetString() == "array");
        Assert.Equal("number", numeric.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("float", numeric.GetProperty("items").GetProperty("format").GetString());
        var encoded = Assert.Single(alternatives, schema => schema.GetProperty("type").GetString() == "string");
        Assert.Equal("byte", encoded.GetProperty("format").GetString());
        Assert.False(embedding.TryGetProperty("properties", out _));
    }
}
