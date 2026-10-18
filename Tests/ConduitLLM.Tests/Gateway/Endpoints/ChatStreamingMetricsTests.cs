using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public class ChatStreamingMetricsTests
{
    /// <summary>Verifies that long streams collect and emit periodic metrics only in enhanced mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeriodicMetrics_OnlyCollectsForEnhancedStreams(bool enhanced)
    {
        var context = CreateContext(enhanced);
        var endpoints = CreateEndpoints(context, []);
        var writer = context.Response.CreateEnhancedSSEWriter(GatewayJsonOptions.Create(), enhanced);
        var collector = new StreamingMetricsCollector("request", "test-model", "provider", TimeSpan.Zero);

        // A long standard stream must not retain timestamps or build latency snapshots
        // for metrics events that its wire format does not expose.
        for (var index = 0; index < 1024; index++)
            await endpoints.EmitPeriodicMetrics(ContentChunk("part"), writer, collector, CancellationToken.None);

        var metrics = collector.GetMetrics();
        if (enhanced)
        {
            Assert.Equal(1024, metrics.TokensGenerated);
            Assert.NotNull(metrics.TimeToFirstTokenMs);
            Assert.NotNull(metrics.AvgInterTokenLatencyMs);
            Assert.Equal(1024, writer.EventsWritten);
            Assert.Contains("event: metrics", ReadBody(context));
        }
        else
        {
            Assert.Equal(0, metrics.TokensGenerated);
            Assert.Null(metrics.TimeToFirstTokenMs);
            Assert.Null(metrics.AvgInterTokenLatencyMs);
            Assert.Equal(0, writer.EventsWritten);
            Assert.Equal(string.Empty, ReadBody(context));
        }
    }

    /// <summary>Verifies completion framing and usage accounting across stream dialects and usage options.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateChatCompletion_PreservesStreamAndAccounting(bool enhanced, bool includeUsage)
    {
        var context = CreateContext(enhanced);
        var endpoints = CreateEndpoints(context, [ContentChunk("first"), ContentChunk("second"), UsageChunk()]);

        await endpoints.CreateChatCompletion(Request(includeUsage));

        var body = ReadBody(context);
        Assert.Contains("first", body);
        Assert.Contains("second", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
        Assert.Equal(1, body.Split("data: [DONE]", StringSplitOptions.None).Length - 1);
        var snapshot = context.GetOrCreateRequestAccountingContext().Snapshot();
        Assert.Equal(StreamTransportOutcome.Completed, snapshot.Transport!.Outcome);
        Assert.Equal(3, snapshot.Transport.ProviderChunksObserved);
        Assert.Equal(10, snapshot.ProviderUsage!.Usage.PromptTokens);
        Assert.Equal(5, snapshot.ProviderUsage.Usage.CompletionTokens);

        if (enhanced)
        {
            Assert.Contains("event: metrics-final", body);
            Assert.Contains("\"completion_tokens\":5", body);
        }
        else
        {
            Assert.DoesNotContain("event:", body);
            var data = body.Split('\n').Where(line => line.StartsWith("data: {")).ToArray();
            Assert.Equal(includeUsage ? 3 : 2, data.Length);
            foreach (var line in data.Take(2))
            {
                using var chunk = JsonDocument.Parse(line[6..]);
                Assert.False(chunk.RootElement.TryGetProperty("usage", out _));
            }
            if (includeUsage)
            {
                using var usage = JsonDocument.Parse(data[^1][6..]);
                Assert.Empty(usage.RootElement.GetProperty("choices").EnumerateArray());
                Assert.Equal(15, usage.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());
            }
        }
    }

    /// <summary>Verifies that client cancellation retains provider usage without emitting a completion marker.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateChatCompletion_ClientCancellationPreservesObservedUsage(bool enhanced)
    {
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(enhanced);
        context.RequestAborted = cancellation.Token;
        var first = ContentChunk("partial");
        first.Usage = UsageChunk().Usage;
        var endpoints = CreateEndpoints(context, [first, ContentChunk("unreachable")], cancellation);

        await endpoints.CreateChatCompletion(Request(includeUsage: true), cancellation.Token);

        var body = ReadBody(context);
        Assert.Contains("partial", body);
        Assert.DoesNotContain("unreachable", body);
        Assert.DoesNotContain("[DONE]", body);
        var snapshot = context.GetOrCreateRequestAccountingContext().Snapshot();
        Assert.Equal(StreamTransportOutcome.ClientDisconnected, snapshot.Transport!.Outcome);
        Assert.Equal(1, snapshot.Transport.ProviderChunksObserved);
        Assert.Equal(15, snapshot.ProviderUsage!.Usage.TotalTokens);
    }

    /// <summary>Creates a non-agentic streaming request with the selected usage option.</summary>
    private static ChatCompletionRequest Request(bool includeUsage) => new()
    {
        Model = "test-model", Stream = true, EnableAgenticMode = false,
        StreamOptions = new StreamOptions { IncludeUsage = includeUsage },
        Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
    };

    /// <summary>Creates an in-memory response at the route selecting the requested SSE dialect.</summary>
    private static DefaultHttpContext CreateContext(bool enhanced)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = enhanced ? "/v1/conduit/chat/completions" : "/v1/chat/completions";
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>Connects the real endpoint orchestration to a deterministic provider stream.</summary>
    private static ChatEndpoints CreateEndpoints(
        DefaultHttpContext context, ChatCompletionChunk[] chunks, CancellationTokenSource cancellation = null)
    {
        var client = new Mock<ILLMClient>();
        client.Setup(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((ChatCompletionRequest _, string _, CancellationToken token) => StreamAsync(chunks, cancellation, token));
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientAsync("test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return new ChatEndpoints(
            new Conduit(factory.Object, NullLogger<Conduit>.Instance),
            NullLogger<ChatEndpoints>.Instance, Mock.Of<IModelProviderMappingService>(),
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(), Mock.Of<IGlobalSettingsCacheService>(),
            Mock.Of<IUsageEstimationService>(),
            httpContextAccessor: new HttpContextAccessor { HttpContext = context });
    }

    /// <summary>Creates one provider chunk containing a content delta.</summary>
    private static ChatCompletionChunk ContentChunk(string content) => new()
    {
        Id = "stream", Model = "test-model",
        Choices = [new StreamingChoice { Index = 0, Delta = new DeltaContent { Content = content } }]
    };

    /// <summary>Creates the provider's final usage-only chunk with known token totals.</summary>
    private static ChatCompletionChunk UsageChunk() => new()
    {
        Id = "stream", Model = "test-model", Choices = [],
        Usage = new Usage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 }
    };

    /// <summary>Reads the accumulated UTF-8 SSE response.</summary>
    private static string ReadBody(DefaultHttpContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    /// <summary>Yields provider chunks and optionally cancels the client after the first observed chunk.</summary>
    private static async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(
        ChatCompletionChunk[] chunks, CancellationTokenSource cancellation,
        [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            yield return chunk;
            cancellation?.Cancel();
        }
        await Task.CompletedTask;
    }
}
