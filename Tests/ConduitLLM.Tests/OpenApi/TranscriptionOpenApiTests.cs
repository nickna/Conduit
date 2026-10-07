using System.Text.Json;
using ConduitLLM.Gateway.OpenApi;
using ConduitLLM.Tests.Gateway.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Tests.OpenApi;

[Trait("Category", "Integration")]
[Trait("Component", "OpenApi")]
public sealed class TranscriptionOpenApiTests
{
    [Fact]
    public async Task FreshGatewayDocument_PreservesNamedMultipartFieldsAndRequiredValues()
    {
        await using var host = await GatewayEndpointTestHost.StartAsync(services =>
        {
            services.AddEndpointsApiExplorer();
            services.AddOpenApi("v1", options =>
                options.AddOperationTransformer<ResponseContractOperationTransformer>());
        }, exposeOpenApi: true);

        using var response = await host.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schema = document.RootElement.GetProperty("paths")
            .GetProperty("/v1/audio/transcriptions").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data").GetProperty("schema");

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("allOf", out _));
        Assert.Equal(["file", "model"], schema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).Order().ToArray());
        var properties = schema.GetProperty("properties");
        Assert.Equal([
            "chunking_strategy", "file", "include", "known_speaker_names",
            "known_speaker_references", "language", "model", "prompt", "response_format",
            "stream", "temperature", "timestamp_granularities"
        ], properties.EnumerateObject().Select(property => property.Name).Order().ToArray());
        foreach (var field in new[] { "model", "language", "prompt", "response_format", "chunking_strategy" })
            Assert.Equal("string", properties.GetProperty(field).GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("stream").GetProperty("type").GetString());
        Assert.Equal("double", properties.GetProperty("temperature").GetProperty("format").GetString());
        Assert.Equal("binary", properties.GetProperty("known_speaker_references")
            .GetProperty("items").GetProperty("format").GetString());
    }
}
