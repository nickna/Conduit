using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.Replicate;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class ReplicateChatSerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LlamaChat_SubmitsRuntimeMessageListThroughGeneratedContracts(bool stream)
    {
        using var handler = new PredictionHandler();
        var client = new ReplicateClient(
            new Provider { Id = 1, ProviderName = "fixture", ProviderType = ProviderType.Replicate, BaseUrl = "https://provider.invalid/v1" },
            new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "fixture-key" },
            "meta/llama-fixture", NullLogger<ReplicateClient>.Instance, new ClientFactory(handler));
        using var extension = JsonDocument.Parse("""{"enabled":false,"weights":[0.5,1]}""");
        var request = new ChatCompletionRequest
        {
            Model = "fixture-alias", MaxTokens = 10, Temperature = 0.5, Stop = ["END", "STOP"],
            Messages = [new Message { Role = "system", Content = "fixture system" }, new Message { Role = "user", Content = "fixture question" }],
            ExtensionData = new() { ["options"] = extension.RootElement.Clone() }
        };
        if (stream)
        {
            var chunks = new List<ChatCompletionChunk>();
            await foreach (var chunk in client.StreamChatCompletionAsync(request)) chunks.Add(chunk);
            Assert.Equal("ok done", string.Concat(chunks.SelectMany(chunk => chunk.Choices).Select(choice => choice.Delta.Content)));
            Assert.Equal("stop", chunks.Last().Choices.Single().FinishReason);
        }
        else
        {
            var response = await client.CreateChatCompletionAsync(request);
            Assert.Equal("ok done", response.Choices.Single().Message.Content);
            Assert.Equal("fixture-alias", response.Model);
            Assert.Null(response.Usage);
        }

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/v1/models/meta/llama-fixture/predictions", handler.Requests[0].AbsolutePath);
        Assert.Equal("/v1/predictions/prediction-fixture", handler.Requests[1].AbsolutePath);
        using var document = JsonDocument.Parse(handler.SubmittedBody!);
        Assert.False(document.RootElement.TryGetProperty("version", out _));
        var input = document.RootElement.GetProperty("input");
        Assert.Equal("fixture system", input.GetProperty("system_prompt").GetString());
        var message = Assert.Single(input.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("fixture question", message.GetProperty("content").GetString());
        Assert.Equal(2, message.EnumerateObject().Count());
        Assert.Equal(10, input.GetProperty("max_length").GetInt32());
        Assert.Equal(0.5, input.GetProperty("temperature").GetDouble());
        Assert.Equal(new[] { "END", "STOP" }, input.GetProperty("stop_sequences").EnumerateArray().Select(value => value.GetString()));
        Assert.False(input.GetProperty("options").GetProperty("enabled").GetBoolean());
        Assert.Equal(0.5, input.GetProperty("options").GetProperty("weights")[0].GetDouble());
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class PredictionHandler : HttpMessageHandler
    {
        public string? SubmittedBody { get; private set; }
        public List<Uri> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.Equal("Token", request.Headers.Authorization!.Scheme);
            string json;
            if (request.Method == HttpMethod.Post)
            {
                SubmittedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                json = """{"id":"prediction-fixture","status":"starting","created_at":"2026-10-10T01:02:03Z"}""";
            }
            else
                json = """{"id":"prediction-fixture","status":"succeeded","output":["ok"," done"],"created_at":"2026-10-10T01:02:03Z"}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
