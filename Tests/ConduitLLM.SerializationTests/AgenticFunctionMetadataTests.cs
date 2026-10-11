using System.Text.Json;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Utilities;

namespace ConduitLLM.SerializationTests;

public class AgenticFunctionMetadataTests
{
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
