using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Gateway.Utilities;
using ConduitLLM.Tests.Http.Middleware.Builders;
using Moq;

namespace ConduitLLM.Tests.Http.Middleware;

public partial class UsageTrackingMiddlewareTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task ProcessResponseAsync_AgenticResults_PersistsCorrelationAndCalculatedCosts(
        bool streaming, int providerCallCount)
    {
        var context = new HttpContextBuilder()
            .ForChatCompletions().WithVirtualKey(123).AsGroq().WithTraceId("parent-trace").Build();
        var completedId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        Fixture.SetupDefaultCost(0.10m);
        Fixture.ToolCostService.Setup(service => service.CalculateToolCostsAsync(
                It.IsAny<ToolUsageData>(), It.IsAny<ConduitLLM.Configuration.ProviderType>()))
            .ReturnsAsync(new ToolCostResult { TotalCost = 0.06m });

        await Invoker.WithNextDelegate(ctx =>
        {
            ctx.Response.ContentType = streaming ? "text/event-stream" : "application/json";
            var accounting = ctx.GetOrCreateRequestAccountingContext();
            accounting.SetOperation(RequestOperation.ChatCompletion, 123, "test-model");
            accounting.RecordProviderUsage(
                new Usage { PromptTokens = providerCallCount * 100, CompletionTokens = 50 },
                "test-model", UsageEvidenceSource.Provider);
            // Three provider calls include a forced final call; costs must use all calls.
            accounting.RecordProviderCalls(Enumerable.Range(1, providerCallCount)
                .Select(iteration => new ProviderCallUsage
                {
                    Iteration = iteration,
                    Usage = new Usage { PromptTokens = 100 }
                }));
            accounting.RecordFunctionExecutions(
            [
                new ToolExecutionEvent
                {
                    Iteration = 1, ToolCallId = "call-success", FunctionName = "lookup",
                    Status = "completed", Cost = 0.03m, FunctionExecutionId = completedId
                },
                new ToolExecutionEvent
                {
                    Iteration = 2, ToolCallId = "call-failure", FunctionName = "lookup",
                    Status = "failed", Cost = 0.02m, FunctionExecutionId = failedId,
                    ErrorMessage = "provider failed"
                }
            ], 0.05m);
            accounting.RecordProviderToolUsage(new ProviderToolUsage
            {
                Tools = [new ProviderToolUsageItem { ToolName = "browser_search", Count = 2 }]
            });
            return Task.CompletedTask;
        }).InvokeAsync(context);

        var log = Assert.Single(Fixture.CapturedRequestLogs);
        Assert.NotNull(log.Metadata);
        var metadata = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(
            System.Text.Json.JsonSerializer.Serialize(log.Metadata))!;
        Assert.Equal("chat_with_functions", metadata.Type);
        Assert.Equal(context.GetRequestAccountingSnapshot()!.BillingRequestId, metadata.BillingRequestId);
        Assert.Equal("parent-trace", metadata.RequestId);
        Assert.Equal(0.05m, metadata.TotalCost);
        Assert.Equal(1, metadata.SuccessCount);
        Assert.Equal(1, metadata.FailedCount);
        Assert.Equal(new[] { 1, 2 }, metadata.FunctionCalls!.Select(call => call.Iteration!.Value));
        Assert.Equal(new Guid?[] { completedId, failedId }, metadata.FunctionCalls.Select(call => call.FunctionExecutionId));
        Assert.Equal("provider failed", metadata.FunctionCalls[1].ErrorMessage);
        Assert.Equal("browser_search", Assert.Single(metadata.ProviderToolUsage!.Tools).ToolName);
        Assert.Equal(providerCallCount * 0.10m, metadata.CostBreakdown!.LlmCost);
        Assert.Equal(0.05m, metadata.CostBreakdown.FunctionCost);
        Assert.Equal(0.06m, metadata.CostBreakdown.ProviderToolCost);
        Assert.Equal(log.Cost, metadata.CostBreakdown.TotalCost);
        Assert.Equal(providerCallCount * 0.10m + 0.11m, log.Cost);
        Fixture.BatchSpendService.Verify(service => service.QueueSpendUpdateAsync(
            123, log.Cost, It.IsAny<DateTime?>()), Times.Once);
    }

    [Fact]
    public async Task TrackStreamingUsageAsync_MissingUsage_PreservesFunctionMetadataAndUnknownComponents()
    {
        var context = new HttpContextBuilder()
            .ForChatCompletions().WithVirtualKey(123).WithTraceId("disconnected-trace").Build();
        var accounting = context.GetOrCreateRequestAccountingContext();
        accounting.SetOperation(RequestOperation.ChatCompletion, 123, "test-model");
        accounting.RecordFunctionExecutions(
        [
            new ToolExecutionEvent
            {
                Iteration = 2, ToolCallId = "failed-call", FunctionName = "lookup",
                Status = "failed", Cost = 0.02m, FunctionExecutionId = Guid.NewGuid()
            }
        ], 0.02m);

        await Invoker.AsStreamingResponse().InvokeAsync(context);

        var log = Assert.Single(Fixture.CapturedRequestLogs);
        var metadata = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(
            System.Text.Json.JsonSerializer.Serialize(log.Metadata))!;
        Assert.Equal(accounting.BillingRequestId, metadata.BillingRequestId);
        Assert.Equal("disconnected-trace", metadata.RequestId);
        Assert.Equal(2, Assert.Single(metadata.FunctionCalls!).Iteration);
        Assert.Null(metadata.CostBreakdown!.LlmCost);
        Assert.Null(metadata.CostBreakdown.ProviderToolCost);
        Assert.Equal(0.02m, metadata.CostBreakdown.FunctionCost);
        Assert.Equal(0.02m, metadata.CostBreakdown.TotalCost);
        Assert.Equal(0.02m, log.Cost);
    }

    [Fact]
    public async Task ProcessResponseAsync_PlainChat_PreservesNullMetadata()
    {
        var context = new HttpContextBuilder().ForChatCompletions().WithVirtualKey(123).Build();
        Fixture.SetupDefaultCost(0.10m);

        await Invoker.WithProviderUsage("test-model", new Usage { PromptTokens = 100 })
            .WithResponseJson("{\"model\":\"test-model\"}").InvokeAsync(context);

        Assert.Null(Assert.Single(Fixture.CapturedRequestLogs).Metadata);
    }
}
