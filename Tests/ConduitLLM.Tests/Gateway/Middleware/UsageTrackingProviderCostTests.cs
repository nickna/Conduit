using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Gateway.Middleware;

public class UsageTrackingProviderCostTests
{
    public static IEnumerable<object[]> CallCostCases()
    {
        (decimal? First, decimal? Second, decimal Markup, bool Trusted, decimal Expected)[] cases =
        [
            (0.01m, null, 1m, true, 0.010075m),
            (null, 0.01m, 1m, true, 0.010075m),
            (0m, null, 1m, true, 0.000075m),
            (null, null, 1m, true, 0.000150m),
            (0.01m, 0.02m, 1m, true, 0.03m),
            (0.01m, null, 2m, true, 0.020075m),
            (0.01m, 0.02m, 2m, true, 0.06m),
            (0.01m, 0.02m, 2m, false, 0.000150m)
        ];
        foreach (var streaming in new[] { false, true })
        foreach (var selectedCostId in new[] { false, true })
        foreach (var item in cases)
            yield return [streaming, selectedCostId, item.First, item.Second, item.Markup, item.Trusted, item.Expected];
    }

    [Theory]
    [MemberData(nameof(CallCostCases))]
    public async Task Agentic_calls_preserve_their_own_reported_cost_and_token_fallback(
        bool streaming, bool selectedCostId, decimal? firstCost, decimal? secondCost,
        decimal markup, bool trusted, decimal expectedCost)
    {
        var first = TokenUsage(firstCost);
        var second = TokenUsage(secondCost);
        var aggregateCost = firstCost.HasValue || secondCost.HasValue
            ? firstCost.GetValueOrDefault() + secondCost.GetValueOrDefault()
            : (decimal?)null;
        var aggregate = new Usage
        {
            PromptTokens = 20, CompletionTokens = 10, TotalTokens = 30,
            ProviderReportedCostUsd = aggregateCost
        };
        var policy = new ProviderCostBillingPolicy { TrustProviderReportedCost = trusted, MarkupMultiplier = markup };
        var fixture = await TrackAsync(streaming, selectedCostId, aggregate,
            [new() { Iteration = 1, Usage = first }, new() { Iteration = 2, Usage = second }],
            policy, aggregateCost);

        Assert.Equal(expectedCost, Assert.Single(fixture.Logs).Cost);
        Assert.Equal(expectedCost, Assert.Single(fixture.QueuedCosts));
        var tokenLookups = trusted
            ? (firstCost.HasValue ? 0 : 1) + (secondCost.HasValue ? 0 : 1)
            : 2;
        fixture.ModelCosts.Verify(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()),
            Times.Exactly(selectedCostId ? tokenLookups : 0));
        fixture.ModelCosts.Verify(service => service.GetCostForModelAsync("test-model", It.IsAny<CancellationToken>()),
            Times.Exactly(selectedCostId ? 0 : tokenLookups));
        Assert.Equal(firstCost, first.ProviderReportedCostUsd);
        Assert.Equal(secondCost, second.ProviderReportedCostUsd);
        Assert.Equal(aggregateCost, aggregate.ProviderReportedCostUsd);
        Assert.All(new[] { first, second, aggregate }, usage =>
        {
            Assert.Null(usage.ProviderCostPolicy);
            Assert.Null(usage.PricingFallbackReason);
        });
        Assert.False(fixture.Context.GetRequestAccountingSnapshot()!.IsIndeterminate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregate_only_usage_can_consume_the_reported_cost_side_channel(bool streaming)
    {
        var usage = TokenUsage(null);
        var fixture = await TrackAsync(streaming, selectedCostId: true, usage, [],
            new ProviderCostBillingPolicy { TrustProviderReportedCost = true, MarkupMultiplier = 2m },
            reportedCost: 0.01m);

        Assert.Equal(0.02m, Assert.Single(fixture.QueuedCosts));
        Assert.Equal(0.02m, Assert.Single(fixture.Logs).Cost);
        fixture.ModelCosts.Verify(service => service.GetCostByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Null(usage.ProviderReportedCostUsd);
        Assert.Null(usage.ProviderCostPolicy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Per_call_pricing_fallback_is_audited_without_mutating_provider_usage(bool streaming)
    {
        var first = new Usage { VideoDurationSeconds = 6.2, VideoResolution = "1280x720" };
        var second = new Usage { VideoDurationSeconds = 6.2, VideoResolution = "1280x720" };
        var fixture = await TrackAsync(streaming, selectedCostId: false,
            new Usage { VideoDurationSeconds = 12.4, VideoResolution = "1280x720" },
            // Deliberately provide unsorted evidence: audit labels must follow the priced call.
            [new() { Iteration = 2, Usage = second }, new() { Iteration = 1, Usage = first }],
            new ProviderCostBillingPolicy(), reportedCost: null,
            modelCost: new ModelCost
            {
                PricingModel = PricingModel.PerVideo,
                PricingConfiguration = "{\"rates\":{\"720p_5\":0.5}}",
                IsActive = true
            });

        Assert.Equal(1m, Assert.Single(fixture.QueuedCosts));
        var audit = Assert.Single(fixture.Audits, item => item.EventType == BillingAuditEventType.UsageEstimated);
        Assert.Contains("iteration 1: Missing per-video rate '720p_6'", audit.FailureReason);
        Assert.Contains("iteration 2: Missing per-video rate '720p_6'", audit.FailureReason);
        Assert.Null(first.PricingFallbackReason);
        Assert.Null(second.PricingFallbackReason);
    }

    private static Usage TokenUsage(decimal? reportedCost) => new()
    {
        PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15,
        ProviderReportedCostUsd = reportedCost
    };

    private static async Task<TrackedRequest> TrackAsync(
        bool streaming, bool selectedCostId, Usage aggregate, List<ProviderCallUsage> calls,
        ProviderCostBillingPolicy policy, decimal? reportedCost, ModelCost modelCost = null)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/v1/chat/completions";
        context.Items[HttpContextKeys.VirtualKeyId] = 42;
        context.Items["ProviderType"] = ProviderType.OpenAI;
        context.Items[HttpContextKeys.ProviderBillingPolicy] = policy;
        if (selectedCostId) context.Items[HttpContextKeys.ModelCostId] = 17;
        if (reportedCost.HasValue) context.Items[HttpContextKeys.ProviderReportedCost] = reportedCost.Value;
        var modelCosts = new Mock<IModelCostService>();
        modelCost ??= new ModelCost
        {
            Id = 17, IsActive = true, InputCostPerMillionTokens = 2.50m, OutputCostPerMillionTokens = 10m
        };
        modelCosts.Setup(service => service.GetCostForModelAsync("test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelCost);
        modelCosts.Setup(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelCost);
        var pricing = new CostCalculationService(modelCosts.Object, NullLogger<CostCalculationService>.Instance);
        var logs = new List<LogRequestDto>();
        var writer = new Mock<IRequestLogRuntimeWriter>();
        writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
            .Callback<LogRequestDto>(logs.Add).Returns(Task.CompletedTask);
        var queuedCosts = new List<decimal>();
        var batch = new Mock<IBatchSpendUpdateService>();
        batch.SetupGet(service => service.IsHealthy).Returns(true);
        batch.Setup(service => service.QueueSpendUpdateAsync(42, It.IsAny<decimal>(), It.IsAny<DateTime?>()))
            .Callback<int, decimal, DateTime?>((_, cost, _) => queuedCosts.Add(cost)).Returns(Task.CompletedTask);
        var audits = new List<BillingAuditEvent>();
        var audit = new Mock<IBillingAuditService>();
        audit.Setup(service => service.LogBillingEvent(It.IsAny<BillingAuditEvent>()))
            .Callback<BillingAuditEvent>(audits.Add);
        var middleware = new UsageTrackingMiddleware(ctx =>
        {
            ctx.Response.ContentType = streaming ? "text/event-stream" : "application/json";
            var accounting = ctx.GetOrCreateRequestAccountingContext();
            accounting.SetOperation(RequestOperation.ChatCompletion, 42, "test-model");
            accounting.RecordProviderUsage(aggregate, "test-model", UsageEvidenceSource.Provider);
            accounting.RecordProviderCalls(calls);
            return Task.CompletedTask;
        }, NullLogger<UsageTrackingMiddleware>.Instance);
        await middleware.InvokeAsync(context, pricing, batch.Object, writer.Object,
            Mock.Of<IVirtualKeyRuntimeService>(), audit.Object, Mock.Of<IToolCostCalculationService>());
        return new TrackedRequest(context, modelCosts, logs, queuedCosts, audits);
    }

    private sealed record TrackedRequest(
        HttpContext Context, Mock<IModelCostService> ModelCosts, List<LogRequestDto> Logs,
        List<decimal> QueuedCosts, List<BillingAuditEvent> Audits);
}
