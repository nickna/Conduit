using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConduitLLM.Tests.Core;

public class AgenticOrchestrationServiceTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    public async Task ExecuteToolCallsAsync_MixedResults_PreservesOriginatingIteration(
        bool sequential, int iteration)
    {
        var metadata = new List<Dictionary<string, object>>();
        var executionService = new Mock<IFunctionExecutionService>();
        executionService.Setup(service => service.ExecuteAsync(
                It.IsAny<int>(), 42, It.IsAny<Dictionary<string, object>>(),
                It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int configurationId, int virtualKeyId, Dictionary<string, object> parameters,
                string idempotencyKey, Dictionary<string, object> executionMetadata,
                string providerToolName, CancellationToken cancellationToken) =>
            {
                metadata.Add(executionMetadata);
                return new FunctionExecution
                {
                    FunctionConfigurationId = configurationId,
                    State = configurationId == 1 ? ExecutionState.Completed : ExecutionState.Failed,
                    ResponseJson = "{}",
                    ErrorMessage = configurationId == 1 ? null : "provider failed",
                    ActualCost = configurationId == 1 ? 0.03m : 0.02m
                };
            });
        var service = new AgenticOrchestrationService(
            executionService.Object, Mock.Of<ILogger<AgenticOrchestrationService>>());
        var toolCalls = new List<ToolCall>
        {
            Call("call-success", "success", "{}"),
            Call("call-failure", "failure", sequential ? "{\"previous\":\"call-success\"}" : "{}")
        };

        Assert.Equal(sequential, service.HasDependencies(toolCalls));
        var result = await service.ExecuteToolCallsAsync(
            toolCalls, 42,
            new Dictionary<string, FunctionRoute>
            {
                ["success"] = new(1), ["failure"] = new(2)
            },
            "request", Guid.NewGuid(), iteration);

        Assert.Equal(2, result.FunctionCallSummaries.Count);
        Assert.All(result.FunctionCallSummaries, summary => Assert.Equal(iteration, summary.Iteration));
        Assert.All(metadata, item => Assert.Equal(iteration, item["iteration_number"]));
        Assert.True(result.FunctionCallSummaries[0].Success);
        Assert.False(result.FunctionCallSummaries[1].Success);
        Assert.Equal(0.05m, result.TotalFunctionCost);
        Assert.False(result.AllSucceeded);
    }

    [Theory]
    [InlineData("missing", "{}")]
    [InlineData("lookup", "invalid json")]
    [InlineData("lookup", "{}")]
    public async Task ExecuteToolCallsAsync_FailureWithoutExecution_PreservesOriginatingIteration(
        string functionName, string arguments)
    {
        var executionService = new Mock<IFunctionExecutionService>();
        executionService.Setup(service => service.ExecuteAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, object>>(),
                It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("execution unavailable"));
        var service = new AgenticOrchestrationService(
            executionService.Object, Mock.Of<ILogger<AgenticOrchestrationService>>());

        var result = await service.ExecuteToolCallsAsync(
            [Call("call", functionName, arguments)], 42,
            new Dictionary<string, FunctionRoute> { ["lookup"] = new(1) },
            "request", Guid.NewGuid(), 3);

        var summary = Assert.Single(result.FunctionCallSummaries);
        Assert.Equal(3, summary.Iteration);
        Assert.False(summary.Success);
        Assert.Null(summary.FunctionExecutionId);
        Assert.NotEmpty(result.Errors);
    }

    private static ToolCall Call(string id, string name, string arguments) => new()
    {
        Id = id,
        Type = "function",
        Function = new FunctionCall { Name = name, Arguments = arguments }
    };
}
