using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Serialization;
using ConduitLLM.Providers.OpenAI;
using ConduitLLM.Providers.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

using CoreEmbeddingRequest = ConduitLLM.Core.Models.EmbeddingRequest;

namespace ConduitLLM.SerializationTests;

public sealed class EmbeddingSerializationTests
{
    [Theory]
    [InlineData("float", false)]
    [InlineData("base64", false)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task ProviderHttp_PreservesEmbeddingRepresentationAndForwardsRequestedEncoding(
        string? encoding, bool useRequestDefault)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var expectedEncoding = useRequestDefault ? "float" : encoding;
        var base64 = expectedEncoding == "base64";
        using var handler = new EmbeddingHandler(ResponseJson(base64 ? "\"zczMPQ==\"" : "[0.1]"));
        var request = new CoreEmbeddingRequest
        {
            Model = "request-embedding", Input = "hello", Dimensions = 1, User = "fixture-user"
        };
        if (!useRequestDefault) request.EncodingFormat = encoding;

        var response = await CreateClient(handler).CreateEmbeddingAsync(request);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/v1/embeddings", handler.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer fixture-key", handler.Authorization);
        using var submitted = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("request-embedding", submitted.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", submitted.RootElement.GetProperty("input").GetString());
        Assert.Equal(1, submitted.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal("fixture-user", submitted.RootElement.GetProperty("user").GetString());
        if (expectedEncoding is null)
            Assert.False(submitted.RootElement.TryGetProperty("encoding_format", out _));
        else
            Assert.Equal(expectedEncoding, submitted.RootElement.GetProperty("encoding_format").GetString());

        Assert.Equal("list", response.Object);
        Assert.Equal("provider-embedding", response.Model);
        var item = Assert.Single(response.Data);
        Assert.Equal("embedding", item.Object);
        Assert.Equal(3, item.Index);
        AssertValue(item.Embedding, base64);
        Assert.Equal(2, response.Usage.PromptTokens);
        Assert.Equal(0, response.Usage.CompletionTokens);
        Assert.Equal(2, response.Usage.TotalTokens);
        using var gatewayJson = JsonDocument.Parse(JsonSerializer.Serialize(response, GatewayJsonOptions.Create()));
        AssertWireValue(gatewayJson.RootElement.GetProperty("data")[0].GetProperty("embedding"), base64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedCoreGatewayAndProviderResponses_RoundTripBothEmbeddingRepresentations(bool base64)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var json = ResponseJson(base64 ? "\"zczMPQ==\"" : "[0.1]");
        var core = JsonSerializer.Deserialize(json, CoreHttpJsonContext.Default.EmbeddingResponse)!;
        var gateway = JsonSerializer.Deserialize(json, GatewayHttpJsonContext.Default.EmbeddingResponse)!;
        var provider = JsonSerializer.Deserialize(json, ProvidersJsonContext.Default.EmbeddingResponse)!;
        AssertValue(Assert.Single(core.Data).Embedding, base64);
        AssertValue(Assert.Single(gateway.Data).Embedding, base64);
        AssertValue(Assert.Single(provider.Data).Embedding, base64);
        var serialized = new[]
        {
            JsonSerializer.Serialize(core, CoreHttpJsonContext.Default.EmbeddingResponse),
            JsonSerializer.Serialize(gateway, GatewayHttpJsonContext.Default.EmbeddingResponse),
            JsonSerializer.Serialize(provider, ProvidersJsonContext.Default.EmbeddingResponse)
        };
        foreach (var roundTrip in serialized)
        {
            using var document = JsonDocument.Parse(roundTrip);
            AssertWireValue(document.RootElement.GetProperty("data")[0].GetProperty("embedding"), base64);
            Assert.Equal(3, document.RootElement.GetProperty("data")[0].GetProperty("index").GetInt32());
            Assert.Equal("provider-embedding", document.RootElement.GetProperty("model").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("123")]
    [InlineData("[0.1,\"0.2\"]")]
    [InlineData("[1e100]")]
    [InlineData("\"not-base64\"")]
    [InlineData("\"AQ==\"")]
    public async Task ProviderHttp_RejectsMalformedEmbeddingInsteadOfReturningASuccessfulResponse(string embedding)
    {
        using var handler = new EmbeddingHandler(ResponseJson(embedding));

        var exception = await Assert.ThrowsAsync<LLMCommunicationException>(() => CreateClient(handler)
            .CreateEmbeddingAsync(new CoreEmbeddingRequest { Model = "request-embedding", Input = "hello" }));

        Assert.IsType<JsonException>(exception.InnerException);
        Assert.NotNull(handler.RequestBody);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("[0.1,null]")]
    [InlineData("\"AQ==\"")]
    public void GeneratedResponseContexts_RejectMalformedEmbedding(string embedding)
    {
        var json = ResponseJson(embedding);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, CoreHttpJsonContext.Default.EmbeddingResponse));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, GatewayHttpJsonContext.Default.EmbeddingResponse));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, ProvidersJsonContext.Default.EmbeddingResponse));
    }

    private static void AssertValue(EmbeddingValue embedding, bool base64)
    {
        Assert.Equal(base64, embedding.IsBase64);
        if (base64)
        {
            Assert.Equal("zczMPQ==", embedding.Base64);
            Assert.Null(embedding.Floats);
        }
        else
        {
            Assert.Equal(new[] { 0.1f }, embedding.Floats);
            Assert.Null(embedding.Base64);
        }
    }

    private static void AssertWireValue(JsonElement embedding, bool base64)
    {
        if (base64)
        {
            Assert.Equal(JsonValueKind.String, embedding.ValueKind);
            Assert.Equal("zczMPQ==", embedding.GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Array, embedding.ValueKind);
            Assert.Equal(0.1f, Assert.Single(embedding.EnumerateArray()).GetSingle());
        }
    }

    private static string ResponseJson(string embedding) => $$$"""
        {"object":"list","data":[{"object":"embedding","index":3,"embedding":{{{embedding}}}}],"model":"provider-embedding","usage":{"prompt_tokens":2,"total_tokens":2}}
        """;

    private static OpenAIClient CreateClient(HttpMessageHandler handler) => new(
        new Provider { Id = 1, ProviderType = ProviderType.OpenAI, BaseUrl = "https://provider.invalid/v1" },
        new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "fixture-key" },
        "configured-embedding", NullLogger<OpenAIClient>.Instance, new ClientFactory(handler));

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class EmbeddingHandler(string response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
