using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.OpenRouter;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class OpenAICompatibleLogitBiasSerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedMapper_SerializesLogitBiasForChatAndStreaming(bool stream)
    {
        using var handler = new ChatHandler(stream);
        var client = new OpenRouterClient(
            new Provider { Id = 1, ProviderName = "fixture", ProviderType = ProviderType.OpenRouter, BaseUrl = "https://provider.invalid/api/v1" },
            new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "fixture-key" },
            "fixture-model", NullLogger<OpenRouterClient>.Instance, new ClientFactory(handler));
        var request = new ChatCompletionRequest
        {
            Model = "fixture-model", Messages = [new Message { Role = "user", Content = "fixture question" }],
            LogitBias = new() { ["123"] = -100, ["456"] = 0, ["789"] = 100 }
        };
        if (stream)
        {
            var chunks = new List<ChatCompletionChunk>();
            await foreach (var chunk in client.StreamChatCompletionAsync(request)) chunks.Add(chunk);
            Assert.Equal("ok", string.Concat(chunks.SelectMany(chunk => chunk.Choices).Select(choice => choice.Delta.Content)));
            Assert.Equal("stop", chunks.Last().Choices.Single().FinishReason);
        }
        else
        {
            var response = await client.CreateChatCompletionAsync(request);
            Assert.Equal("ok", response.Choices.Single().Message.Content?.ToString());
            Assert.Equal(2, response.Usage!.TotalTokens);
        }
        using var document = JsonDocument.Parse(handler.SubmittedBody!);
        var bias = document.RootElement.GetProperty("logit_bias");
        Assert.Equal(new[] { "123", "456", "789" }, bias.EnumerateObject().Select(property => property.Name));
        Assert.Equal(-100, bias.GetProperty("123").GetSingle());
        Assert.Equal(0, bias.GetProperty("456").GetSingle());
        Assert.Equal(100, bias.GetProperty("789").GetSingle());
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ChatHandler(bool stream) : HttpMessageHandler
    {
        public string? SubmittedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SubmittedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = stream
                ? "data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}\n\ndata: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}\n\ndata: [DONE]\n\n"
                : """{"id":"fixture","object":"chat.completion","created":1,"model":"fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
}
