using System.Text.Json;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Options;

namespace ConduitLLM.SerializationTests;

public class ChatRuntimeStateTests
{
    [Fact]
    public void RuntimeRoutingAndCachingState_DoesNotChangeRequestWirePayload()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var request = new ChatCompletionRequest { Model = "model", Messages = [] };
        var options = GatewayJsonOptions.Create();
        var wire = JsonSerializer.Serialize(request, options);
        request.PromptCachingIntent = new PromptCachingIntent { Strategy = PromptCachingStrategy.Automatic };
        request.RoutingAffinityKey = "private-affinity";
        request.SelectedMappingId = 23;
        request.RoutingAffinityUsed = true;
        request.RoutingDecisionReason = "private-route";
        request.RoutingFailoverCount = 2;
        request.ProviderInvocationStarting = (_, _) => Task.CompletedTask;
        request.ProviderInvocationCompleted = (_, _, _) => { };
        request.FunctionExecutionCompleted = _ => { };
        request.ProviderStreamUsageObserved = (_, _, _) => { };
        request.ProviderStreamCompleted = _ => { };

        Assert.Equal(wire, JsonSerializer.Serialize(request, options));
    }
}
