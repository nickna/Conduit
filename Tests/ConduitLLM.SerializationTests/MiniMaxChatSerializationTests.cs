using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.MiniMax;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.SerializationTests;

public sealed class MiniMaxChatSerializationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DebugChatAndStreaming_SerializeToolsAndMultimodalContentWithoutReflection(bool stream, bool jsonContent)
    {
        using var content = JsonDocument.Parse("""[{"type":"text","text":"fixture question"},{"type":"image_url","image_url":{"url":"https://cdn.invalid/image.png"}}]""");
        using var handler = new ChatHandler(stream);
        var logger = new DebugLogger();
        var client = new MiniMaxClient(
            new Provider { Id = 1, ProviderName = "fixture", ProviderType = ProviderType.MiniMax, BaseUrl = "https://provider.invalid" },
            new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "fixture-key" },
            "fixture-model", logger, new ClientFactory(handler));
        var request = new ChatCompletionRequest
        {
            Model = "fixture-model",
            Messages = [new Message
            {
                Role = "user", Content = jsonContent ? content.RootElement.Clone() : new List<object>
                {
                    new TextContentPart { Text = "fixture question" },
                    new ImageUrlContentPart { ImageUrl = new ImageUrl { Url = "https://cdn.invalid/image.png" } }
                }
            }],
            Tools = [new Tool { Function = new FunctionDefinition
            {
                Name = "get_weather",
                Parameters = JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")!.AsObject()
            }}],
            ToolChoice = ToolChoice.Function("get_weather"), ResponseFormat = ResponseFormat.Json()
        };

        if (stream)
        {
            var chunks = new List<ChatCompletionChunk>();
            await foreach (var chunk in client.StreamChatCompletionAsync(request)) chunks.Add(chunk);
            Assert.Equal("ok", string.Concat(chunks.SelectMany(chunk => chunk.Choices).Select(choice => choice.Delta.Content)));
            Assert.Equal("stop", chunks.Last().Choices.Single().FinishReason);
            Assert.Equal(2, chunks.Count);
            Assert.Null(chunks[0].Usage);
            Assert.Equal(1, chunks[1].Usage!.PromptTokens);
            Assert.Equal(1, chunks[1].Usage!.CompletionTokens);
            Assert.Equal(2, chunks[1].Usage!.TotalTokens);
        }
        else
        {
            var response = await client.CreateChatCompletionAsync(request);
            Assert.Equal("ok", response.Choices.Single().Message.Content);
            Assert.Equal(2, response.Usage!.TotalTokens);
        }

        Assert.Contains(logger.Messages, message => message.Contains("get_weather", StringComparison.Ordinal));
        Assert.Equal(stream ? "/v1/text/chatcompletion_v2" : "/v1/chat/completions", handler.Endpoint!.AbsolutePath);
        using var document = JsonDocument.Parse(handler.SubmittedBody!);
        var submitted = document.RootElement;
        Assert.Equal(stream, submitted.GetProperty("stream").GetBoolean());
        Assert.Equal("get_weather", submitted.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
        var schema = submitted.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("city").GetProperty("type").GetString());
        Assert.Equal("city", schema.GetProperty("required")[0].GetString());
        var message = submitted.GetProperty("messages")[0];
        Assert.Equal("fixture question", message.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("https://cdn.invalid/image.png", message.GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("json_schema", submitted.GetProperty("reply_constraints").GetProperty("guidance_type").GetString());
        Assert.Equal("object", submitted.GetProperty("reply_constraints").GetProperty("json_schema").GetProperty("type").GetString());
        if (stream)
        {
            Assert.Equal("user", message.GetProperty("name").GetString());
        }
        else
        {
            Assert.False(message.TryGetProperty("name", out _));
        }
        Assert.False(submitted.TryGetProperty("max_tokens", out _));
    }

    private sealed class DebugLogger : ILogger<MiniMaxClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ChatHandler(bool stream) : HttpMessageHandler
    {
        public string? SubmittedBody { get; private set; }
        public Uri? Endpoint { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri;
            SubmittedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = stream
                ? "data: {\"id\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}\n\ndata: {\"id\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}\n\ndata: [DONE]\n\n"
                : """{"id":"fixture","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
}
