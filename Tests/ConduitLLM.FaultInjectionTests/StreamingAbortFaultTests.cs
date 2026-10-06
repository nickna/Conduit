using System.Runtime.CompilerServices;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class StreamingAbortFaultTests(BillingFaultFixture fixture)
{
    [Fact(Timeout = 90_000)]
    public async Task ClientDisconnect_BeforeUsageChunk_BillsEstimatedPartialStreamAfterDirectWriteFailure()
    {
        const decimal expectedCost = 0.0042m;
        var account = await fixture.SeedAccountAsync();
        await using var batch = fixture.CreateBatchService();
        await batch.StartAsync(CancellationToken.None);
        var directService = new DirectApiVirtualKeyService(
            fixture.CreateKeyRepository(),
            fixture.CreateGroupRepository(),
            Mock.Of<IVirtualKeySpendHistoryRepository>(),
            null,
            NullLogger<DirectApiVirtualKeyService>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Body = new DisconnectingResponseStream();
        context.Items["VirtualKeyId"] = account.KeyId;
        context.Items["ProviderType"] = "OpenAI";

        var costService = new Mock<ICostCalculationService>();
        costService.Setup(service => service.CalculateCostAsync(
                "fault-model", It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedCost);
        var requestLogService = new Mock<IRequestLogService>();
        requestLogService.Setup(service => service.LogRequestAsync(It.IsAny<ConduitLLM.Configuration.DTOs.LogRequestDto>()))
            .Returns(Task.CompletedTask);

        var llmClient = new Mock<ILLMClient>();
        llmClient.Setup(client => client.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .Returns(PartialStreamWithoutUsage());
        var clientFactory = new Mock<ILLMClientFactory>();
        clientFactory.Setup(factory => factory.GetClientAsync("fault-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmClient.Object);
        var estimator = new Mock<IUsageEstimationService>();
        estimator.Setup(service => service.EstimateUsageFromStreamingResponseAsync(
                "fault-model", It.IsAny<List<Message>>(), "partial completion",
                It.IsAny<IReadOnlyList<Tool>?>(),
                It.Is<CancellationToken>(ct => ct.CanBeCanceled)))
            .ReturnsAsync(new Usage { PromptTokens = 20, CompletionTokens = 30, TotalTokens = 50 });
        var endpoints = new ChatEndpoints(
            new Conduit(clientFactory.Object, NullLogger<Conduit>.Instance),
            NullLogger<ChatEndpoints>.Instance,
            Mock.Of<IModelProviderMappingService>(),
            GatewayJsonOptions.Create(),
            Mock.Of<IEventPublisher>(),
            Mock.Of<IGlobalSettingsCacheService>(),
            estimator.Object,
            httpContextAccessor: new HttpContextAccessor { HttpContext = context });

        var middleware = new UsageTrackingMiddleware(
            async httpContext =>
            {
                await endpoints.CreateChatCompletion(new ChatCompletionRequest
                {
                    Model = "fault-model",
                    Stream = true,
                    MaxAgenticIterations = 1,
                    EnableAgenticMode = false,
                    Messages = [new Message { Role = "user", Content = "hello" }]
                }, httpContext.RequestAborted);
            },
            NullLogger<UsageTrackingMiddleware>.Instance);

        var act = () => middleware.InvokeAsync(
            context,
            costService.Object,
            batch,
            requestLogService.Object,
            directService,
            Mock.Of<IBillingAuditService>(),
            Mock.Of<IToolCostCalculationService>());

        await act.Should().NotThrowAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, expectedCost);

        (await batch.FlushPendingUpdatesAsync()).Should().Be(1);
        await fixture.AssertAccountedForAsync(account.GroupId, expectedCost);
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> PartialStreamWithoutUsage(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatCompletionChunk
        {
            Model = "fault-model",
            Choices =
            [
                new StreamingChoice
                {
                    Index = 0,
                    Delta = new DeltaContent { Content = "partial completion" }
                }
            ]
        };
        await Task.Yield();
        throw new OperationCanceledException("provider stream ended before usage", cancellationToken);
    }

    private sealed class DisconnectingResponseStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("client disconnected");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("client disconnected"));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("client disconnected"));
    }
}
