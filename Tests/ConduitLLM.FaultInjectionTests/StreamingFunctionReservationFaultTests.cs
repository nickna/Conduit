using System.Runtime.CompilerServices;
using System.Text;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Billing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
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
public sealed class StreamingFunctionReservationFaultTests(BillingFaultFixture fixture)
{
    [Theory(Timeout = 90_000)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task FunctionNotificationFailure_WithOptionalParentReservation_SettlesExactlyOnce(
        bool functionSucceeded,
        bool cancelBeforeNotification,
        bool reserveParent)
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
            .Returns(FunctionToolStreamAsync())
            .Returns(PartialStreamWithoutUsageAsync(disconnect.Token));
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
        var realReservations = new SpendReservationService(
            batch, NullLogger<SpendReservationService>.Instance);
        var reservations = new TrackingSpendReservationService(realReservations);
        var spendEstimator = new Mock<IChatSpendEstimator>();
        spendEstimator.Setup(service => service.EstimateMaximumCostAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatSpendEstimate(true, 0.25m, 10, 128, null));
        using var requestServices = new ServiceCollection()
            .AddSingleton<ISpendReservationService>(reservations)
            .BuildServiceProvider();
        context.RequestServices = requestServices;
        var endpoints = new ChatEndpoints(
            new Conduit(factory.Object, NullLogger<Conduit>.Instance,
                functionDiscoveryService: discovery.Object, agenticOrchestrationService: orchestration.Object),
            NullLogger<ChatEndpoints>.Instance, Mock.Of<IModelProviderMappingService>(),
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(), settings.Object, estimator.Object,
            repository.Object,
            chatSpendEstimator: spendEstimator.Object,
            spendReservationService: reservations,
            billingAdmissionOptions: Options.Create(new BillingAdmissionOptions
            {
                Mode = reserveParent ? BillingAdmissionMode.Enforce : BillingAdmissionMode.Off
            }),
            httpContextAccessor: new HttpContextAccessor { HttpContext = context });
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
        estimator.Verify(service => service.EstimateUsageFromStreamingResponseAsync(
            It.IsAny<string>(), It.IsAny<List<Message>>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CancellationToken>()), Times.Once);
        reservations.ReserveCalls.Should().Be(reserveParent ? 1 : 0);
        reservations.StartCalls.Should().Be(reserveParent ? 1 : 0);
        reservations.SettleCalls.Should().Be(reserveParent ? 1 : 0);
        reservations.ReleaseCalls.Should().Be(0);
        reservations.IndeterminateCalls.Should().Be(0);
        spendEstimator.Verify(service => service.EstimateMaximumCostAsync(
            It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()),
            reserveParent ? Times.Once() : Times.Never());

        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var redisDb = redis.GetDatabase();
        (await redisDb.KeyExistsAsync($"reserved_spend:group:{account.GroupId}")).Should().BeFalse();
        (await redisDb.HashExistsAsync(
            $"spend_reservations_started:group:{account.GroupId}", snapshot.BillingRequestId))
            .Should().BeFalse();
        (await redisDb.HashExistsAsync(
            $"spend_reservations:group:{account.GroupId}", snapshot.BillingRequestId))
            .Should().BeFalse();
        if (reserveParent)
        {
            snapshot.Reservation.Should().NotBeNull();
            snapshot.Reservation!.InvocationStarted.Should().BeTrue();
            snapshot.Reservation.Closed.Should().BeTrue();
            reservations.LastSettlement!.Status.Should().Be(SpendReservationSettlementStatus.Settled);
            (await redisDb.HashLengthAsync($"spend_reservations_settled:group:{account.GroupId}"))
                .Should().Be(1);
            (await redisDb.HashGetAsync(
                $"spend_reservations_settled:group:{account.GroupId}", snapshot.BillingRequestId))
                .Should().Be((RedisValue)5_000_000L);
            // Re-delivering the parent settlement cannot queue or debit its functions again.
            var replay = await realReservations.SettleAsync(
                account.KeyId, snapshot.BillingRequestId, functionCost);
            replay.Status.Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        }
        else
        {
            snapshot.Reservation.Should().BeNull();
            (await redisDb.HashLengthAsync($"spend_reservations_settled:group:{account.GroupId}"))
                .Should().Be(0);
        }

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

    private static async IAsyncEnumerable<ChatCompletionChunk> FunctionToolStreamAsync()
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

    private static async IAsyncEnumerable<ChatCompletionChunk> PartialStreamWithoutUsageAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatCompletionChunk
        {
            Model = "fault-model",
            Choices = [new StreamingChoice
            {
                Index = 0, Delta = new DeltaContent { Content = "partial completion" }
            }]
        };
        await Task.Yield();
        throw new OperationCanceledException("provider stream ended before usage", cancellationToken);
    }

    private sealed class TrackingSpendReservationService(ISpendReservationService inner)
        : ISpendReservationService
    {
        public int ReserveCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int SettleCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int IndeterminateCalls { get; private set; }
        public SpendReservationSettlementResult? LastSettlement { get; private set; }

        public Task<SpendReservationResult> ReserveAsync(
            int virtualKeyId, string billingRequestId, decimal amount)
        {
            ReserveCalls++;
            return inner.ReserveAsync(virtualKeyId, billingRequestId, amount);
        }

        public Task<bool> MarkInvocationStartedAsync(int virtualKeyId, string billingRequestId)
        {
            StartCalls++;
            return inner.MarkInvocationStartedAsync(virtualKeyId, billingRequestId);
        }

        public async Task<SpendReservationSettlementResult> SettleAsync(
            int virtualKeyId, string billingRequestId, decimal actualAmount, DateTime? billedAtUtc = null)
        {
            SettleCalls++;
            LastSettlement = await inner.SettleAsync(
                virtualKeyId, billingRequestId, actualAmount, billedAtUtc);
            return LastSettlement;
        }

        public Task ReleaseAsync(int virtualKeyId, string billingRequestId)
        {
            ReleaseCalls++;
            return inner.ReleaseAsync(virtualKeyId, billingRequestId);
        }

        public Task MarkIndeterminateAsync(int virtualKeyId, string billingRequestId, string reason)
        {
            IndeterminateCalls++;
            return inner.MarkIndeterminateAsync(virtualKeyId, billingRequestId, reason);
        }
    }
}
