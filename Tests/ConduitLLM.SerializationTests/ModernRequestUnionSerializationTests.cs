using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Audio;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Providers.OpenAI;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class ModernRequestUnionSerializationTests
{
    [Theory]
    [InlineData("minimal")]
    [InlineData("policy")]
    [InlineData("policy-null")]
    [InlineData("input-null")]
    [InlineData("output-null")]
    [InlineData("null")]
    public async Task Moderation_CoreGatewayAndProviderHttpPreserveTheTypedObjectOrNull(string variant)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var (moderation, expected) = variant switch
        {
            "minimal" => ("""{"model":"omni-moderation-latest"}""", """{"model":"omni-moderation-latest"}"""),
            "policy" => ("""{"model":"omni-moderation-latest","policy":{"input":{"mode":"block"},"output":{"mode":"score"}}}""",
                """{"model":"omni-moderation-latest","policy":{"input":{"mode":"block"},"output":{"mode":"score"}}}"""),
            "policy-null" => ("""{"model":"omni-moderation-latest","policy":null}""", """{"model":"omni-moderation-latest"}"""),
            "input-null" => ("""{"model":"omni-moderation-latest","policy":{"input":null,"output":{"mode":"score"}}}""",
                """{"model":"omni-moderation-latest","policy":{"output":{"mode":"score"}}}"""),
            "output-null" => ("""{"model":"omni-moderation-latest","policy":{"input":{"mode":"block"},"output":null}}""",
                """{"model":"omni-moderation-latest","policy":{"input":{"mode":"block"}}}"""),
            _ => ("null", "null")
        };
        var json = ChatJson(moderation);
        var core = JsonSerializer.Deserialize(json, CoreHttpJsonContext.Default.ChatCompletionRequest)!;
        var gateway = JsonSerializer.Deserialize<ChatCompletionRequest>(json, GatewayJsonOptions.Create())!;
        foreach (var roundTrip in new[]
                 {
                     JsonSerializer.Serialize(core, CoreHttpJsonContext.Default.ChatCompletionRequest),
                     JsonSerializer.Serialize(gateway, GatewayJsonOptions.Create())
                 })
        {
            using var document = JsonDocument.Parse(roundTrip);
            AssertModeration(document.RootElement, expected, variant == "null");
        }

        using var handler = new RequestHandler(speech: false);
        var response = await CreateClient(handler).CreateChatCompletionAsync(gateway);

        Assert.Equal("ok", Assert.IsType<JsonElement>(Assert.Single(response.Choices).Message.Content).GetString());
        Assert.Equal(2, response.Usage!.TotalTokens);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/v1/chat/completions", handler.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer fixture-key", handler.Authorization);
        using var submitted = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("fixture-model", submitted.RootElement.GetProperty("model").GetString());
        AssertModeration(submitted.RootElement, expected, variant == "null");
    }

    [Theory]
    [InlineData("\"legacy-string\"")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("{\"model\":null}")]
    [InlineData("{\"model\":\"\"}")]
    [InlineData("{\"model\":7}")]
    [InlineData("{\"model\":\"omni-moderation-latest\",\"policy\":{\"input\":{}}}")]
    [InlineData("{\"model\":\"omni-moderation-latest\",\"policy\":{\"input\":{\"mode\":\"observe\"}}}")]
    [InlineData("{\"model\":\"omni-moderation-latest\",\"policy\":{\"input\":{\"mode\":\"Block\"}}}")]
    [InlineData("{\"model\":\"omni-moderation-latest\",\"policy\":{\"input\":{\"mode\":0}}}")]
    [InlineData("{\"model\":\"omni-moderation-latest\",\"policy\":{\"output\":{\"mode\":null}}}")]
    public void MalformedModeration_IsRejectedByGeneratedCoreAndGatewayRequestContracts(string moderation)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var json = ChatJson(moderation);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, CoreHttpJsonContext.Default.ChatCompletionRequest));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChatCompletionRequest>(json, GatewayJsonOptions.Create()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpeechVoice_CoreGatewayAndProviderHttpPreserveStringAndCustomId(bool custom)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var voice = custom ? """{"id":"voice_123"}""" : "\"alloy\"";
        var json = SpeechJson(voice);
        var core = JsonSerializer.Deserialize(json, CoreHttpJsonContext.Default.TextToSpeechRequest)!;
        var gateway = JsonSerializer.Deserialize<TextToSpeechRequest>(json, GatewayJsonOptions.Create())!;
        Assert.Equal(custom, core.Voice.IsCustom);
        Assert.Equal(custom ? "voice_123" : "alloy", core.Voice.Id);
        foreach (var roundTrip in new[]
                 {
                     JsonSerializer.Serialize(core, CoreHttpJsonContext.Default.TextToSpeechRequest),
                     JsonSerializer.Serialize(gateway, GatewayJsonOptions.Create())
                 })
        {
            using var document = JsonDocument.Parse(roundTrip);
            AssertJsonEqual(voice, document.RootElement.GetProperty("voice"));
        }

        using var handler = new RequestHandler(speech: true);
        var response = await CreateClient(handler).CreateSpeechAsync(gateway);

        Assert.Equal(RequestHandler.Audio, response.AudioData);
        Assert.Equal("audio/mpeg", response.ContentType);
        Assert.Equal("fixture-model", response.Model);
        Assert.Equal(5, response.Usage!.TtsCharacters);
        Assert.Equal("fixture-generation", response.Usage.Metadata!["generation_id"]);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/v1/audio/speech", handler.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer fixture-key", handler.Authorization);
        using var submitted = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("fixture-model", submitted.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", submitted.RootElement.GetProperty("input").GetString());
        AssertJsonEqual(voice, submitted.RootElement.GetProperty("voice"));
        Assert.Equal("mp3", submitted.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(1, submitted.RootElement.GetProperty("speed").GetDouble());
        Assert.Equal("fixture instructions", submitted.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("audio", submitted.RootElement.GetProperty("stream_format").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("\"\"")]
    [InlineData("{}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":123}")]
    public void MalformedSpeechVoice_IsRejectedByGeneratedCoreAndGatewayRequestContracts(string voice)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var json = SpeechJson(voice);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, CoreHttpJsonContext.Default.TextToSpeechRequest));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TextToSpeechRequest>(json, GatewayJsonOptions.Create()));
    }

    private static void AssertModeration(JsonElement request, string expected, bool omitted)
    {
        if (omitted)
            Assert.False(request.TryGetProperty("moderation", out _));
        else
            AssertJsonEqual(expected, request.GetProperty("moderation"));
    }

    private static void AssertJsonEqual(string expected, JsonElement actual)
    {
        using var document = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(document.RootElement, actual));
    }

    private static string ChatJson(string moderation) => $$$"""
        {"model":"fixture-model","messages":[{"role":"user","content":"hello"}],"moderation":{{{moderation}}}}
        """;

    private static string SpeechJson(string voice) => $$$"""
        {"model":"fixture-model","input":"hello","voice":{{{voice}}},"response_format":"mp3","speed":1,"instructions":"fixture instructions","stream_format":"audio"}
        """;

    private static OpenAIClient CreateClient(HttpMessageHandler handler) => new(
        new Provider { Id = 1, ProviderType = ProviderType.OpenAI, BaseUrl = "https://provider.invalid/v1" },
        new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "fixture-key" },
        "fixture-model", NullLogger<OpenAIClient>.Instance, new ClientFactory(handler));

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RequestHandler(bool speech) : HttpMessageHandler
    {
        internal static readonly byte[] Audio = [0x49, 0x44, 0x33, 0x00, 0x01];
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
            if (speech)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Audio) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                response.Headers.Add("X-Generation-Id", "fixture-generation");
                return response;
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"chat-union","object":"chat.completion","created":1,"model":"fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
