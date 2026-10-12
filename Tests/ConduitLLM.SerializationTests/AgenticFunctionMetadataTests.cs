using System.Text.Json;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Utilities;

namespace ConduitLLM.SerializationTests;

public class AgenticFunctionMetadataTests
{
    [Fact]
    public void Partial_cost_metadata_preserves_known_amounts_without_false_complete_totals()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(
            [new ToolExecutionEvent { Status = "completed", Cost = 0.05m, FunctionName = "lookup" }],
            costBreakdown: new FunctionExecutionCostBreakdown(null, 0.05m, null, 0.13m, 0.05m, 0.03m));
        var breakdown = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(json)!.CostBreakdown!;

        Assert.Null(breakdown.LlmCost);
        Assert.Null(breakdown.ProviderToolCost);
        Assert.Equal(0.05m, breakdown.KnownLlmCost);
        Assert.Equal(0.03m, breakdown.KnownProviderToolCost);
        Assert.Equal(0.13m, breakdown.TotalCost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Agentic_response_serializes_known_costs_and_omits_unresolved_totals(bool priced)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var response = new ChatCompletionResponse
        {
            Id = "chat", Created = 0, Object = "chat.completion", Model = "model", Choices = [],
            AgenticMetrics = new AgenticExecutionMetrics
            {
                TotalIterations = 2, TotalFunctionCalls = 1, TotalFunctionCost = 0.05m,
                TotalLLMCost = priced ? 0.02m : null, TotalCost = priced ? 0.07m : null,
                ProviderCalls = [new ProviderCallUsage { Iteration = 1, Usage = new Usage { ProviderReportedCostUsd = 0.01m } }]
            }
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, GatewayJsonOptions.Create()));
        var metrics = json.RootElement.GetProperty("agentic_metrics");
        Assert.Equal(0.05m, metrics.GetProperty("total_function_cost").GetDecimal());
        Assert.False(metrics.TryGetProperty("provider_calls", out _));
        Assert.Equal(priced, metrics.TryGetProperty("total_llm_cost", out var llmCost));
        Assert.Equal(priced, metrics.TryGetProperty("total_cost", out var totalCost));
        if (priced)
        {
            Assert.Equal(0.02m, llmCost.GetDecimal());
            Assert.Equal(0.07m, totalCost.GetDecimal());
        }
    }

    [Fact]
    public void Function_metadata_round_trips_all_billing_components_without_reflection()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var executionId = Guid.NewGuid();
        var executionEvent = new ToolExecutionEvent
        {
            Iteration = 4,
            ToolCallId = "call",
            FunctionName = "lookup",
            Status = "failed",
            FunctionExecutionId = executionId,
            Cost = 0.02m,
            ErrorMessage = "provider failed"
        };
        var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(
            [executionEvent], "billing-parent", "http-parent",
            new FunctionExecutionCostBreakdown(0.10m, 0.02m, 0.03m, 0.15m),
            new ToolUsageData { Tools = [new ToolUsageItem { ToolName = "browser", Count = 1 }] });
        var restored = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(json)!;

        Assert.Equal("billing-parent", restored.BillingRequestId);
        Assert.Equal("http-parent", restored.RequestId);
        Assert.Equal(0.02m, restored.TotalCost);
        Assert.Equal(0.15m, restored.CostBreakdown!.TotalCost);
        Assert.Equal(0.10m, restored.CostBreakdown.LlmCost);
        Assert.Equal(0.03m, restored.CostBreakdown.ProviderToolCost);
        Assert.Equal("browser", Assert.Single(restored.ProviderToolUsage!.Tools).ToolName);
        Assert.Equal(executionId, Assert.Single(restored.FunctionCalls!).FunctionExecutionId);
        Assert.Equal(4, restored.FunctionCalls![0].Iteration);
        using var metadata = JsonDocument.Parse(json);
        Assert.Equal("browser", metadata.RootElement.GetProperty("providerToolUsage")
            .GetProperty("tools")[0].GetProperty("tool_name").GetString());

        // SSE events use the Gateway's generated resolver, independently of the log converter.
        using var sse = JsonDocument.Parse(JsonSerializer.Serialize(executionEvent, GatewayJsonOptions.Create()));
        Assert.Equal(4, sse.RootElement.GetProperty("iteration").GetInt32());
    }
}
