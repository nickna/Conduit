using System.Net;
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
public sealed class NonStreamingInvocationReservationTests(BillingFaultFixture fixture)
{
    private const string Model = "nonstream-invocation-fault";
    private const decimal ReservedAmount = 0.25m;
    private static readonly DateTime BilledAt = new(2026, 7, 24, 23, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime BillingWindow = BilledAt.Date.AddHours(23);

    public static IEnumerable<object[]> Scenarios()
    {
        yield return ["normal-null", 2, 1, 0.02m];
        yield return ["normal-failed", 2, 1, 0.02m];
        yield return ["forced-null", 2, 1, 0.02m];
        yield return ["forced-failed", 2, 1, 0.02m];
        yield return ["fallback-null", 2, 0, 0.02m];
        yield return ["fallback-failed", 2, 0, 0.02m];
        yield return ["earlier-null", 2, 1, 0.02m];
        yield return ["all-null", 2, 1, 0m];
        yield return ["fallback-limit-forced", 3, 0, 0.05m];
        yield return ["cancel-after-function", 1, 1, 0.02m];
        yield return ["cancel-before-invocation", 0, 0, 0m];
        yield return ["normal-null-clone", 2, 1, 0.02m];
        yield return ["normal-null-hosted", 2, 1, 0.02m];
        yield return ["normal-failed-hosted", 2, 1, 0.02m];
    }

    [Theory(Timeout = 90_000)]
    [MemberData(nameof(Scenarios))]
    public async Task RealCoreAndEndpoint_PreserveEveryInvocationAndDebitKnownWorkOnce(
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
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = canceled.Token };
        context.Request.Path = "/v1/conduit/chat/completions";
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Items["VirtualKeyId"] = account.KeyId;
        context.Items["Conduit.BillingOccurredAtUtc"] = BilledAt;

        var providerCalls = 0;
        var client = new Mock<ILLMClient>();
        client.Setup(service => service.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .Returns((ChatCompletionRequest _, string? _, CancellationToken _) =>
            {
                providerCalls++;
                if (providerCalls == 2 && scenario.Contains("failed", StringComparison.Ordinal))
                    return Task.FromException<ChatCompletionResponse>(new LLMCommunicationException(
                        "provider unavailable", HttpStatusCode.ServiceUnavailable,
                        "{\"error\":{\"message\":\"provider unavailable\"}}"));
                var first = providerCalls == 1;
                var fallbackLimit = scenario == "fallback-limit-forced";
                var toolCall = first || fallbackLimit && providerCalls == 2;
                decimal? cost = first
                    ? scenario is "earlier-null" or "all-null" ? null : 0.02m
                    : scenario == "earlier-null" ? 0.02m
                    : fallbackLimit && providerCalls == 2 ? 0.03m : null;
                var response = Response(providerCalls, toolCall, cost);
                if (first && scenario.Contains("hosted", StringComparison.Ordinal))
                    response.ProviderToolUsage = new ProviderToolUsage
                    {
                        Tools = [new ProviderToolUsageItem { ToolName = $"configured-native-{account.KeyId}", Count = 1 }]
                    };
                return Task.FromResult(response);
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
        var executionId = Guid.NewGuid();
        var functionCalls = 0;
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
                        Iteration = 1, FunctionName = "lookup", ToolCallId = "call-1",
                        Success = true, Cost = 0.05m, FunctionExecutionId = executionId
                    }],
                    ToolResultMessages = [new Message { Role = MessageRole.Tool, ToolCallId = "call-1", Content = "found" }]
                };
            });
        var provider = new Provider
        {
            ProviderType = ProviderType.OpenAI, TrustProviderReportedCosts = true,
            ProviderCostMarkupMultiplier = 1m
        };
        var mapping = new ModelProviderMapping
        {
            Id = 23, ProviderId = 1, Provider = provider,
            ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 17, MaxInputTokens = 100 }
        };
        var mappings = new Mock<IModelProviderMappingService>();
        mappings.Setup(service => service.GetMappingByModelAliasAsync(Model)).ReturnsAsync(mapping);
        mappings.Setup(service => service.GetMappingByIdAsync(23)).ReturnsAsync(mapping);
        var costs = new Mock<IModelCostService>();
        costs.Setup(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>())).ReturnsAsync((ModelCost?)null);
        var pricing = new CostCalculationService(costs.Object, NullLogger<CostCalculationService>.Instance);
        IToolCostCalculationService toolPricing = Mock.Of<IToolCostCalculationService>();
        var knownHostedCost = scenario.Contains("hosted", StringComparison.Ordinal) ? 0.03m : 0m;
        if (knownHostedCost > 0m)
        {
            await using var seed = fixture.CreateDbContext();
            seed.ProviderTools.Add(new ProviderTool
            {
                Provider = ProviderType.OpenAI, ToolName = $"configured-native-{account.KeyId}",
                BillingUnit = "requests", CostPerUnit = knownHostedCost, IsActive = true
            });
            await seed.SaveChangesAsync();
            var toolContexts = new Mock<IDbContextFactory<ConduitDbContext>>();
            toolContexts.Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => fixture.CreateDbContext());
            toolPricing = new ToolCostCalculationService(toolContexts.Object, NullLogger<ToolCostCalculationService>.Instance);
        }
        var estimator = new Mock<IUsageEstimationService>();
        estimator.Setup(service => service.EstimateUsageFromStreamingResponseAsync(
                It.IsAny<string>(), It.IsAny<List<Message>>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No authoritative estimate is available for these invocations"));
        var configs = new Mock<IFunctionConfigurationRepository>();
        configs.Setup(service => service.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionConfiguration { Id = 1, ConfigurationName = "lookup", IsEnabled = true }]);
        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(service => service.GetMaxAgenticIterationsAsync()).ReturnsAsync(5);
        settings.Setup(service => service.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
        var maximumSpend = new Mock<IChatSpendEstimator>();
        maximumSpend.Setup(service => service.EstimateMaximumCostAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatSpendEstimate(true, ReservedAmount, 10, 128, null));
        var manager = new Mock<IContextManager>();
        manager.Setup(service => service.ManageContextAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<int?>()))
            .ReturnsAsync((ChatCompletionRequest request, int? _) => new ChatCompletionRequest
            {
                Model = request.Model, Messages = request.Messages.ToList(), FunctionConfigurationIds = request.FunctionConfigurationIds,
                EnableAgenticMode = request.EnableAgenticMode, MaxAgenticIterations = request.MaxAgenticIterations
            });
        var core = new Conduit(factory.Object, NullLogger<Conduit>.Instance,
            contextManager: scenario == "normal-null-clone" ? manager.Object : null,
            modelProviderMappingService: mappings.Object,
            contextOptions: Options.Create(new ContextManagementOptions()),
            functionDiscoveryService: discovery.Object, agenticOrchestrationService: orchestration.Object);
        var endpoint = new ChatEndpoints(core, NullLogger<ChatEndpoints>.Instance, mappings.Object,
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(), settings.Object, estimator.Object, configs.Object,
            chatSpendEstimator: maximumSpend.Object, spendReservationService: reservations,
            billingAdmissionOptions: Options.Create(new BillingAdmissionOptions { Mode = BillingAdmissionMode.Enforce }),
            httpContextAccessor: new HttpContextAccessor { HttpContext = context }, costCalculationService: pricing,
            toolCostCalculationService: toolPricing);
        var logs = new List<LogRequestDto>();
        var writer = new Mock<IRequestLogRuntimeWriter>();
        writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
            .Callback<LogRequestDto>(logs.Add).Returns(Task.CompletedTask);
        var audits = new List<BillingAuditEvent>();
        var audit = new Mock<IBillingAuditService>();
        audit.Setup(service => service.LogBillingEvent(It.IsAny<BillingAuditEvent>()))
            .Callback<BillingAuditEvent>(audits.Add);
        var request = new ChatCompletionRequest
        {
            Model = Model, EnableAgenticMode = true, FunctionConfigurationIds = [1],
            MaxAgenticIterations = scenario.StartsWith("forced", StringComparison.Ordinal) || scenario == "fallback-limit-forced" ? 1 : 5,
            Messages = [new Message { Role = MessageRole.User, Content = "look up" }]
        };
        var middleware = new UsageTrackingMiddleware(async ctx =>
        {
            var result = await endpoint.CreateChatCompletion(request, ctx.RequestAborted);
            await result.ExecuteAsync(ctx);
        }, NullLogger<UsageTrackingMiddleware>.Instance);
        if (scenario == "cancel-before-invocation") canceled.Cancel();

        Task Invoke() => middleware.InvokeAsync(context, pricing, batch, writer.Object,
            Mock.Of<IVirtualKeyRuntimeService>(), audit.Object, toolPricing);
        if (scenario.StartsWith("cancel", StringComparison.Ordinal))
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke);
            Assert.Equal(canceled.Token, exception.CancellationToken);
        }
        else
        {
            await Invoke();
            Assert.Equal(scenario.Contains("failed", StringComparison.Ordinal) ? 503 : 200, context.Response.StatusCode);
        }

        var snapshot = context.GetRequestAccountingSnapshot()!;
        var knownFunctionCost = expectedFunctionCalls * 0.05m;
        var knownTotal = knownProviderCost + knownFunctionCost + knownHostedCost;
        var incomplete = !scenario.StartsWith("cancel", StringComparison.Ordinal);
        Assert.Equal(expectedProviderCalls, providerCalls);
        Assert.Equal(expectedFunctionCalls, functionCalls);
        client.Verify(service => service.CreateChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()), Times.Exactly(expectedProviderCalls));
        orchestration.Verify(service => service.ExecuteToolCallsAsync(It.IsAny<List<ToolCall>>(), account.KeyId,
            It.IsAny<Dictionary<string, FunctionRoute>>(), It.IsAny<string>(), It.IsAny<Guid>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedFunctionCalls));
        Assert.Equal(Enumerable.Range(1, expectedProviderCalls), snapshot.ProviderInvocations);
        Assert.Equal(incomplete, snapshot.HasIncompleteProviderUsage);
        Assert.Equal(knownFunctionCost, snapshot.FunctionExecutionCost);
        Assert.Equal(incomplete, snapshot.IsIndeterminate);
        Assert.Equal(!incomplete, snapshot.Reservation!.Closed);
        if (expectedProviderCalls > 0)
        {
            Assert.NotNull(snapshot.ProviderPricing);
            Assert.Equal(knownProviderCost, snapshot.ProviderPricing.KnownCost);
            Assert.Equal(incomplete ? null : (decimal?)knownProviderCost, snapshot.ProviderPricing.Cost);
            var log = Assert.Single(logs);
            Assert.Equal(knownTotal, log.Cost);
            Assert.Equal(BilledAt, log.BilledAtUtc);
            if (expectedFunctionCalls > 0)
            {
                var breakdown = log.Metadata!["costBreakdown"];
                if (incomplete)
                {
                    Assert.Equal(JsonValueKind.Null, breakdown.GetProperty("llmCost").ValueKind);
                    Assert.Equal(knownProviderCost, breakdown.GetProperty("knownLlmCost").GetDecimal());
                }
                else Assert.Equal(knownProviderCost, breakdown.GetProperty("llmCost").GetDecimal());
                Assert.Equal(knownFunctionCost, breakdown.GetProperty("functionCost").GetDecimal());
                Assert.Equal(knownTotal, breakdown.GetProperty("totalCost").GetDecimal());
                Assert.Equal(executionId, Assert.Single(snapshot.FunctionExecutions).FunctionExecutionId);
            }
            if (knownHostedCost > 0m)
            {
                Assert.Single(snapshot.ProviderToolUsage!.Tools);
                Assert.Equal(knownHostedCost, snapshot.ProviderToolPricing!.Cost);
            }
        }
        else
        {
            Assert.Empty(logs);
            Assert.DoesNotContain(audits, item => item.EventType is BillingAuditEventType.MissingUsageData or
                BillingAuditEventType.PricingCalculationFailed);
        }
        if (scenario == "earlier-null") Assert.Equal(2, Assert.Single(snapshot.ProviderCalls).Iteration);
        if (scenario == "fallback-limit-forced")
            Assert.Equal(new[] { 1, 2 }, snapshot.ProviderCalls.Select(call => call.Iteration));
        if (scenario == "normal-null-clone")
            manager.Verify(service => service.ManageContextAsync(It.IsAny<ChatCompletionRequest>(), 100), Times.Once);

        SpendReservationReconciliation? expectedEvidence = null;
        if (incomplete)
        {
            var evidence = await batch.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId);
            expectedEvidence = evidence;
            Assert.NotNull(evidence);
            Assert.Equal(knownTotal, evidence.KnownAmount);
            Assert.Equal(ReservedAmount - knownTotal, evidence.HeldAmount);
            Assert.Equal(BillingWindow, evidence.BillingWindowStartUtc);
            Assert.False(evidence.Resolved);
            Assert.Equal(ReservedAmount, await batch.GetPendingSpendAsync(account.KeyId));
            Assert.Equal(SpendReservationSettlementStatus.AlreadySettled,
                (await reservations.RecordKnownIndeterminateSpendAsync(account.KeyId, snapshot.BillingRequestId,
                    knownTotal, "replayed finalization", BilledAt.AddDays(1))).Status);
            Assert.Equal(SpendReservationSettlementStatus.Conflict,
                (await reservations.SettleAsync(account.KeyId, snapshot.BillingRequestId, knownTotal)).Status);
        }
        else
        {
            Assert.Null(await batch.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId));
        }
        await batch.FlushPendingUpdatesAsync();
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, knownTotal);
        Assert.Equal(incomplete ? ReservedAmount - knownTotal : 0m, await batch.GetPendingSpendAsync(account.KeyId));
        await using var db = fixture.CreateDbContext();
        var group = await db.VirtualKeyGroups.SingleAsync(group => group.Id == account.GroupId);
        Assert.Equal(100m - knownTotal, group.Balance);
        Assert.Equal(knownTotal, group.LifetimeSpent);
        var debits = await db.VirtualKeyGroupTransactions.Where(transaction => transaction.VirtualKeyGroupId == account.GroupId &&
            transaction.TransactionType == TransactionType.Debit).ToListAsync();
        Assert.Equal(knownTotal == 0m ? 0 : 1, debits.Count);
        Assert.Equal(knownTotal, debits.Sum(transaction => transaction.Amount));
        Assert.All(debits, transaction => Assert.Equal(BillingWindow, transaction.BillingWindowStartUtc));
        if (incomplete)
        {
            await using var recovered = fixture.CreateBatchService();
            Assert.Equal(expectedEvidence,
                await recovered.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId));
            Assert.Equal(SpendReservationSettlementStatus.AlreadySettled,
                (await recovered.RecordIndeterminateSpendReservationAsync(account.KeyId, snapshot.BillingRequestId,
                    knownTotal, "recovered finalization", BilledAt.AddDays(2))).Status);
            await recovered.FlushPendingUpdatesAsync();
            Assert.Equal(1, await db.VirtualKeyGroupTransactions.CountAsync(transaction =>
                transaction.VirtualKeyGroupId == account.GroupId && transaction.TransactionType == TransactionType.Debit));
        }
        Assert.Equal(expectedProviderCalls, providerCalls);
        Assert.Equal(expectedFunctionCalls, functionCalls);
        await batch.StopAsync(CancellationToken.None);
    }

    private static ChatCompletionResponse Response(int ordinal, bool toolCall, decimal? reportedCost) => new()
    {
        Id = $"provider-{ordinal}", Created = 0, Object = "chat.completion", Model = Model,
        Usage = reportedCost.HasValue ? new Usage
        {
            PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15, ProviderReportedCostUsd = reportedCost
        } : null,
        Choices = [new Choice
        {
            Index = 0, FinishReason = toolCall ? FinishReason.ToolCalls : FinishReason.Stop,
            Message = new Message
            {
                Role = MessageRole.Assistant, Content = toolCall ? null : "done",
                ToolCalls = toolCall ? [new ToolCall
                {
                    Id = $"call-{ordinal}", Type = "function",
                    Function = new FunctionCall { Name = "lookup", Arguments = "{}" }
                }] : null
            }
        }]
    };

    private static async Task<BatchSpendUpdateService> StartBatchAsync(
        NpgsqlDataSource dataSource, RedisConnectionFactory redisFactory)
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
}
