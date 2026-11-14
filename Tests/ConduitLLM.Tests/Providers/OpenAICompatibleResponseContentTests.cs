using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.Groq;
using ConduitLLM.Providers.OpenAI;
using ConduitLLM.Providers.OpenRouter;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Providers;

[Trait("Category", "Unit")]
[Trait("Component", "Providers")]
public sealed class OpenAICompatibleResponseContentTests
{
    [Theory]
    [InlineData(ProviderType.OpenAI)]
    [InlineData(ProviderType.Azure)]
    [InlineData(ProviderType.Groq)]
    [InlineData(ProviderType.OpenRouter)]
    public async Task CreateChatCompletionAsync_HttpJsonText_IsStringWithUsage(ProviderType providerType)
    {
        const string text = "A reply with café, 雪, \"quotes\", and a newline.\nNext line.";
        var messageJson = $$"""{"role":"assistant","content":{{JsonSerializer.Serialize(text)}} }""";

        var response = await CompleteAsync(providerType, messageJson);

        var choice = Assert.Single(response.Choices);
        Assert.Equal(text, Assert.IsType<string>(choice.Message.Content));
        Assert.Equal("assistant", choice.Message.Role);
        Assert.Equal("stop", choice.FinishReason);
        Assert.Equal("chatcmpl-http-text", response.Id);
        Assert.Equal("model-alias", response.Model);
        Assert.Equal("chat.completion", response.Object);
        Assert.NotNull(response.Usage);
        Assert.Equal(10, response.Usage.PromptTokens);
        Assert.Equal(5, response.Usage.CompletionTokens);
        Assert.Equal(15, response.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(ProviderType.OpenAI, "\"\"", "")]
    [InlineData(ProviderType.Azure, "\"\"", "")]
    [InlineData(ProviderType.Groq, "\"\"", "")]
    [InlineData(ProviderType.OpenRouter, "\"\"", "")]
    [InlineData(ProviderType.OpenAI, "null", null)]
    [InlineData(ProviderType.Azure, "null", null)]
    [InlineData(ProviderType.Groq, "null", null)]
    [InlineData(ProviderType.OpenRouter, "null", null)]
    public async Task CreateChatCompletionAsync_HttpJsonEmptyOrNull_DoesNotInventText(
        ProviderType providerType, string contentJson, string? expected)
    {
        var messageJson = $$"""{"role":"assistant","content":{{contentJson}} }""";

        var response = await CompleteAsync(providerType, messageJson);

        var content = Assert.Single(response.Choices).Message.Content;
        if (expected is null)
            Assert.Null(content);
        else
            Assert.Equal(expected, Assert.IsType<string>(content));
        Assert.Equal(15, response.Usage!.TotalTokens);
    }

    [Theory]
    [InlineData("[{\"type\":\"text\",\"text\":\"first\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"https://example.test/image.png\"},\"vendor_hint\":\"keep\"}]", JsonValueKind.Array)]
    [InlineData("{\"type\":\"output_audio\",\"payload\":\"AQID\"}", JsonValueKind.Object)]
    [InlineData("42", JsonValueKind.Number)]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("false", JsonValueKind.False)]
    public async Task CreateChatCompletionAsync_HttpJsonStructuredOrUnsupportedContent_RemainsStructured(
        string contentJson, JsonValueKind expectedKind)
    {
        var messageJson = $$"""{"role":"assistant","content":{{contentJson}} }""";

        var response = await CompleteAsync(ProviderType.OpenAI, messageJson);

        var content = Assert.IsType<JsonElement>(Assert.Single(response.Choices).Message.Content);
        Assert.Equal(expectedKind, content.ValueKind);
        Assert.Equal(contentJson, content.GetRawText());
        Assert.Equal(15, response.Usage!.TotalTokens);
    }

    [Fact]
    public async Task CreateChatCompletionAsync_HttpJsonText_PreservesToolsAndRichProviderMetadata()
    {
        const string messageJson = """
            {
              "role":"assistant",
              "content":"I can look that up. 雪",
              "tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"query\":\"café\"}"}}],
              "annotations":[{"type":"url_citation","url":"https://example.test/source","vendor_hint":"keep"}],
              "audio":{"id":"audio-1","data":"AQID"},
              "images":[{"type":"image_url","image_url":{"url":"https://example.test/image.png"}}],
              "reasoning_details":[{"type":"reasoning.text","text":"provider reasoning"}],
              "reasoning":"reasoning text",
              "vendor_message":{"source":"keep-message-extension"}
            }
            """;

        var response = await CompleteAsync(ProviderType.OpenRouter, messageJson, "tool_calls");

        var choice = Assert.Single(response.Choices);
        var message = choice.Message;
        Assert.Equal("I can look that up. 雪", Assert.IsType<string>(message.Content));
        Assert.Equal("tool_calls", choice.FinishReason);
        var call = Assert.Single(message.ToolCalls!);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("function", call.Type);
        Assert.Equal("lookup", call.Function!.Name);
        Assert.Equal("{\"query\":\"café\"}", call.Function.Arguments);
        Assert.Equal("keep", Assert.Single(message.Annotations!).GetProperty("vendor_hint").GetString());
        Assert.Equal("audio-1", message.Audio!.Value.GetProperty("id").GetString());
        Assert.Equal("https://example.test/image.png", message.Images!.Value[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("provider reasoning", message.ReasoningDetails!.Value[0].GetProperty("text").GetString());
        Assert.Equal("reasoning text", message.Reasoning);
        Assert.Equal("keep-message-extension", message.ExtensionData!["vendor_message"].GetProperty("source").GetString());
        Assert.Equal("keep-response-extension", response.ExtensionData!["vendor_response"].GetProperty("trace").GetString());
        Assert.Equal("fp-http", response.SystemFingerprint);
        Assert.Equal(15, response.Usage!.TotalTokens);
    }

    private static async Task<ChatCompletionResponse> CompleteAsync(
        ProviderType providerType, string messageJson, string finishReason = "stop")
    {
        var body = $$"""
            {
              "id":"chatcmpl-http-text","object":"chat.completion","created":1760000000,
              "model":"provider-model","choices":[{"index":0,"message":{{messageJson}},"finish_reason":"{{finishReason}}"}],
              "usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15},
              "system_fingerprint":"fp-http","vendor_response":{"trace":"keep-response-extension"}
            }
            """;
        var factory = new JsonResponseFactory(body);
        var provider = new Provider
        {
            Id = 1,
            ProviderType = providerType,
            ProviderName = providerType.ToString(),
            BaseUrl = "https://provider.invalid/v1"
        };
        var credential = new ProviderKeyCredential
        {
            Id = 1,
            ProviderId = 1,
            ApiKey = "test-api-key",
            BaseUrl = "https://provider.invalid/v1"
        };
        ILLMClient client = providerType switch
        {
            ProviderType.OpenAI or ProviderType.Azure => new OpenAIClient(provider, credential, "provider-model", NullLogger<OpenAIClient>.Instance, factory),
            ProviderType.Groq => new GroqClient(provider, credential, "provider-model", NullLogger<GroqClient>.Instance, factory),
            ProviderType.OpenRouter => new OpenRouterClient(provider, credential, "provider-model", NullLogger.Instance, factory),
            _ => throw new ArgumentOutOfRangeException(nameof(providerType))
        };

        var response = await client.CreateChatCompletionAsync(new ChatCompletionRequest
        {
            Model = "model-alias",
            Messages = [new Message { Role = "user", Content = "hello" }]
        });

        Assert.Equal(1, factory.RequestCount);
        Assert.NotNull(factory.RequestUri);
        Assert.EndsWith("/chat/completions", factory.RequestUri.AbsolutePath);
        using var request = JsonDocument.Parse(factory.RequestBody!);
        Assert.Equal("provider-model", request.RootElement.GetProperty("model").GetString());
        return response;
    }

    private sealed class JsonResponseFactory(string responseBody) : IHttpClientFactory
    {
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        public HttpClient CreateClient(string name) => new(new JsonResponseHandler(this, responseBody));

        private sealed class JsonResponseHandler(JsonResponseFactory owner, string responseBody) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                owner.RequestCount++;
                owner.RequestUri = request.RequestUri;
                owner.RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                };
            }
        }
    }
}
