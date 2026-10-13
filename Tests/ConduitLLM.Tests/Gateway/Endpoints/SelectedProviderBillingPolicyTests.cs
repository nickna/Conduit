using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Responses;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public class SelectedProviderBillingPolicyTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var path in new[] { "/v1/chat/completions", "/v1/conduit/chat/completions", "/v1/responses" })
        foreach (var streaming in new[] { false, true })
        foreach (var scenario in new[] { "trusted-to-untrusted", "untrusted-to-trusted", "markup", "zero", "fallback", "absent-provider" })
            yield return [path, streaming, scenario];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualSelectedProviderPolicy_DeterminesSummarySettlementAndLog(
        string path, bool streaming, string scenario)
    {
        var responses = path == "/v1/responses";
        var aliasProvider = new Provider { Id = 1, ProviderType = ProviderType.OpenAI,
            TrustProviderReportedCosts = scenario != "untrusted-to-trusted", ProviderCostMarkupMultiplier = 1m };
        var selectedProvider = scenario == "absent-provider" ? null : new Provider
        {
            Id = 2, ProviderType = ProviderType.OpenAI,
            TrustProviderReportedCosts = scenario != "trusted-to-untrusted", ProviderCostMarkupMultiplier = 2m
        };
        decimal? reported = scenario == "zero" ? 0m : scenario == "fallback" ? null : 0.01m;
        var rawUsage = new Usage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15,
            ProviderReportedCostUsd = reported };
        var expected = scenario switch { "zero" => 0m,
            "trusted-to-untrusted" or "fallback" or "absent-provider" => 0.001m, _ => 0.02m };
        var trustedPrice = selectedProvider?.TrustProviderReportedCosts == true && reported.HasValue;
        var client = new Mock<ILLMClient>();
        client.Setup(service => service.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse
            {
                Id = "response", Object = "chat.completion", Created = 0, Model = "routed-model", Usage = rawUsage,
                Choices = [new Choice { Index = 0, FinishReason = FinishReason.Stop,
                    Message = new Message { Role = MessageRole.Assistant, Content = "done" } }]
            });
        client.Setup(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>())).Returns(Stream(rawUsage));
        var mappings = new Mock<IModelProviderMappingService>();
        mappings.Setup(service => service.GetMappingByModelAliasAsync("routed-model"))
            .ReturnsAsync(new ModelProviderMapping { Id = 17, ProviderId = 1, Provider = aliasProvider,
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 17 } });
        mappings.Setup(service => service.GetMappingByIdAsync(23))
            .ReturnsAsync(new ModelProviderMapping { Id = 23, ProviderId = 2, Provider = selectedProvider,
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 23 } });
        var modelCost = new ModelCost { Id = 23, IsActive = true,
            InputCostPerMillionTokens = 50m, OutputCostPerMillionTokens = 100m };
        var modelCosts = new Mock<IModelCostService>();
        modelCosts.Setup(service => service.GetCostByIdAsync(23, It.IsAny<CancellationToken>())).ReturnsAsync(modelCost);
        modelCosts.Setup(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelCost { Id = 17, IsActive = true,
                InputCostPerMillionTokens = 2.5m, OutputCostPerMillionTokens = 10m });
        var pricing = new CostCalculationService(modelCosts.Object, NullLogger<CostCalculationService>.Instance);
        var reservations = new Mock<ISpendReservationService>();
        reservations.Setup(service => service.ReserveAsync(42, It.IsAny<string>(), 0.25m))
            .ReturnsAsync(new SpendReservationResult(SpendReservationOutcome.Reserved, 0.25m));
        reservations.Setup(service => service.MarkInvocationStartedAsync(42, It.IsAny<string>())).ReturnsAsync(true);
        decimal? settled = null;
        reservations.Setup(service => service.SettleAsync(42, It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()))
            .Callback<int, string, decimal, DateTime?>((_, _, cost, _) => settled = cost)
            .ReturnsAsync(new SpendReservationSettlementResult(SpendReservationSettlementStatus.Settled, 0m));
        using var services = new ServiceCollection().AddLogging()
            .ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions))
            .AddSingleton<ISpendReservationService>(reservations.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Items[HttpContextKeys.VirtualKeyId] = 42;
        context.Response.Body = new MemoryStream();
        // Even an untrusted default provider must remove an earlier request-local policy.
        context.Items[HttpContextKeys.ProviderBillingPolicy] = new ProviderCostBillingPolicy
        { TrustProviderReportedCost = true, MarkupMultiplier = 7m };
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientForChatAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) =>
            {
                if (aliasProvider.TrustProviderReportedCosts)
                    Assert.Equal(1m, Assert.IsType<ProviderCostBillingPolicy>(context.Items[HttpContextKeys.ProviderBillingPolicy]).MarkupMultiplier);
                else
                    Assert.False(context.Items.ContainsKey(HttpContextKeys.ProviderBillingPolicy));
                request.SelectedMappingId = 23;
            }).ReturnsAsync(client.Object);
        var discovery = new Mock<IFunctionDiscoveryService>();
        discovery.Setup(service => service.GetToolsForFunctionConfigurationsAsync(
            It.IsAny<List<int>>(), 42, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        discovery.Setup(service => service.GetFunctionNameToIdMappingAsync(
            It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var conduit = new Conduit(factory.Object, NullLogger<Conduit>.Instance,
            functionDiscoveryService: discovery.Object, agenticOrchestrationService: Mock.Of<IAgenticOrchestrationService>());
        var maximumSpend = new Mock<IChatSpendEstimator>();
        maximumSpend.Setup(service => service.EstimateMaximumCostAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatSpendEstimate(true, 0.25m, 10, 128, null));
        var admission = Options.Create(new BillingAdmissionOptions { Mode = BillingAdmissionMode.Enforce });
        var accessor = new HttpContextAccessor { HttpContext = context };
        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(service => service.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
        settings.Setup(service => service.GetMaxAgenticIterationsAsync()).ReturnsAsync(5);
        var configs = new Mock<IFunctionConfigurationRepository>();
        configs.Setup(service => service.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionConfiguration { Id = 1, ConfigurationName = "lookup", IsEnabled = true }]);
        var chat = new ChatEndpoints(conduit, NullLogger<ChatEndpoints>.Instance, mappings.Object,
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(), settings.Object, Mock.Of<IUsageEstimationService>(),
            configs.Object, chatSpendEstimator: maximumSpend.Object, spendReservationService: reservations.Object,
            billingAdmissionOptions: admission, httpContextAccessor: accessor, costCalculationService: pricing);
        var responseEndpoint = new ResponsesEndpoints(new ResponsesChatExecutor(conduit), mappings.Object,
            Mock.Of<IUsageEstimationService>(), GatewayJsonOptions.Create(), accessor,
            NullLogger<ResponsesEndpoints>.Instance, maximumSpend.Object, reservations.Object, admission);
        var logs = new List<LogRequestDto>();
        var writer = new Mock<IRequestLogRuntimeWriter>();
        writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
            .Callback<LogRequestDto>(logs.Add).Returns(Task.CompletedTask);
        var middleware = new UsageTrackingMiddleware(async ctx =>
        {
            var result = responses
                ? await responseEndpoint.CreateResponse(new CreateResponseRequest
                { Model = "routed-model", Input = JsonSerializer.SerializeToElement("hello"), Store = false, Stream = streaming })
                : await chat.CreateChatCompletion(new ChatCompletionRequest
                {
                    Model = "routed-model", Stream = streaming, FunctionConfigurationIds = [1],
                    EnableAgenticMode = true, MaxAgenticIterations = 5,
                    Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
                });
            await result.ExecuteAsync(ctx);
            if (!responses && !streaming)
            {
                // The serialized summary and middleware must share the selected price already calculated.
                modelCost.InputCostPerMillionTokens = 999m;
                modelCost.OutputCostPerMillionTokens = 999m;
            }
        }, NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, pricing, Mock.Of<IBatchSpendUpdateService>(), writer.Object,
            Mock.Of<IVirtualKeyRuntimeService>(), Mock.Of<IBillingAuditService>(), Mock.Of<IToolCostCalculationService>());

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(expected, settled);
        var log = Assert.Single(logs);
        Assert.Equal(expected, log.Cost);
        Assert.Equal(2, log.ProviderId);
        Assert.Equal(23, log.ModelProviderMappingId);
        Assert.Equal(trustedPrice ? RequestBillingMethod.ProviderReportedCost : RequestBillingMethod.ModelCost, log.BillingMethod);
        Assert.Equal(trustedPrice ? reported : null, log.ProviderReportedCostUsd);
        Assert.Equal(trustedPrice ? 2m : (decimal?)null, log.ProviderCostMarkupMultiplier);
        Assert.Null(rawUsage.ProviderCostPolicy);
        Assert.Null(rawUsage.PricingFallbackReason);
        Assert.Equal(reported, rawUsage.ProviderReportedCostUsd);
        var snapshot = context.GetRequestAccountingSnapshot()!;
        Assert.True(snapshot.Reservation!.Closed);
        Assert.False(snapshot.IsIndeterminate);
        Assert.Null(snapshot.ProviderUsage!.Usage.ProviderCostPolicy);
        Assert.Null(snapshot.ProviderUsage.Usage.PricingFallbackReason);
        Assert.All(snapshot.ProviderCalls, call => Assert.Null(call.Usage.ProviderCostPolicy));
        if (selectedProvider?.TrustProviderReportedCosts == true)
            Assert.Equal(2m, Assert.IsType<ProviderCostBillingPolicy>(context.Items[HttpContextKeys.ProviderBillingPolicy]).MarkupMultiplier);
        else
            Assert.False(context.Items.ContainsKey(HttpContextKeys.ProviderBillingPolicy));
        modelCosts.Verify(service => service.GetCostByIdAsync(23, It.IsAny<CancellationToken>()),
            trustedPrice ? Times.Never() : Times.Once());
        modelCosts.Verify(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()), Times.Never);
        reservations.Verify(service => service.SettleAsync(42, It.IsAny<string>(), expected, It.IsAny<DateTime?>()), Times.Once());
        if (!responses && !streaming)
        {
            context.Response.Body.Position = 0;
            using var body = await JsonDocument.ParseAsync(context.Response.Body);
            var metrics = body.RootElement.GetProperty("agentic_metrics");
            Assert.Equal(expected, metrics.GetProperty("total_llm_cost").GetDecimal());
            Assert.Equal(expected, metrics.GetProperty("total_cost").GetDecimal());
        }
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> Stream(Usage usage)
    {
        await Task.Yield();
        yield return new ChatCompletionChunk { Id = "chunk", Created = 0, Model = "routed-model", Usage = usage,
            Choices = [new StreamingChoice { Index = 0, FinishReason = FinishReason.Stop,
                Delta = new DeltaContent { Content = "done" } }] };
    }
}
