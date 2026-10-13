using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;
using Npgsql;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class StreamingInvocationReservationTests(BillingFaultFixture fixture)
{
    private const string Model = "stream-invocation-fault";
    private const decimal Bound = 0.25m;
    private static readonly DateTime BilledAt = new(2026, 7, 25, 23, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime BillingWindow = BilledAt.Date.AddHours(23);

    public static IEnumerable<object[]> Scenarios()
    {
        yield return ["normal-before-chunk", 2, 1, 0.02m];
        yield return ["normal-partial", 2, 1, 0.02m];
        yield return ["forced-before-chunk", 2, 1, 0.02m];
        yield return ["forced-partial", 2, 1, 0.02m];
        yield return ["fallback-before-chunk", 2, 0, 0.02m];
        yield return ["fallback-partial", 2, 0, 0.02m];
        yield return ["cumulative-repeated", 2, 1, 0.02m];
        yield return ["earlier-null", 2, 1, 0.02m];
        yield return ["context-clone", 2, 1, 0.02m];
        yield return ["cancel-after-function", 1, 1, 0.02m];
        yield return ["intermediate-failure", 1, 0, 0.01m];
        yield return ["client-write-usage", 1, 0, 0.02m];
        yield return ["client-write-no-usage", 1, 0, 0m];
        yield return ["provider-dispose-usage", 1, 0, 0.02m];
        yield return ["client-write-dispose", 1, 0, 0.02m];
        yield return ["canceled-with-usage-chunk", 1, 0, 0.02m];
    }

    [Theory(Timeout = 90_000)]
    [MemberData(nameof(Scenarios))]
    public async Task ActualStreamingPipeline_RetainsRawKnownCostUntilEveryInvocationExhausts(
        string scenario, int expectedProviderCalls, int expectedFunctionCalls, decimal knownProviderCost)
    {
        var account = await fixture.SeedAccountAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.PostgreSqlConnectionString);
        using var redisFactory = new RedisConnectionFactory(
            Options.Create(new CacheOptions { RedisConnectionString = fixture.RedisConnectionString }),
            NullLogger<RedisConnectionFactory>.Instance);
        await using var batch = await StartBatchAsync(dataSource, redisFactory);
        var reservations = new SpendReservationService(batch, NullLogger<SpendReservationService>.Instance);
        using var services = new ServiceCollection().AddLogging()
            .ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions))
            .AddSingleton<ISpendReservationService>(reservations).BuildServiceProvider();
        using var canceled = new CancellationTokenSource();
        using var body = new TransportFailureStream(scenario.StartsWith("client-write", StringComparison.Ordinal), canceled);
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = canceled.Token };
        context.Request.Path = "/v1/conduit/chat/completions";
        context.Response.Body = body;
        context.Items["VirtualKeyId"] = account.KeyId;
        context.Items["Conduit.BillingOccurredAtUtc"] = BilledAt;
        var providerCalls = 0;
        var client = new Mock<ILLMClient>();
        client.Setup(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .Returns((ChatCompletionRequest _, string? _, CancellationToken token) =>
            {
                providerCalls++;
                if (scenario is "provider-dispose-usage" or "client-write-dispose")
                    return new DisposeFailureEnumerable(Chunk(1, false, 0.02m, "transport-body"));
                return ProviderStream(scenario, providerCalls, canceled, token);
            });
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientForChatAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => request.SelectedMappingId = 23)
            .ReturnsAsync(client.Object);
        var discovery = new Mock<IFunctionDiscoveryService>();
        discovery.Setup(service => service.GetToolsForFunctionConfigurationsAsync(
                It.IsAny<List<int>>(), account.KeyId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        discovery.Setup(service => service.GetFunctionNameToIdMappingAsync(
                It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(scenario.StartsWith("fallback", StringComparison.Ordinal)
                ? [] : new Dictionary<string, FunctionRoute> { ["lookup"] = new(1) });
        var functionCalls = 0;
        var executionId = Guid.NewGuid();
        var orchestration = new Mock<IAgenticOrchestrationService>();
        orchestration.Setup(service => service.ExecuteToolCallsAsync(It.IsAny<List<ToolCall>>(), account.KeyId,
                It.IsAny<Dictionary<string, FunctionRoute>>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                functionCalls++;
                if (scenario == "cancel-after-function") canceled.Cancel();
                return new AgenticExecutionResult
                {
                    AllSucceeded = true, TotalFunctionCost = 0.05m,
                    FunctionCallSummaries = [new FunctionCallSummary
                    {
                        Iteration = 1, FunctionName = "lookup", ToolCallId = "call-1", Success = true,
                        Cost = 0.05m, FunctionExecutionId = executionId
                    }],
                    ToolResultMessages = [new Message { Role = MessageRole.Tool, ToolCallId = "call-1", Content = "found" }]
                };
            });
        var routedProvider = new Provider
        {
            ProviderType = ProviderType.OpenAI, TrustProviderReportedCosts = true, ProviderCostMarkupMultiplier = 1m
        };
        var mapping = new ModelProviderMapping
        {
            Id = 23, ProviderId = 1, Provider = routedProvider,
            ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 17, MaxInputTokens = 100 }
        };
        var mappings = new Mock<IModelProviderMappingService>();
        mappings.Setup(service => service.GetMappingByModelAliasAsync(Model)).ReturnsAsync(mapping);
        mappings.Setup(service => service.GetMappingByIdAsync(23)).ReturnsAsync(mapping);
        var modelCosts = new Mock<IModelCostService>();
        modelCosts.Setup(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>())).ReturnsAsync((ModelCost?)null);
        var pricing = new ConduitLLM.Core.Services.CostCalculationService(modelCosts.Object,
            NullLogger<ConduitLLM.Core.Services.CostCalculationService>.Instance);
        var estimator = new Mock<IUsageEstimationService>();
        estimator.Setup(service => service.EstimateUsageFromStreamingResponseAsync(
                It.IsAny<string>(), It.IsAny<List<Message>>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No authoritative estimate is available for this interrupted invocation"));
        var configs = new Mock<IFunctionConfigurationRepository>();
        configs.Setup(service => service.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionConfiguration { Id = 1, ConfigurationName = "lookup", IsEnabled = true }]);
        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(service => service.GetMaxAgenticIterationsAsync()).ReturnsAsync(5);
        settings.Setup(service => service.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
        var maximumSpend = new Mock<IChatSpendEstimator>();
        maximumSpend.Setup(service => service.EstimateMaximumCostAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatSpendEstimate(true, Bound, 10, 128, null));
        var manager = new Mock<IContextManager>();
        manager.Setup(service => service.ManageContextAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<int?>()))
            .ReturnsAsync((ChatCompletionRequest request, int? _) => new ChatCompletionRequest
            {
                Model = request.Model, Stream = true, Messages = request.Messages.ToList(),
                FunctionConfigurationIds = request.FunctionConfigurationIds, EnableAgenticMode = request.EnableAgenticMode,
                MaxAgenticIterations = request.MaxAgenticIterations
            });
        var core = new Conduit(factory.Object, NullLogger<Conduit>.Instance,
            contextManager: scenario == "context-clone" ? manager.Object : null,
            modelProviderMappingService: mappings.Object, contextOptions: Options.Create(new ContextManagementOptions()),
            functionDiscoveryService: discovery.Object, agenticOrchestrationService: orchestration.Object);
        var endpointLogger = new EndpointLogger();
        var endpoint = new ChatEndpoints(core, endpointLogger, mappings.Object,
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(), settings.Object, estimator.Object, configs.Object,
            usageTrackingOptions: Options.Create(new UsageTrackingOptions { AccountingFinalizationTimeoutSeconds = 1 }),
            chatSpendEstimator: maximumSpend.Object, spendReservationService: reservations,
            billingAdmissionOptions: Options.Create(new BillingAdmissionOptions { Mode = BillingAdmissionMode.Enforce }),
            httpContextAccessor: new HttpContextAccessor { HttpContext = context }, costCalculationService: pricing,
            toolCostCalculationService: Mock.Of<IToolCostCalculationService>());
        var logs = new List<LogRequestDto>();
        var writer = new Mock<IRequestLogRuntimeWriter>();
        writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
            .Callback<LogRequestDto>(logs.Add).Returns(Task.CompletedTask);
        var request = new ChatCompletionRequest
        {
            Model = Model, Stream = true, EnableAgenticMode = true, FunctionConfigurationIds = [1],
            MaxAgenticIterations = scenario.StartsWith("forced", StringComparison.Ordinal) || scenario.StartsWith("fallback", StringComparison.Ordinal) ? 1 : 5,
            Messages = [new Message { Role = MessageRole.User, Content = "look up" }]
        };
        var middleware = new UsageTrackingMiddleware(async ctx =>
        {
            var result = await endpoint.CreateChatCompletion(request, ctx.RequestAborted);
            await result.ExecuteAsync(ctx);
        }, NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, pricing, batch, writer.Object, Mock.Of<IVirtualKeyRuntimeService>(),
            Mock.Of<IBillingAuditService>(), Mock.Of<IToolCostCalculationService>());

        var snapshot = context.GetRequestAccountingSnapshot()!;
        var incomplete = scenario != "cancel-after-function";
        var knownFunctionCost = expectedFunctionCalls * 0.05m;
        var knownTotal = knownProviderCost + knownFunctionCost;
        Assert.Equal(expectedProviderCalls, providerCalls);
        Assert.Equal(expectedFunctionCalls, functionCalls);
        Assert.Equal(Enumerable.Range(1, expectedProviderCalls), snapshot.ProviderInvocations);
        Assert.Equal(Enumerable.Range(1, expectedProviderCalls), snapshot.StreamingProviderInvocations);
        int[] completedInvocations = scenario == "earlier-null" ? [1, 2] :
            expectedProviderCalls > 1 || scenario == "cancel-after-function" ? [1] : [];
        Assert.Equal(completedInvocations, snapshot.CompletedStreamingProviderInvocations);
        Assert.Equal(incomplete, snapshot.HasIncompleteProviderUsage);
        Assert.Equal(incomplete, snapshot.IsIndeterminate);
        Assert.Equal(!incomplete, snapshot.Reservation!.Closed);
        Assert.True(snapshot.Reservation.InvocationStarted);
        Assert.Equal(knownFunctionCost, snapshot.FunctionExecutionCost);
        client.Verify(service => service.StreamChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()), Times.Exactly(expectedProviderCalls));
        orchestration.Verify(service => service.ExecuteToolCallsAsync(It.IsAny<List<ToolCall>>(), account.KeyId,
            It.IsAny<Dictionary<string, FunctionRoute>>(), It.IsAny<string>(), It.IsAny<Guid>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedFunctionCalls));
        if (knownTotal > 0m)
        {
            Assert.NotNull(snapshot.ProviderPricing);
            Assert.Equal(knownProviderCost, snapshot.ProviderPricing.KnownCost);
            Assert.Equal(incomplete ? null : (decimal?)knownProviderCost, snapshot.ProviderPricing.Cost);
            var log = Assert.Single(logs);
            Assert.Equal(knownTotal, log.Cost);
            Assert.Equal(BilledAt, log.BilledAtUtc);
            if (expectedFunctionCalls > 0)
            {
                Assert.Equal(executionId, Assert.Single(snapshot.FunctionExecutions).FunctionExecutionId);
                var breakdown = log.Metadata!["costBreakdown"];
                if (incomplete)
                {
                    Assert.Equal(JsonValueKind.Null, breakdown.GetProperty("llmCost").ValueKind);
                    Assert.Equal(knownProviderCost, breakdown.GetProperty("knownLlmCost").GetDecimal());
                }
                else Assert.Equal(knownProviderCost, breakdown.GetProperty("llmCost").GetDecimal());
                Assert.Equal(knownFunctionCost, breakdown.GetProperty("functionCost").GetDecimal());
                Assert.Equal(knownTotal, breakdown.GetProperty("totalCost").GetDecimal());
            }
            var call = Assert.Single(snapshot.ProviderCalls);
            Assert.Equal(scenario == "earlier-null" ? 2 : 1, call.Iteration);
            Assert.Equal(knownProviderCost, call.Usage.ProviderReportedCostUsd);
        }
        if (scenario == "cumulative-repeated")
        {
            Assert.Equal(10, Assert.Single(snapshot.ProviderCalls).Usage.PromptTokens);
            Assert.Equal(5, snapshot.ProviderUsage!.Usage.CompletionTokens);
            Assert.Equal(15, snapshot.ProviderUsage.Usage.TotalTokens);
        }
        if (scenario == "context-clone")
            manager.Verify(service => service.ManageContextAsync(It.IsAny<ChatCompletionRequest>(), 100), Times.Once);
        if (scenario.StartsWith("client-write", StringComparison.Ordinal)) Assert.Equal(1, body.FailedWrites);
        if (scenario == "client-write-dispose")
            Assert.Equal("client write failed after provider evidence was received",
                Assert.IsType<IOException>(Assert.Single(endpointLogger.StreamFailures)).Message);
        if (scenario == "provider-dispose-usage")
            Assert.Equal("provider enumerator disposal failed",
                Assert.IsType<IOException>(Assert.Single(endpointLogger.StreamFailures)).Message);
        if (scenario is "cancel-after-function" or "canceled-with-usage-chunk") Assert.True(canceled.IsCancellationRequested);
        if (expectedProviderCalls > 1 || knownProviderCost > 0m)
            estimator.Verify(service => service.EstimateUsageFromStreamingResponseAsync(
                It.IsAny<string>(), It.IsAny<List<Message>>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<Tool>?>(),
                It.IsAny<CancellationToken>()), Times.Never);

        SpendReservationReconciliation? expectedEvidence = null;
        if (incomplete)
        {
            expectedEvidence = await batch.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId);
            Assert.NotNull(expectedEvidence);
            Assert.Equal(knownTotal, expectedEvidence.KnownAmount);
            Assert.Equal(Bound - knownTotal, expectedEvidence.HeldAmount);
            Assert.Equal(BillingWindow, expectedEvidence.BillingWindowStartUtc);
            Assert.False(expectedEvidence.Resolved);
            Assert.Equal(Bound, await batch.GetPendingSpendAsync(account.KeyId));
            Assert.Equal(SpendReservationSettlementStatus.AlreadySettled,
                (await reservations.RecordKnownIndeterminateSpendAsync(account.KeyId, snapshot.BillingRequestId,
                    knownTotal, "same streaming subtotal replayed", BilledAt.AddDays(1))).Status);
            Assert.Equal(SpendReservationSettlementStatus.Conflict,
                (await reservations.SettleAsync(account.KeyId, snapshot.BillingRequestId, knownTotal)).Status);
        }
        else Assert.Null(await batch.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId));
        await batch.FlushPendingUpdatesAsync();
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, knownTotal);
        Assert.Equal(incomplete ? Bound - knownTotal : 0m, await batch.GetPendingSpendAsync(account.KeyId));
        await using var db = fixture.CreateDbContext();
        var group = await db.VirtualKeyGroups.SingleAsync(group => group.Id == account.GroupId);
        Assert.Equal(100m - knownTotal, group.Balance);
        Assert.Equal(knownTotal, group.LifetimeSpent);
        var debits = await db.VirtualKeyGroupTransactions.Where(transaction => transaction.VirtualKeyGroupId == account.GroupId &&
            transaction.TransactionType == TransactionType.Debit).ToListAsync();
        Assert.Equal(knownTotal > 0m ? 1 : 0, debits.Count);
        Assert.Equal(knownTotal, debits.Sum(transaction => transaction.Amount));
        Assert.All(debits, transaction => Assert.Equal(BillingWindow, transaction.BillingWindowStartUtc));
        if (incomplete)
        {
            await using var recovered = fixture.CreateBatchService();
            Assert.Equal(expectedEvidence,
                await recovered.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId));
            Assert.Equal(SpendReservationSettlementStatus.AlreadySettled,
                (await recovered.RecordIndeterminateSpendReservationAsync(account.KeyId, snapshot.BillingRequestId,
                    knownTotal, "restarted streaming finalizer", BilledAt.AddDays(2))).Status);
            await recovered.FlushPendingUpdatesAsync();
            Assert.Equal(knownTotal > 0m ? 1 : 0, await db.VirtualKeyGroupTransactions.CountAsync(transaction =>
                transaction.VirtualKeyGroupId == account.GroupId && transaction.TransactionType == TransactionType.Debit));
        }
        Assert.Equal(expectedProviderCalls, providerCalls);
        Assert.Equal(expectedFunctionCalls, functionCalls);
        await batch.StopAsync(CancellationToken.None);
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> ProviderStream(string scenario, int ordinal,
        CancellationTokenSource canceled, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (scenario is "client-write-usage" or "client-write-no-usage" or "canceled-with-usage-chunk")
        {
            if (scenario == "canceled-with-usage-chunk") canceled.Cancel();
            yield return Chunk(ordinal, false, scenario == "client-write-no-usage" ? null : 0.02m, "transport-body");
            yield break;
        }
        if (scenario == "intermediate-failure")
        {
            // Finish + usage is an intermediate cumulative snapshot, not proof of exhaustion.
            yield return Chunk(ordinal, false, 0.01m, "intermediate-body");
            await Task.Yield();
            throw new LLMCommunicationException("provider failed after intermediate usage", HttpStatusCode.ServiceUnavailable, null);
        }
        if (ordinal == 1)
        {
            yield return Chunk(ordinal, true, scenario == "earlier-null" ? null : scenario == "cumulative-repeated" ? 0.01m : 0.02m);
            if (scenario == "cumulative-repeated")
            {
                yield return Chunk(ordinal, false, 0.02m, usageOnly: true);
                yield return Chunk(ordinal, false, 0.02m, usageOnly: true);
            }
            yield break;
        }
        if (scenario == "earlier-null")
        {
            yield return Chunk(ordinal, false, 0.02m, "done");
            yield break;
        }
        if (!scenario.EndsWith("before-chunk", StringComparison.Ordinal))
        {
            yield return Chunk(ordinal, false, null, "partial-body", finish: false);
            await Task.Yield();
        }
        throw new LLMCommunicationException("later provider failed before usage", HttpStatusCode.ServiceUnavailable, null);
    }

    private static ChatCompletionChunk Chunk(int ordinal, bool toolCall, decimal? cost, string? content = null,
        bool usageOnly = false, bool finish = true) => new()
    {
        Id = $"stream-{ordinal}", Created = 0, Object = "chat.completion.chunk", Model = Model,
        Usage = cost.HasValue ? new Usage
        {
            PromptTokens = cost == 0.01m ? 5 : 10, CompletionTokens = cost == 0.01m ? 2 : 5,
            TotalTokens = cost == 0.01m ? 7 : 15, ProviderReportedCostUsd = cost
        } : null,
        Choices = usageOnly ? [] : [new StreamingChoice
        {
            Index = 0, FinishReason = finish ? toolCall ? FinishReason.ToolCalls : FinishReason.Stop : null,
            Delta = new DeltaContent
            {
                Content = content, ToolCalls = toolCall ? [new ToolCallChunk
                {
                    Index = 0, Id = $"call-{ordinal}", Type = "function",
                    Function = new FunctionCallChunk { Name = "lookup", Arguments = "{}" }
                }] : null
            }
        }]
    };

    private sealed class DisposeFailureEnumerable(ChatCompletionChunk chunk) : IAsyncEnumerable<ChatCompletionChunk>
    {
        public IAsyncEnumerator<ChatCompletionChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(chunk);
        private sealed class Enumerator(ChatCompletionChunk chunk) : IAsyncEnumerator<ChatCompletionChunk>
        {
            private bool _yielded;
            public ChatCompletionChunk Current => chunk;
            public ValueTask<bool> MoveNextAsync()
            {
                var result = !_yielded;
                _yielded = true;
                return ValueTask.FromResult(result);
            }
            public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("provider enumerator disposal failed"));
        }
    }

    private sealed class TransportFailureStream(bool failWrites, CancellationTokenSource canceled) : MemoryStream
    {
        public int FailedWrites { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (failWrites && FailedWrites == 0 && Encoding.UTF8.GetString(buffer.Span).Contains("transport-body", StringComparison.Ordinal))
            {
                FailedWrites++;
                canceled.Cancel();
                return ValueTask.FromException(new IOException("client write failed after provider evidence was received"));
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private static async Task<BatchSpendUpdateService> StartBatchAsync(NpgsqlDataSource dataSource, RedisConnectionFactory redisFactory)
    {
        var startup = new StartupLogger();
        var batch = new BatchSpendUpdateService(Mock.Of<IServiceScopeFactory>(), redisFactory,
            Options.Create(new BatchSpendingOptions { FlushIntervalSeconds = 3600, MaximumIntervalSeconds = 7200 }),
            startup, Mock.Of<IBillingAlertingService>(), runtimeStore: new NpgsqlVirtualKeyRuntimeStore(dataSource));
        try
        {
            await batch.StartAsync(CancellationToken.None);
            await startup.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(batch.IsHealthy);
            return batch;
        }
        catch
        {
            await batch.DisposeAsync();
            throw;
        }
    }

    private sealed class StartupLogger : ILogger<BatchSpendUpdateService>
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message == "No pending spend updates to flush" ||
                message.StartsWith("Flushed ", StringComparison.Ordinal) &&
                message.EndsWith("pending updates from previous session", StringComparison.Ordinal))
                Completed.TrySetResult();
            else if (message == "Error flushing pending updates on startup")
                Completed.TrySetException(exception ?? new InvalidOperationException(message));
        }
    }

    private sealed class EndpointLogger : ILogger<ChatEndpoints>
    {
        public List<Exception> StreamFailures { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null && formatter(state, exception) == "Error in stream processing")
                StreamFailures.Add(exception);
        }
    }
}
