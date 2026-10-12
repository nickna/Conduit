using System.Text.Json;
using System.Net;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Exceptions;
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
using ConduitLLM.Gateway.Utilities;
using ConduitLLM.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public class ChatAgenticCostSummaryTests
{
    public static IEnumerable<object[]> Scenarios()
    {
        foreach (var path in new[] { "/v1/chat/completions", "/v1/conduit/chat/completions" })
        foreach (var scenario in new[] { "success", "failed-function", "forced-final", "fallback", "tiered", "markup", "zero", "hosted-tools", "tool-pricing-failure", "pricing-failure", "partial-provider", "provider-failure-hosted-tools", "partial-hosted-tools" })
        foreach (var streaming in new[] { false, true })
            yield return [path, scenario, streaming];
        foreach (var path in new[] { "/v1/chat/completions", "/v1/conduit/chat/completions" })
        foreach (var scenario in new[] { "missing-second", "missing-first", "all-missing", "failed-provider",
            "throw-provider", "fallback-missing", "forced-final-missing", "fallback-limit-gap", "cancel-after-functions", "single-estimate",
            "route-unavailable", "failed-provider-route-unavailable" })
            yield return [path, scenario, false];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Serialized_summary_matches_settlement_without_repricing(string path, string scenario, bool streaming)
    {
        var missingCases = scenario is "missing-second" or "missing-first" or "all-missing" or "failed-provider" or
            "throw-provider" or "fallback-missing" or "forced-final-missing" or "fallback-limit-gap" or "failed-provider-route-unavailable";
        var routeUnavailable = scenario is "route-unavailable" or "failed-provider-route-unavailable";
        var incompletePricing = missingCases || routeUnavailable;
        var observerCase = missingCases || routeUnavailable || scenario is "cancel-after-functions" or "single-estimate";
        var fallback = scenario.StartsWith("fallback", StringComparison.Ordinal);
        var pricingFailure = scenario is "pricing-failure" or "partial-provider" or "provider-failure-hosted-tools";
        var toolPricingFailure = scenario is "tool-pricing-failure" or "partial-hosted-tools";
        using var toolDatabase = scenario is "hosted-tools" or "tool-pricing-failure" or "partial-hosted-tools" or "provider-failure-hosted-tools"
            ? new SqliteTestDatabase() : null;
        IToolCostCalculationService toolPricing = Mock.Of<IToolCostCalculationService>();
        if (toolDatabase is not null)
        {
            if (scenario != "tool-pricing-failure")
            {
                await using var seed = toolDatabase.CreateContext();
                seed.ProviderTools.Add(new ProviderTool
                {
                    Provider = ProviderType.OpenAI, ToolName = "browser_search",
                    BillingUnit = "requests", CostPerUnit = 0.03m, IsActive = true
                });
                await seed.SaveChangesAsync();
            }
            toolPricing = new ToolCostCalculationService(toolDatabase.CreateDbContextFactory(),
                NullLogger<ToolCostCalculationService>.Instance);
        }
        var functionCost = fallback || scenario == "single-estimate" ? 0m : 0.05m;
        var firstUsage = new Usage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15,
            ProviderReportedCostUsd = scenario is "markup" or "partial-provider" ? 0.01m : scenario == "zero" ? 0m : null };
        var secondUsage = new Usage { PromptTokens = 20, CompletionTokens = 10, TotalTokens = 30,
            ProviderReportedCostUsd = scenario == "zero" ? 0m : null };
        if (observerCase)
        {
            firstUsage.ProviderReportedCostUsd = 0.01m;
            secondUsage.ProviderReportedCostUsd = 0.015m;
        }
        using var cancellation = new CancellationTokenSource();
        var client = new Mock<ILLMClient>();
        client.SetupSequence(service => service.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(firstUsage, toolCall: true))
            .ReturnsAsync(Response(secondUsage, toolCall: false, hostedTool: toolDatabase is not null,
                partialHostedTool: scenario == "partial-hosted-tools"));
        if (observerCase)
        {
            var calls = 0;
            client.Setup(service => service.CreateChatCompletionAsync(
                    It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    calls++;
                    if (calls == 2 && scenario.StartsWith("failed-provider", StringComparison.Ordinal))
                        throw new LLMCommunicationException("later provider unavailable", HttpStatusCode.ServiceUnavailable, null);
                    if (calls == 2 && scenario == "throw-provider")
                        throw new InvalidOperationException("later provider exploded");
                    var missing = scenario == "all-missing" || (calls == 1 && scenario is "missing-first" or "single-estimate") ||
                        (calls == 2 && scenario is "missing-second" or "fallback-missing" or "forced-final-missing") ||
                        (calls == 3 && scenario == "fallback-limit-gap");
                    return Response(missing ? null : calls == 1 ? firstUsage : secondUsage,
                        toolCall: scenario != "single-estimate" && (calls == 1 || scenario == "fallback-limit-gap" && calls == 2),
                        hostedTool: routeUnavailable && calls == 1);
                });
        }
        client.SetupSequence(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .Returns(StreamingResponse(firstUsage, toolCall: true))
            .Returns(StreamingResponse(secondUsage, toolCall: false, hostedTool: toolDatabase is not null,
                partialHostedTool: scenario == "partial-hosted-tools"));
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientForChatAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => request.SelectedMappingId = 23)
            .ReturnsAsync(client.Object);
        var discovery = new Mock<IFunctionDiscoveryService>();
        discovery.Setup(service => service.GetToolsForFunctionConfigurationsAsync(
            It.IsAny<List<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        discovery.Setup(service => service.GetFunctionNameToIdMappingAsync(
            It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fallback ? [] : new Dictionary<string, FunctionRoute> { ["lookup"] = new(1) });
        var executionId = Guid.NewGuid();
        var orchestration = new Mock<IAgenticOrchestrationService>();
        orchestration.Setup(service => service.ExecuteToolCallsAsync(
            It.IsAny<List<ToolCall>>(), 42, It.IsAny<Dictionary<string, FunctionRoute>>(),
            It.IsAny<string>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticExecutionResult
            {
                AllSucceeded = scenario != "failed-function", TotalFunctionCost = functionCost,
                FunctionCallSummaries = [new FunctionCallSummary
                {
                    Iteration = 1, FunctionName = "lookup", ToolCallId = "call-1", Cost = functionCost,
                    Success = scenario != "failed-function", FunctionExecutionId = executionId,
                    ErrorMessage = scenario == "failed-function" ? "failed after incurring cost" : null
                }],
                ToolResultMessages = [new Message { Role = MessageRole.Tool, ToolCallId = "call-1", Content = "result" }]
            });
        if (scenario == "cancel-after-functions")
            orchestration.Setup(service => service.ExecuteToolCallsAsync(
                    It.IsAny<List<ToolCall>>(), 42, It.IsAny<Dictionary<string, FunctionRoute>>(),
                    It.IsAny<string>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    cancellation.Cancel();
                    return new AgenticExecutionResult
                    {
                        AllSucceeded = true, TotalFunctionCost = functionCost,
                        FunctionCallSummaries = [new FunctionCallSummary { Iteration = 1, FunctionName = "lookup",
                            ToolCallId = "call-1", Cost = functionCost, Success = true, FunctionExecutionId = executionId }]
                    };
                });
        var provider = new Provider { ProviderType = ProviderType.OpenAI,
            TrustProviderReportedCosts = observerCase || scenario is "markup" or "zero" or "partial-provider", ProviderCostMarkupMultiplier = 2m };
        var mappings = new Mock<IModelProviderMappingService>();
        mappings.Setup(service => service.GetMappingByModelAliasAsync("test-model"))
            .ReturnsAsync(new ModelProviderMapping { ProviderId = 1, Provider = provider,
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 17 } });
        mappings.Setup(service => service.GetMappingByIdAsync(23))
            .ReturnsAsync(new ModelProviderMapping { Id = 23, ProviderId = 1, Provider = provider,
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 23 } });
        if (routeUnavailable)
            mappings.Setup(service => service.GetMappingByIdAsync(23))
                .ThrowsAsync(new InvalidOperationException("selected mapping lookup is unavailable"));
        var modelCost = new ModelCost
        {
            Id = 23, IsActive = true, InputCostPerMillionTokens = 2.50m, OutputCostPerMillionTokens = 10m,
            PricingModel = scenario == "tiered" ? PricingModel.TieredTokens : PricingModel.Standard,
            PricingConfiguration = scenario == "tiered"
                ? "{\"tiers\":[{\"maxContext\":10,\"inputCost\":2.5,\"outputCost\":10},{\"maxContext\":20,\"inputCost\":5,\"outputCost\":20}]}"
                : null
        };
        var modelCosts = new Mock<IModelCostService>();
        var lookup = modelCosts.Setup(service => service.GetCostByIdAsync(23, It.IsAny<CancellationToken>()));
        if (pricingFailure) lookup.ThrowsAsync(new InvalidOperationException("pricing is unavailable"));
        else lookup.ReturnsAsync(modelCost);
        var pricing = new CostCalculationService(modelCosts.Object, NullLogger<CostCalculationService>.Instance);
        var reservations = new Mock<ISpendReservationService>();
        reservations.Setup(service => service.ReserveAsync(42, It.IsAny<string>(), 0.25m))
            .ReturnsAsync(new SpendReservationResult(SpendReservationOutcome.Reserved, 0.25m));
        reservations.Setup(service => service.MarkInvocationStartedAsync(42, It.IsAny<string>())).ReturnsAsync(true);
        decimal? settledCost = null;
        decimal? knownCost = null;
        reservations.Setup(service => service.SettleAsync(42, It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()))
            .Callback<int, string, decimal, DateTime?>((_, _, cost, _) => settledCost = cost)
            .ReturnsAsync(new SpendReservationSettlementResult(SpendReservationSettlementStatus.Settled, 0m));
        reservations.Setup(service => service.RecordKnownIndeterminateSpendAsync(42, It.IsAny<string>(),
                It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<DateTime?>()))
            .Callback<int, string, decimal, string, DateTime?>((_, _, cost, _, _) => knownCost = cost)
            .ReturnsAsync(new SpendReservationSettlementResult(SpendReservationSettlementStatus.Settled, 0m));
        using var services = new ServiceCollection().AddLogging()
            .ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions))
            .AddSingleton<ISpendReservationService>(reservations.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.Items["VirtualKeyId"] = 42;
        var maximumSpend = new Mock<IChatSpendEstimator>();
        maximumSpend.Setup(service => service.EstimateMaximumCostAsync(
            It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatSpendEstimate(true, 0.25m, 10, 128, null));
        var configs = new Mock<IFunctionConfigurationRepository>();
        configs.Setup(service => service.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionConfiguration { Id = 1, ConfigurationName = "lookup", IsEnabled = true }]);
        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(service => service.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
        settings.Setup(service => service.GetMaxAgenticIterationsAsync()).ReturnsAsync(5);
        var estimation = new Mock<IUsageEstimationService>();
        estimation.Setup(service => service.EstimateUsageFromStreamingResponseAsync(
                It.IsAny<string>(), It.IsAny<List<Message>>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<Tool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Usage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 });
        var endpoints = new ChatEndpoints(new Conduit(factory.Object, NullLogger<Conduit>.Instance,
            functionDiscoveryService: discovery.Object, agenticOrchestrationService: orchestration.Object),
            NullLogger<ChatEndpoints>.Instance, mappings.Object, GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(),
            settings.Object, estimation.Object, configs.Object,
            chatSpendEstimator: maximumSpend.Object, spendReservationService: reservations.Object,
            billingAdmissionOptions: Options.Create(new BillingAdmissionOptions { Mode = BillingAdmissionMode.Enforce }),
            httpContextAccessor: new HttpContextAccessor { HttpContext = context }, costCalculationService: pricing,
            toolCostCalculationService: toolPricing);
        var logs = new List<LogRequestDto>();
        var writer = new Mock<IRequestLogRuntimeWriter>();
        writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
            .Callback<LogRequestDto>(logs.Add).Returns(Task.CompletedTask);
        var audits = new List<BillingAuditEvent>();
        var audit = new Mock<IBillingAuditService>();
        audit.Setup(service => service.LogBillingEvent(It.IsAny<BillingAuditEvent>())).Callback<BillingAuditEvent>(audits.Add);
        var middleware = new UsageTrackingMiddleware(async ctx =>
        {
            var result = await endpoints.CreateChatCompletion(new ChatCompletionRequest
            {
                Model = "test-model", Stream = streaming, FunctionConfigurationIds = [1], EnableAgenticMode = true,
                MaxAgenticIterations = scenario is "forced-final" or "forced-final-missing" or "fallback-limit-gap" ? 1 : 5,
                Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
            }, cancellation.Token);
            await result.ExecuteAsync(ctx);
            // Billing must reuse the response's immutable price even if rates subsequently change.
            if (!streaming && !observerCase)
            {
                modelCost.InputCostPerMillionTokens = 999m;
                modelCost.OutputCostPerMillionTokens = 999m;
            }
            if (!streaming && toolDatabase is not null && scenario != "tool-pricing-failure")
            {
                await using var changed = toolDatabase.CreateContext();
                var tool = changed.ProviderTools.Single();
                tool.CostPerUnit = 999m;
                await changed.SaveChangesAsync();
            }
        }, NullLogger<UsageTrackingMiddleware>.Instance);
        async Task Invoke() => await middleware.InvokeAsync(context, pricing, Mock.Of<IBatchSpendUpdateService>(), writer.Object,
            Mock.Of<IVirtualKeyRuntimeService>(), audit.Object, toolPricing);
        if (scenario == "throw-provider")
            Assert.Equal("later provider exploded", (await Assert.ThrowsAsync<InvalidOperationException>(Invoke)).Message);
        else if (scenario == "cancel-after-functions")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke);
        else await Invoke();

        if (observerCase)
        {
            var snapshot = context.GetRequestAccountingSnapshot()!;
            var invocationCount = scenario == "fallback-limit-gap" ? 3 : scenario is "single-estimate" or "cancel-after-functions" ? 1 : 2;
            Assert.Equal(Enumerable.Range(1, invocationCount), snapshot.ProviderInvocations);
            Assert.Equal(missingCases, snapshot.HasIncompleteProviderUsage);
            Assert.Equal(incompletePricing, snapshot.IsIndeterminate);
            Assert.Equal(functionCost, snapshot.FunctionExecutionCost);
            var providerCost = scenario switch
            {
                "all-missing" or "route-unavailable" or "failed-provider-route-unavailable" => 0m,
                "missing-first" => 0.03m, "fallback-limit-gap" => 0.05m,
                "single-estimate" => 0.000075m, _ => 0.02m
            };
            Assert.Equal(providerCost, snapshot.ProviderPricing!.KnownCost);
            if (incompletePricing) Assert.Null(snapshot.ProviderPricing.Cost);
            else Assert.Equal(providerCost, snapshot.ProviderPricing.Cost);
            Assert.Equal(providerCost + functionCost, incompletePricing ? knownCost : settledCost);
            Assert.Equal(!incompletePricing, snapshot.Reservation!.Closed);
            var log = Assert.Single(logs);
            Assert.Equal(providerCost + functionCost, log.Cost);
            Assert.Equal(23, log.ModelProviderMappingId);
            if (incompletePricing && functionCost > 0m)
            {
                var metadata = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(JsonSerializer.Serialize(log.Metadata))!;
                Assert.Null(metadata.CostBreakdown!.LlmCost);
                Assert.Equal(providerCost, metadata.CostBreakdown.KnownLlmCost);
                if (routeUnavailable)
                {
                    Assert.Null(metadata.CostBreakdown.ProviderToolCost);
                    Assert.Single(metadata.ProviderToolUsage!.Tools);
                    Assert.NotNull(snapshot.ProviderToolPricing!.UsageJson);
                    Assert.False(context.Items.ContainsKey("ProviderId"));
                }
            }
            estimation.Verify(service => service.EstimateUsageFromStreamingResponseAsync(
                    It.IsAny<string>(), It.IsAny<List<Message>>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<Tool>>(), It.IsAny<CancellationToken>()),
                scenario == "single-estimate" ? Times.Once() : Times.Never());
            if (scenario is not "throw-provider" and not "cancel-after-functions")
            {
                Assert.Equal(scenario.StartsWith("failed-provider", StringComparison.Ordinal) ? 503 : 200, context.Response.StatusCode);
                context.Response.Body.Position = 0;
                using var responseBody = await JsonDocument.ParseAsync(context.Response.Body);
                if (incompletePricing && !scenario.StartsWith("failed-provider", StringComparison.Ordinal))
                {
                    var summary = responseBody.RootElement.GetProperty("agentic_metrics");
                    Assert.False(summary.TryGetProperty("total_llm_cost", out _));
                    Assert.False(summary.TryGetProperty("total_cost", out _));
                }
            }
            return;
        }

        context.Response.Body.Position = 0;
        using var body = streaming ? null : await JsonDocument.ParseAsync(context.Response.Body);
        var metrics = body?.RootElement.GetProperty("agentic_metrics");
        Assert.Equal(200, context.Response.StatusCode);
        if (metrics is { } serializedMetrics)
        {
            Assert.Equal(functionCost, serializedMetrics.GetProperty("total_function_cost").GetDecimal());
            Assert.False(serializedMetrics.TryGetProperty("provider_calls", out _));
        }
        Assert.Equal(2, context.GetRequestAccountingSnapshot()!.ProviderCalls.Count);
        Assert.Null(firstUsage.ProviderCostPolicy);
        Assert.Null(secondUsage.ProviderCostPolicy);
        Assert.Equal(scenario == "zero" ? 0m : (decimal?)null, secondUsage.ProviderReportedCostUsd);
        client.Verify(service => service.CreateChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()), streaming ? Times.Never() : Times.Exactly(2));
        client.Verify(service => service.StreamChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()), streaming ? Times.Exactly(2) : Times.Never());
        modelCosts.Verify(service => service.GetCostByIdAsync(23, It.IsAny<CancellationToken>()),
            Times.Exactly(scenario == "zero" ? 0 : scenario is "markup" or "partial-provider" ? 1 : 2));
        modelCosts.Verify(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()), Times.Never);
        modelCosts.Verify(service => service.GetCostForModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        if (pricingFailure || toolPricingFailure)
        {
            if (metrics is { } failedMetrics)
            {
                Assert.Equal(toolPricingFailure, failedMetrics.TryGetProperty("total_llm_cost", out var knownLlmCost));
                if (toolPricingFailure) Assert.Equal(0.000225m, knownLlmCost.GetDecimal());
                Assert.False(failedMetrics.TryGetProperty("total_cost", out _));
            }
            Assert.Null(settledCost);
            var expectedKnown = scenario switch
            {
                "partial-provider" => 0.07m, "provider-failure-hosted-tools" => 0.11m,
                "partial-hosted-tools" => 0.110225m, "tool-pricing-failure" => 0.050225m, _ => 0.05m
            };
            Assert.Equal(expectedKnown, knownCost);
            Assert.Equal(knownCost, Assert.Single(logs).Cost);
            Assert.False(context.GetRequestAccountingSnapshot()!.Reservation!.Closed);
            Assert.True(context.GetRequestAccountingSnapshot()!.IsIndeterminate);
            var pricingEvidence = context.GetRequestAccountingSnapshot()!.ProviderPricing!;
            if (pricingFailure)
            {
                Assert.Null(pricingEvidence.Cost);
                Assert.Equal(scenario == "partial-provider" ? 0.02m : 0m, pricingEvidence.KnownCost);
            }
            else Assert.Equal(pricingEvidence.Cost, pricingEvidence.KnownCost);
            if (scenario == "partial-hosted-tools")
                Assert.Equal(0.06m, context.GetRequestAccountingSnapshot()!.ProviderToolPricing!.KnownCost);
            if (pricingFailure) Assert.Contains(audits, item => item.EventType == BillingAuditEventType.PricingCalculationFailed);
        }
        else
        {
            var expectedLlmCost = scenario switch { "tiered" => 0.000375m, "markup" => 0.020150m, "zero" => 0m, _ => 0.000225m };
            var summaryCost = expectedLlmCost + functionCost + (scenario == "hosted-tools" ? 0.06m : 0m);
            if (metrics is { } completeMetrics)
            {
                Assert.Equal(expectedLlmCost, completeMetrics.GetProperty("total_llm_cost").GetDecimal());
                Assert.Equal(summaryCost, completeMetrics.GetProperty("total_cost").GetDecimal());
            }
            Assert.Equal(summaryCost, settledCost);
            Assert.Equal(summaryCost, Assert.Single(logs).Cost);
            Assert.True(context.GetRequestAccountingSnapshot()!.Reservation!.Closed);
        }
        reservations.Verify(service => service.SettleAsync(42, It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()),
            pricingFailure || toolPricingFailure ? Times.Never() : Times.Once());
        if (!fallback && metrics is { } functionMetrics)
        {
            var function = Assert.Single(functionMetrics.GetProperty("function_calls").EnumerateArray());
            Assert.Equal(executionId, function.GetProperty("function_execution_id").GetGuid());
            Assert.Equal(scenario != "failed-function", function.GetProperty("success").GetBoolean());
        }
    }

    private static ChatCompletionResponse Response(Usage? usage, bool toolCall, bool hostedTool = false, bool partialHostedTool = false) => new()
    {
        Id = "response", Created = 0, Object = "chat.completion", Model = "test-model", Usage = usage,
        ProviderToolUsage = hostedTool ? HostedUsage(partialHostedTool) : null,
        Choices = [new Choice { Index = 0, FinishReason = toolCall ? FinishReason.ToolCalls : FinishReason.Stop,
            Message = new Message { Role = MessageRole.Assistant, Content = toolCall ? null : "done",
                ToolCalls = toolCall ? [new ToolCall { Id = "call-1", Type = "function",
                    Function = new FunctionCall { Name = "lookup", Arguments = "{}" } }] : null } }]
    };

    private static ProviderToolUsage HostedUsage(bool partial)
    {
        var usage = new ProviderToolUsage { Tools = [new ProviderToolUsageItem { ToolName = "browser_search", Count = 2 }] };
        if (partial) usage.Tools.Add(new ProviderToolUsageItem { ToolName = "unconfigured", Count = 1 });
        return usage;
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> StreamingResponse(Usage usage, bool toolCall,
        bool hostedTool = false, bool partialHostedTool = false)
    {
        await Task.Yield();
        yield return new ChatCompletionChunk
        {
            Id = "chunk", Created = 0, Model = "test-model", Usage = usage,
            ProviderToolUsage = hostedTool ? HostedUsage(partialHostedTool) : null,
            Choices = [new StreamingChoice { Index = 0, FinishReason = toolCall ? FinishReason.ToolCalls : FinishReason.Stop,
                Delta = new DeltaContent { Content = toolCall ? null : "done",
                    ToolCalls = toolCall ? [new ToolCallChunk { Index = 0, Id = "call-1", Type = "function",
                        Function = new FunctionCallChunk { Name = "lookup", Arguments = "{}" } }] : null } }]
        };
    }
}
