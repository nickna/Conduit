using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Providers.MiniMax;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Providers;

[Trait("Category", "Unit")]
[Trait("Component", "Providers")]
public sealed class MiniMaxStreamingAccountingTests
{
    private const string ContentFrame = """
        {"id":"minimax-1","created":1,"model":"provider-model","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"}}]}
        """;

    private const string FinalFrame = """
        {"id":"minimax-1","created":1,"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    [Fact]
    public async Task StreamChatCompletionAsync_ForwardsFinalUsageAndPreservesProviderRequest()
    {
        var factory = new SseHttpClientFactory(ContentFrame, FinalFrame);
        var chunks = await ReadChunksAsync(CreateClient(factory));

        Assert.Collection(chunks,
            chunk =>
            {
                Assert.Equal("ok", Assert.Single(chunk.Choices).Delta.Content);
                Assert.Null(chunk.Usage);
            },
            chunk =>
            {
                Assert.Equal(FinishReason.Stop, Assert.Single(chunk.Choices).FinishReason);
                AssertUsage(chunk.Usage);
            });
        Assert.All(chunks, chunk =>
        {
            Assert.Equal("minimax-1", chunk.Id);
            Assert.Equal("chat.completion.chunk", chunk.Object);
            Assert.Equal(1, chunk.Created);
            Assert.Equal("test-model", chunk.Model);
        });
        Assert.Equal(HttpMethod.Post, factory.Method);
        Assert.EndsWith("/v1/text/chatcompletion_v2", factory.RequestUri.AbsolutePath);
        Assert.Equal("Bearer test-key", factory.Authorization);
        Assert.Contains("text/event-stream", factory.Accept);
        using var request = JsonDocument.Parse(factory.RequestBody);
        Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("test-model", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task StreamChatCompletionAsync_ForwardsUsageOnlyFinalFrames(string choices)
    {
        var frame = $$$"""
            {"id":"minimax-1","choices":{{{choices}}},"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;
        var chunks = await ReadChunksAsync(CreateClient(new SseHttpClientFactory(ContentFrame, frame)));

        Assert.Equal("ok", Assert.Single(chunks[0].Choices).Delta.Content);
        Assert.Empty(chunks[1].Choices);
        AssertUsage(chunks[1].Usage);
        Assert.Equal(2, chunks.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"usage\":null")]
    [InlineData(",\"usage\":{}")]
    [InlineData(",\"usage\":{\"total_characters\":2}")]
    public async Task StreamChatCompletionAsync_DoesNotInventTokenUsage(string usage)
    {
        var frame = $$$"""
            {"id":"minimax-1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]{{{usage}}}}
            """;
        var chunks = await ReadChunksAsync(CreateClient(new SseHttpClientFactory(ContentFrame, frame)));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(FinishReason.Stop, Assert.Single(chunks[1].Choices).FinishReason);
        Assert.Null(chunks[1].Usage);
    }

    [Theory]
    [InlineData("{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}", 0, 0, 0)]
    [InlineData("{\"prompt_tokens\":0}", 0, null, null)]
    [InlineData("{\"prompt_tokens\":3}", 3, null, null)]
    [InlineData("{\"completion_tokens\":4}", null, 4, null)]
    [InlineData("{\"total_tokens\":7}", null, null, 7)]
    public async Task StreamChatCompletionAsync_PreservesReportedZeroAndPartialTokenUsage(
        string usage, int? promptTokens, int? completionTokens, int? totalTokens)
    {
        var frame = $$$"""
            {"id":"minimax-1","choices":[],"usage":{{{usage}}}}
            """;
        var chunk = Assert.Single(await ReadChunksAsync(CreateClient(new SseHttpClientFactory(frame))));

        Assert.Empty(chunk.Choices);
        Assert.NotNull(chunk.Usage);
        Assert.Equal(promptTokens, chunk.Usage.PromptTokens);
        Assert.Equal(completionTokens, chunk.Usage.CompletionTokens);
        Assert.Equal(totalTokens, chunk.Usage.TotalTokens);
    }

    [Fact]
    public async Task StreamChatCompletionAsync_RetainsUsageWhileSuppressingRedundantFinalMessage()
    {
        const string finalMessage = """
            {"id":"minimax-1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;
        var chunks = await ReadChunksAsync(CreateClient(new SseHttpClientFactory(ContentFrame, finalMessage)));

        Assert.Equal("ok", string.Concat(chunks.SelectMany(chunk => chunk.Choices).Select(choice => choice.Delta.Content)));
        var finalChoice = Assert.Single(chunks[1].Choices);
        Assert.Null(finalChoice.Delta.Content);
        Assert.Equal(MessageRole.Assistant, finalChoice.Delta.Role);
        Assert.Equal(FinishReason.Stop, finalChoice.FinishReason);
        Assert.Equal("chat.completion.chunk", chunks[1].Object);
        AssertUsage(chunks[1].Usage);
    }

    [Theory]
    [InlineData("delta")]
    [InlineData("message")]
    public async Task StreamChatCompletionAsync_PreservesFunctionCallsWithUsage(string field)
    {
        var frame = $$$"""
            {"id":"minimax-1","choices":[{"index":0,"{{{field}}}":{"role":"assistant","function_call":{"name":"lookup","arguments":"{\"id\":7}"}},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;
        var chunks = await ReadChunksAsync(CreateClient(new SseHttpClientFactory(frame)));

        var chunk = Assert.Single(chunks);
        AssertUsage(chunk.Usage);
        var choice = Assert.Single(chunk.Choices);
        Assert.Equal(FinishReason.ToolCalls, choice.FinishReason);
        var tool = Assert.Single(choice.Delta.ToolCalls);
        Assert.Equal(0, tool.Index);
        Assert.False(string.IsNullOrEmpty(tool.Id));
        Assert.Equal("function", tool.Type);
        Assert.Equal("lookup", tool.Function.Name);
        Assert.Equal("{\"id\":7}", tool.Function.Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayStream_AccountsReportedUsageExactlyOnce(bool usageOnly)
    {
        var finalFrame = usageOnly
            ? """{"id":"minimax-1","choices":null,"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""
            : FinalFrame;
        var provider = CreateClient(new SseHttpClientFactory(ContentFrame, finalFrame));
        var clients = new Mock<ILLMClientFactory>();
        clients.Setup(service => service.GetClientAsync("test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);
        var estimator = new Mock<IUsageEstimationService>(MockBehavior.Strict);
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/v1/chat/completions";
        context.Items["VirtualKeyId"] = 42;
        context.Items["ProviderType"] = ProviderType.MiniMax;
        context.Response.Body = new MemoryStream();
        var endpoints = new ChatEndpoints(
            new Conduit(clients.Object, NullLogger<Conduit>.Instance),
            NullLogger<ChatEndpoints>.Instance, Mock.Of<IModelProviderMappingService>(),
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(),
            Mock.Of<IGlobalSettingsCacheService>(), estimator.Object,
            httpContextAccessor: new HttpContextAccessor { HttpContext = context });
        var pricing = new Mock<ICostCalculationService>();
        pricing.Setup(service => service.CalculateCostAsync(
                "test-model", It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.01m);
        var spend = new Mock<IBatchSpendUpdateService>();
        spend.SetupGet(service => service.IsHealthy).Returns(true);
        spend.Setup(service => service.QueueSpendUpdateAsync(42, It.IsAny<decimal>(), It.IsAny<DateTime?>()))
            .Returns(Task.CompletedTask);
        var logs = new List<LogRequestDto>();
        var writer = new Mock<IRequestLogRuntimeWriter>();
        writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
            .Callback<LogRequestDto>(logs.Add).Returns(Task.CompletedTask);
        var middleware = new UsageTrackingMiddleware(
            async _ => { await endpoints.CreateChatCompletion(CreateRequest()); },
            NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, pricing.Object, spend.Object, writer.Object,
            Mock.Of<IVirtualKeyRuntimeService>(), Mock.Of<IBillingAuditService>(), Mock.Of<IToolCostCalculationService>());

        var accounting = context.GetRequestAccountingSnapshot();
        Assert.NotNull(accounting.ProviderUsage);
        Assert.Equal(UsageEvidenceSource.Provider, accounting.ProviderUsage.Source);
        AssertUsage(accounting.ProviderUsage.Usage);
        Assert.False(accounting.IsIndeterminate);
        Assert.Empty(estimator.Invocations);
        pricing.Verify(service => service.CalculateCostAsync("test-model",
            It.Is<Usage>(usage => usage.PromptTokens == 1 && usage.CompletionTokens == 1 && usage.TotalTokens == 2),
            It.IsAny<CancellationToken>()), Times.Once);
        spend.Verify(service => service.QueueSpendUpdateAsync(42, 0.01m, It.IsAny<DateTime?>()), Times.Once);
        var log = Assert.Single(logs);
        Assert.Equal(1, log.InputTokens);
        Assert.Equal(1, log.OutputTokens);
        Assert.Equal(0.01m, log.Cost);
        writer.Verify(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()), Times.Once);
    }

    private static void AssertUsage(Usage usage)
    {
        Assert.NotNull(usage);
        Assert.Equal(1, usage.PromptTokens);
        Assert.Equal(1, usage.CompletionTokens);
        Assert.Equal(2, usage.TotalTokens);
    }

    private static ChatCompletionRequest CreateRequest() => new()
    {
        Model = "test-model", Stream = true,
        Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
    };

    private static MiniMaxClient CreateClient(SseHttpClientFactory factory) => new(
        new Provider { Id = 1, ProviderType = ProviderType.MiniMax, ProviderName = "MiniMax" },
        new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "test-key" },
        "provider-model", NullLogger<MiniMaxClient>.Instance, factory);

    private static async Task<List<ChatCompletionChunk>> ReadChunksAsync(MiniMaxClient client)
    {
        var chunks = new List<ChatCompletionChunk>();
        await foreach (var chunk in client.StreamChatCompletionAsync(CreateRequest())) chunks.Add(chunk);
        return chunks;
    }

    private sealed class SseHttpClientFactory(params string[] frames) : IHttpClientFactory
    {
        private string ResponseBody { get; } =
            string.Join("\n\n", frames.Select(frame => $"data: {frame}")) + "\n\ndata: [DONE]\n\n";

        public HttpMethod Method { get; private set; }
        public Uri RequestUri { get; private set; }
        public string Authorization { get; private set; }
        public string Accept { get; private set; }
        public string RequestBody { get; private set; }

        public HttpClient CreateClient(string name) => new(new SseHandler(this));

        private sealed class SseHandler(SseHttpClientFactory fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                fixture.Method = request.Method;
                fixture.RequestUri = request.RequestUri;
                fixture.Authorization = request.Headers.Authorization?.ToString();
                fixture.Accept = request.Headers.Accept.ToString();
                fixture.RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        fixture.ResponseBody,
                        Encoding.UTF8, "text/event-stream")
                };
            }
        }
    }
}
