using ConduitLLM.Core.Models;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.UsageTracking;
using Microsoft.AspNetCore.Http;
using Moq;

namespace ConduitLLM.Tests.Http.Accounting;

public class RequestAccountingContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedStream_PromotesKnownToolPriceWithoutRepricing(bool estimated)
    {
        var http = new DefaultHttpContext();
        var accounting = http.GetOrCreateRequestAccountingContext();
        accounting.RecordProviderInvocation(1, streaming: true);
        accounting.RecordProviderToolCallUsage(1, new ProviderToolUsage
            { Tools = [new ProviderToolUsageItem { ToolName = "browser_search", Count = 1 }] });
        var pricing = new Mock<IToolCostCalculationService>();
        pricing.Setup(service => service.CalculateToolCostsAsync(It.IsAny<ToolUsageData>(), It.IsAny<ConduitLLM.Configuration.ProviderType>()))
            .ReturnsAsync(new ToolCostResult { TotalCost = 0.03m });
        var partial = await ProviderUsagePricing.GetOrCalculateToolsAsync(http, pricing.Object);
        Assert.Null(partial.Cost);
        Assert.Equal(0.03m, partial.KnownCost);

        if (estimated)
            accounting.RecordProviderUsage(new Usage { TotalTokens = 10 }, "test", UsageEvidenceSource.Estimated);
        else accounting.RecordProviderStreamCompleted(1);

        var complete = await ProviderUsagePricing.GetOrCalculateToolsAsync(http, pricing.Object);
        Assert.Equal(0.03m, complete.Cost);
        Assert.Null(complete.FailureReason);
        Assert.Null(partial.Cost);
        pricing.Verify(service => service.CalculateToolCostsAsync(It.IsAny<ToolUsageData>(),
            It.IsAny<ConduitLLM.Configuration.ProviderType>()), Times.Once());
    }

    [Fact]
    public void SnapshotCarriesTypedUsageTransportAndIndeterminateState()
    {
        var context = new RequestAccountingContext();
        var usage = new Usage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 };
        context.SetOperation(RequestOperation.ChatCompletion, 42, "requested-model");
        context.RecordProviderUsage(usage, "resolved-model", UsageEvidenceSource.Provider);
        context.RecordTransport(new StreamTransportEvidence(
            StreamTransportOutcome.Completed,
            2,
            3,
            100,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            false));
        context.MarkIndeterminate("settlement pending");

        var snapshot = context.Snapshot();

        Assert.Equal(RequestOperation.ChatCompletion, snapshot.Operation);
        Assert.Equal(42, snapshot.VirtualKeyId);
        Assert.Same(usage, snapshot.ProviderUsage!.Usage);
        Assert.Equal("resolved-model", snapshot.ProviderUsage.Model);
        Assert.Equal(StreamTransportOutcome.Completed, snapshot.Transport!.Outcome);
        Assert.True(snapshot.IsIndeterminate);
        Assert.Equal("settlement pending", snapshot.IndeterminateReason);
    }

    [Fact]
    public void BoundedAccumulatorNeverRetainsMoreThanConfiguredLimit()
    {
        var accumulator = new BoundedStringAccumulator(5);

        accumulator.Append("abc");
        accumulator.Append("defgh");

        Assert.Equal(5, accumulator.Length);
        Assert.Equal(8, accumulator.TotalCharactersObserved);
        Assert.True(accumulator.LimitExceeded);
        Assert.Equal("abcde", accumulator.ToString());
    }
}
