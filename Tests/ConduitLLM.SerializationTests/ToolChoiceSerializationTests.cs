using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Providers.MiniMax;
using ConduitLLM.Providers.OpenAI;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class ToolChoiceSerializationTests
{
    [Theory]
    [InlineData("get_weather")]
    [InlineData("quoted_\"name\"\n")]
    public void ForcedFunction_RoundTripsThroughGeneratedGatewayContracts(string name)
    {
        var options = GatewayJsonOptions.Create();
        var request = new ChatCompletionRequest
        {
            Model = "test-model", Messages = [], ToolChoice = ToolChoice.Function(name)
        };

        var json = JsonSerializer.Serialize(request, options);
        var restored = JsonSerializer.Deserialize<ChatCompletionRequest>(json, options)!;

        Assert.True(restored.ToolChoice!.TryGetFunctionName(out var restoredName));
        Assert.Equal(name, restoredName);
        var rawChoice = Assert.IsType<JsonElement>(restored.ToolChoice.GetSerializedValue());
        Assert.Equal(name, rawChoice.GetProperty("function").GetProperty("name").GetString());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(restored, options));
        var choice = document.RootElement.GetProperty("tool_choice");
        Assert.Equal("function", choice.GetProperty("type").GetString());
        Assert.Equal(name, choice.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(2, choice.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedFunction_ReachesProviderHttpSerializationWithoutReflection(bool miniMax)
    {
        using var handler = new CapturingHandler();
        var factory = new CapturingHttpClientFactory(handler);
        var provider = new Provider
        {
            Id = 1, ProviderName = "fixture",
            ProviderType = miniMax ? ProviderType.MiniMax : ProviderType.OpenAICompatible,
            BaseUrl = "https://provider.invalid"
        };
        var credential = new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "fixture-key" };
        ILLMClient client = miniMax
            ? new MiniMaxClient(provider, credential, "test-model", NullLogger<MiniMaxClient>.Instance, factory)
            : new OpenAIClient(provider, credential, "test-model", NullLogger<OpenAIClient>.Instance, factory);

        await client.CreateChatCompletionAsync(new ChatCompletionRequest
        {
            Model = "test-model",
            Messages = [new Message { Role = "user", Content = "hello" }],
            ToolChoice = ToolChoice.Function("get_weather")
        });

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var choice = document.RootElement.GetProperty("tool_choice");
        Assert.Equal("function", choice.GetProperty("type").GetString());
        Assert.Equal("get_weather", choice.GetProperty("function").GetProperty("name").GetString());
    }

    private sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"chatcmpl-fixture","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
