using System.Text.Json;
using ConduitLLM.Gateway.OpenApi;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Tests.Gateway.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Tests.OpenApi;

[Trait("Category", "Integration")]
[Trait("Component", "OpenApi")]
public sealed class ChatUnionOpenApiTests
{
    [Fact]
    public async Task FreshDocument_SeparatesOpenAIContentAndProviderExtensions()
    {
        await using var host = await GatewayEndpointTestHost.StartAsync(services =>
        {
            services.ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions));
            services.AddEndpointsApiExplorer();
            services.AddOpenApi("v1", options =>
            {
                options.AddSchemaTransformer<StructuredJsonSchemaTransformer>();
                options.AddDocumentTransformer<ProviderChatDocumentTransformer>();
            });
        }, exposeOpenApi: true);
        using var response = await host.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var standard = schemas.GetProperty("Message").GetProperty("properties").GetProperty("content")
            .GetProperty("oneOf")[1].GetProperty("items");
        Assert.Equal("type", standard.GetProperty("discriminator").GetProperty("propertyName").GetString());
        Assert.Equal(5, standard.GetProperty("oneOf").GetArrayLength());
        Assert.Equal(["mp3", "wav"], schemas.GetProperty("ChatAudioContentPart").GetProperty("properties")
            .GetProperty("input_audio").GetProperty("properties").GetProperty("format").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).Order().ToArray());
        var provider = schemas.GetProperty("ProviderChatMessage").GetProperty("properties").GetProperty("content")
            .GetProperty("oneOf")[1].GetProperty("items").GetProperty("oneOf");
        Assert.Equal(6, provider[0].GetProperty("oneOf").GetArrayLength());
        Assert.Contains("flac", schemas.GetProperty("ProviderChatAudioContentPart").GetProperty("properties")
            .GetProperty("input_audio").GetProperty("properties").GetProperty("format").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["type"], provider[1].GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal(6, provider[1].GetProperty("properties").GetProperty("type").GetProperty("not").GetProperty("enum").GetArrayLength());
        Assert.Equal("#/components/schemas/ProviderChatCompletionRequest", document.RootElement.GetProperty("paths")
            .GetProperty("/v1/conduit/chat/completions").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
    }
}
