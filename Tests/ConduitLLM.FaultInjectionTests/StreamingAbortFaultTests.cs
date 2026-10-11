using System.Runtime.CompilerServices;
using System.Text;
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
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class StreamingAbortFaultTests(BillingFaultFixture fixture)
{
    [Theory(Timeout = 90_000)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task FunctionNotificationFailure_WithoutProviderUsage_SettlesIncurredCostExactlyOnce(
        bool functionSucceeded,
        bool cancelBeforeNotification)
    {
        const decimal functionCost = 0.05m;
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        using var disconnect = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/conduit/chat/completions";
        context.RequestAborted = disconnect.Token;
        context.Items["VirtualKeyId"] = account.KeyId;
        context.Items["ProviderType"] = "OpenAI";
        var responseStream = new FunctionNotificationFailureStream(disconnect);
        context.Response.Body = responseStream;

        var client = new Mock<ILLMClient>();
        client.SetupSequence(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .Returns(FunctionToolStream())
            .Returns(PartialStreamWithoutUsage(disconnect.Token));
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientAsync("fault-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        var discovery = new Mock<IFunctionDiscoveryService>();
        discovery.Setup(service => service.GetToolsForFunctionConfigurationsAsync(
                It.IsAny<List<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        discovery.Setup(service => service.GetFunctionNameToIdMappingAsync(
                It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, FunctionRoute> { ["lookup"] = new(1) });
        var executionId = Guid.NewGuid();
        var summary = new FunctionCallSummary
        {
            FunctionExecutionId = executionId, FunctionName = "lookup", ToolCallId = "call-1",
            Success = functionSucceeded, Cost = functionCost,
            ErrorMessage = functionSucceeded ? null : "function failed after incurring cost"
        };
        var orchestration = new Mock<IAgenticOrchestrationService>();
        orchestration.Setup(service => service.ExecuteToolCallsAsync(
                It.IsAny<List<ToolCall>>(), It.IsAny<int>(), It.IsAny<Dictionary<string, FunctionRoute>>(),
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (cancelBeforeNotification) disconnect.Cancel();
                // A repeated terminal notification for the same execution must not double-charge.
                return new AgenticExecutionResult
                {
                    AllSucceeded = functionSucceeded, FunctionCallSummaries = [summary, summary],
                    ToolResultMessages = [new Message { Role = MessageRole.Tool, ToolCallId = "call-1", Content = "result" }]
                };
            });
        var repository = new Mock<IFunctionConfigurationRepository>();
        repository.Setup(service => service.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionConfiguration { Id = 1, ConfigurationName = "lookup", IsEnabled = true }]);
        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(service => service.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
        settings.Setup(service => service.GetMaxAgenticIterationsAsync()).ReturnsAsync(5);
        var estimator = new Mock<IUsageEstimationService>();
        estimator.Setup(service => service.EstimateUsageFromStreamingResponseAsync(
                It.IsAny<string>(), It.IsAny<List<Message>>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("provider usage unavailable and estimation failed"));
        var endpoints = new ChatEndpoints(
            new Conduit(factory.Object, NullLogger<Conduit>.Instance,
                functionDiscoveryService: discovery.Object, agenticOrchestrationService: orchestration.Object),
            NullLogger<ChatEndpoints>.Instance, Mock.Of<IModelProviderMappingService>(),
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(), settings.Object, estimator.Object,
            repository.Object, httpContextAccessor: new HttpContextAccessor { HttpContext = context });
        var requestLog = new Mock<IRequestLogRuntimeWriter>();
        requestLog.Setup(service => service.LogRequestAsync(It.IsAny<ConduitLLM.Configuration.DTOs.LogRequestDto>()))
            .Returns(Task.CompletedTask);
        var middleware = new UsageTrackingMiddleware(async httpContext =>
            await endpoints.CreateChatCompletion(new ChatCompletionRequest
            {
                Model = "fault-model", Stream = true, FunctionConfigurationIds = [1],
                EnableAgenticMode = true, MaxAgenticIterations = 5,
                Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
            }, httpContext.RequestAborted), NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, Mock.Of<ICostCalculationService>(), batch, requestLog.Object,
            Mock.Of<IVirtualKeyRuntimeService>(), Mock.Of<IBillingAuditService>(), Mock.Of<IToolCostCalculationService>());

        disconnect.IsCancellationRequested.Should().BeTrue();
        responseStream.FailedTerminalWrites.Should().Be(cancelBeforeNotification ? 0 : 1);
        var snapshot = context.GetOrCreateRequestAccountingContext().Snapshot();
        snapshot.FunctionExecutionCost.Should().Be(functionCost);
        snapshot.FunctionExecutions.Should().ContainSingle()
            .Which.FunctionExecutionId.Should().Be(executionId);
        snapshot.FunctionExecutions[0].Status.Should().Be(functionSucceeded ? "completed" : "failed");
        snapshot.ProviderUsage.Should().BeNull();
        await fixture.AssertAccountedForAsync(account.GroupId, functionCost);
        (await batch.FlushPendingUpdatesAsync()).Should().Be(1);
        (await batch.FlushPendingUpdatesAsync()).Should().Be(0);

        await using var verification = fixture.CreateDbContext();
        var debit = await verification.VirtualKeyGroupTransactions.SingleAsync(
            transaction => transaction.VirtualKeyGroupId == account.GroupId);
        debit.Amount.Should().Be(functionCost);
        (await verification.VirtualKeyGroups.SingleAsync(group => group.Id == account.GroupId))
            .Balance.Should().Be(100m - functionCost);
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> FunctionToolStream()
    {
        yield return new ChatCompletionChunk
        {
            Model = "fault-model",
            Choices = [new StreamingChoice
            {
                Index = 0, FinishReason = FinishReason.ToolCalls,
                Delta = new DeltaContent
                {
                    ToolCalls = [new ToolCallChunk
                    {
                        Index = 0, Id = "call-1",
                        Function = new FunctionCallChunk { Name = "lookup", Arguments = "{}" }
                    }]
                }
            }]
        };
        await Task.CompletedTask;
    }

    private sealed class FunctionNotificationFailureStream(CancellationTokenSource disconnect) : MemoryStream
    {
        public int FailedTerminalWrites { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(buffer.Span);
            if (text.Contains("\"status\":\"completed\"", StringComparison.Ordinal) ||
                text.Contains("\"status\":\"failed\"", StringComparison.Ordinal))
            {
                FailedTerminalWrites++;
                disconnect.Cancel();
                return ValueTask.FromException(new IOException("client disconnected during function notification"));
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task ClientDisconnect_BeforeUsageChunk_BillsEstimatedPartialStreamAfterDirectWriteFailure()
    {
        const decimal expectedCost = 0.0042m;
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
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

        await using var verification = fixture.CreateDbContext();
        var debit = await verification.VirtualKeyGroupTransactions.SingleAsync(
            transaction => transaction.VirtualKeyGroupId == account.GroupId);
        debit.Amount.Should().Be(expectedCost);
        (await verification.VirtualKeyGroups.SingleAsync(group => group.Id == account.GroupId))
            .Balance.Should().Be(100m - expectedCost);
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
