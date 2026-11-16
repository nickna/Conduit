using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.OpenAI;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Providers;

[Trait("Category", "Unit")]
[Trait("Component", "Providers")]
public sealed class ProviderFrameCompatibilityTests
{
    [Theory]
    [InlineData(1, "\n")]
    [InlineData(2, "\r\n")]
    [InlineData(7, "\n")]
    [InlineData(4096, "\r\n")]
    public async Task StreamingPreservesFramingUnicodeToolsAndUsage(int readSize, string newline)
    {
        var frames = new[]
        {
            ": heartbeat", "", "event: message",
            "data: {\"id\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}", "",
            "data: {\"id\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"café 雪\",\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"lookup\",\"arguments\":\"{\\\"key\\\":\"}}]}}]}", "",
            "data: {\"id\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"42}\"}}]},\"finish_reason\":\"tool_calls\"}]}", "",
            "data: {\"id\":\"fixture\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}", "",
            "data: [DONE]", "",
            "data: {\"id\":\"must-not-follow-done\",\"choices\":[]}", "", ""
        };
        var client = CreateClient(string.Join(newline, frames), readSize);
        var chunks = await ReadAsync(client);

        Assert.Equal(4, chunks.Count);
        Assert.Equal("assistant", chunks[0].Choices[0].Delta.Role);
        Assert.Equal("café 雪", chunks[1].Choices[0].Delta.Content);
        var first = Assert.Single(chunks[1].Choices[0].Delta.ToolCalls!);
        var last = Assert.Single(chunks[2].Choices[0].Delta.ToolCalls!);
        Assert.Equal("call-1", first.Id);
        Assert.Equal("lookup", first.Function!.Name);
        Assert.Equal("{\"key\":42}", first.Function.Arguments + last.Function!.Arguments);
        Assert.Equal("tool_calls", chunks[2].Choices[0].FinishReason);
        Assert.Empty(chunks[3].Choices);
        Assert.Equal(15, chunks[3].Usage!.TotalTokens);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal("model-alias", chunk.Model);
            Assert.Equal("model-alias", chunk.OriginalModelAlias);
        });
    }

    [Fact]
    public async Task MalformedFrameRetainsTheExistingProviderFailure()
    {
        var client = CreateClient("data: {malformed}\n\ndata: {\"id\":\"kept\",\"choices\":[]}\n\ndata: [DONE]\n\n", 3);

        // The JsonElement path currently yields an undefined value after the JSON
        // parse warning; mapping it fails. A direct typed parser would instead
        // skip that frame, so it is not a compatible drop-in optimization.
        var failure = await Assert.ThrowsAsync<LLMCommunicationException>(() => ReadAsync(client));

        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public async Task ExistingMappingAndJsonTransformationHooksAreStillInvoked()
    {
        var factory = new ClientFactory("data: {\"id\":\"original\",\"choices\":[]}\n\ndata: [DONE]\n\n", 1);
        var client = new HookClient(factory);

        var chunk = Assert.Single(await ReadAsync(client));

        Assert.Equal("transformed", chunk.Id);
        Assert.Equal(1, client.MappingCalls);
        Assert.Equal(1, client.TransformationCalls);
    }

    private static OpenAIClient CreateClient(string frames, int readSize) => new(
        CreateProvider(), CreateCredential(), "fixture-model", NullLogger<OpenAIClient>.Instance,
        new ClientFactory(frames, readSize));

    private static Provider CreateProvider() =>
        new() { Id = 1, ProviderType = ProviderType.OpenAI, ProviderName = "fixture", BaseUrl = "https://provider.invalid/v1" };

    private static ProviderKeyCredential CreateCredential() =>
        new() { Id = 1, ProviderId = 1, ApiKey = "fixture-key" };

    private static async Task<List<ChatCompletionChunk>> ReadAsync(OpenAIClient client)
    {
        var result = new List<ChatCompletionChunk>();
        await foreach (var chunk in client.StreamChatCompletionAsync(new()
        {
            Model = "model-alias", Messages = [new Message { Role = "user", Content = "fixture" }]
        })) result.Add(chunk);
        return result;
    }

    private sealed class HookClient(IHttpClientFactory factory) : OpenAIClient(
        CreateProvider(), CreateCredential(), "fixture-model", NullLogger<OpenAIClient>.Instance, factory)
    {
        public int MappingCalls { get; private set; }
        public int TransformationCalls { get; private set; }

        protected override ChatCompletionChunk? MapStreamingChunk(JsonElement chunk)
        {
            MappingCalls++;
            return base.MapStreamingChunk(chunk);
        }

        protected override string TransformChunkJson(JsonElement chunk)
        {
            TransformationCalls++;
            return chunk.GetRawText().Replace("original", "transformed", StringComparison.Ordinal);
        }
    }

    private sealed class ClientFactory(string frames, int readSize) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(frames, readSize));
    }

    private sealed class Handler(string frames, int readSize) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(frames), readSize));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private sealed class FragmentedStream(byte[] bytes, int readSize) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, readSize)], cancellationToken);
    }
}
