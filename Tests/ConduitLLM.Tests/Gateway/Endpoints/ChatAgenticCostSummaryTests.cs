using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
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
        foreach (var scenario in new[] { "success", "failed-function", "forced-final", "fallback", "tiered", "markup", "zero", "hosted-tools", "tool-pricing-failure", "pricing-failure" })
            yield return [path, scenario];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Serialized_summary_matches_settlement_without_repricing(string path, string scenario)
    {
        var fallback = scenario == "fallback";
        var pricingFailure = scenario == "pricing-failure";
        var toolPricingFailure = scenario == "tool-pricing-failure";
        using var toolDatabase = scenario is "hosted-tools" or "tool-pricing-failure" ? new SqliteTestDatabase() : null;
        IToolCostCalculationService toolPricing = Mock.Of<IToolCostCalculationService>();
        if (toolDatabase is not null)
        {
            if (!toolPricingFailure)
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
        var functionCost = fallback || pricingFailure ? 0m : 0.05m;
        var firstUsage = new Usage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15,
            ProviderReportedCostUsd = scenario == "markup" ? 0.01m : scenario == "zero" ? 0m : null };
        var secondUsage = new Usage { PromptTokens = 20, CompletionTokens = 10, TotalTokens = 30,
            ProviderReportedCostUsd = scenario == "zero" ? 0m : null };
        var client = new Mock<ILLMClient>();
        client.SetupSequence(service => service.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(firstUsage, toolCall: true))
            .ReturnsAsync(Response(secondUsage, toolCall: false, hostedTool: toolDatabase is not null));
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
        var provider = new Provider { ProviderType = ProviderType.OpenAI,
            TrustProviderReportedCosts = scenario is "markup" or "zero", ProviderCostMarkupMultiplier = 2m };
        var mappings = new Mock<IModelProviderMappingService>();
        mappings.Setup(service => service.GetMappingByModelAliasAsync("test-model"))
            .ReturnsAsync(new ModelProviderMapping { ProviderId = 1, Provider = provider,
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 17 } });
        mappings.Setup(service => service.GetMappingByIdAsync(23))
            .ReturnsAsync(new ModelProviderMapping { Id = 23, ProviderId = 1, Provider = provider,
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 23 } });
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
        reservations.Setup(service => service.SettleAsync(42, It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()))
            .Callback<int, string, decimal, DateTime?>((_, _, cost, _) => settledCost = cost)
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
        var endpoints = new ChatEndpoints(new Conduit(factory.Object, NullLogger<Conduit>.Instance,
            functionDiscoveryService: discovery.Object, agenticOrchestrationService: orchestration.Object),
            NullLogger<ChatEndpoints>.Instance, mappings.Object, GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(),
            settings.Object, Mock.Of<IUsageEstimationService>(), configs.Object,
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
                Model = "test-model", FunctionConfigurationIds = [1], EnableAgenticMode = true,
                MaxAgenticIterations = scenario == "forced-final" ? 1 : 5,
                Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
            });
            await result.ExecuteAsync(ctx);
            // Billing must reuse the response's immutable price even if rates subsequently change.
            modelCost.InputCostPerMillionTokens = 999m;
            modelCost.OutputCostPerMillionTokens = 999m;
            if (toolDatabase is not null && !toolPricingFailure)
            {
                await using var changed = toolDatabase.CreateContext();
                var tool = changed.ProviderTools.Single();
                tool.CostPerUnit = 999m;
                await changed.SaveChangesAsync();
            }
        }, NullLogger<UsageTrackingMiddleware>.Instance);
        await middleware.InvokeAsync(context, pricing, Mock.Of<IBatchSpendUpdateService>(), writer.Object,
            Mock.Of<IVirtualKeyRuntimeService>(), audit.Object, toolPricing);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        var metrics = body.RootElement.GetProperty("agentic_metrics");
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(functionCost, metrics.GetProperty("total_function_cost").GetDecimal());
        Assert.False(metrics.TryGetProperty("provider_calls", out _));
        Assert.Equal(2, context.GetRequestAccountingSnapshot()!.ProviderCalls.Count);
        Assert.Null(firstUsage.ProviderCostPolicy);
        Assert.Null(secondUsage.ProviderCostPolicy);
        Assert.Equal(scenario == "zero" ? 0m : (decimal?)null, secondUsage.ProviderReportedCostUsd);
        client.Verify(service => service.CreateChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        modelCosts.Verify(service => service.GetCostByIdAsync(23, It.IsAny<CancellationToken>()),
            Times.Exactly(scenario == "zero" ? 0 : scenario is "markup" or "pricing-failure" ? 1 : 2));
        modelCosts.Verify(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()), Times.Never);
        modelCosts.Verify(service => service.GetCostForModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        if (pricingFailure || toolPricingFailure)
        {
            Assert.Equal(toolPricingFailure, metrics.TryGetProperty("total_llm_cost", out var knownLlmCost));
            if (toolPricingFailure) Assert.Equal(0.000225m, knownLlmCost.GetDecimal());
            Assert.False(metrics.TryGetProperty("total_cost", out _));
            Assert.Null(settledCost);
            Assert.False(context.GetRequestAccountingSnapshot()!.Reservation!.Closed);
            Assert.True(context.GetRequestAccountingSnapshot()!.IsIndeterminate);
            if (pricingFailure) Assert.Contains(audits, item => item.EventType == BillingAuditEventType.PricingCalculationFailed);
        }
        else
        {
            var expectedLlmCost = scenario switch { "tiered" => 0.000375m, "markup" => 0.020150m, "zero" => 0m, _ => 0.000225m };
            var summaryCost = metrics.GetProperty("total_cost").GetDecimal();
            Assert.Equal(expectedLlmCost, metrics.GetProperty("total_llm_cost").GetDecimal());
            Assert.Equal(expectedLlmCost + functionCost + (scenario == "hosted-tools" ? 0.06m : 0m), summaryCost);
            Assert.Equal(summaryCost, settledCost);
            Assert.Equal(summaryCost, Assert.Single(logs).Cost);
            Assert.True(context.GetRequestAccountingSnapshot()!.Reservation!.Closed);
        }
        reservations.Verify(service => service.SettleAsync(42, It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()),
            pricingFailure || toolPricingFailure ? Times.Never() : Times.Once());
        if (!fallback)
        {
            var function = Assert.Single(metrics.GetProperty("function_calls").EnumerateArray());
            Assert.Equal(executionId, function.GetProperty("function_execution_id").GetGuid());
            Assert.Equal(scenario != "failed-function", function.GetProperty("success").GetBoolean());
        }
    }

    private static ChatCompletionResponse Response(Usage usage, bool toolCall, bool hostedTool = false) => new()
    {
        Id = "response", Created = 0, Object = "chat.completion", Model = "test-model", Usage = usage,
        ProviderToolUsage = hostedTool ? new ProviderToolUsage
        {
            Tools = [new ProviderToolUsageItem { ToolName = "browser_search", Count = 2 }]
        } : null,
        Choices = [new Choice { Index = 0, FinishReason = toolCall ? FinishReason.ToolCalls : FinishReason.Stop,
            Message = new Message { Role = MessageRole.Assistant, Content = toolCall ? null : "done",
                ToolCalls = toolCall ? [new ToolCall { Id = "call-1", Type = "function",
                    Function = new FunctionCall { Name = "lookup", Arguments = "{}" } }] : null } }]
    };
}
