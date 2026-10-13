using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Functions.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Core;

public sealed class ConduitAgenticDefaultsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectClrCaller_UnspecifiedOptionsExecuteFiveIterationsThenFinalCall(bool streaming)
    {
        var providerCalls = 0;
        var client = new Mock<ILLMClient>();
        client.Setup(service => service.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(++providerCalls <= 5));
        client.Setup(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => StreamAsync(++providerCalls <= 5));
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientForChatAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        var discovery = new Mock<IFunctionDiscoveryService>();
        discovery.Setup(service => service.GetToolsForFunctionConfigurationsAsync(
                It.IsAny<List<int>>(), 1, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        discovery.Setup(service => service.GetFunctionNameToIdMappingAsync(
                It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, FunctionRoute> { ["lookup"] = new(7) });
        var iterations = new List<int>();
        var orchestration = new Mock<IAgenticOrchestrationService>();
        orchestration.Setup(service => service.ExecuteToolCallsAsync(
                It.IsAny<List<ToolCall>>(), 1, It.IsAny<Dictionary<string, FunctionRoute>>(),
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<List<ToolCall>, int, Dictionary<string, FunctionRoute>, string, Guid, int, CancellationToken>(
                (_, _, _, _, _, iteration, _) => iterations.Add(iteration))
            .ReturnsAsync(new AgenticExecutionResult { AllSucceeded = true, ToolResultMessages = [] });
        var conduit = new Conduit(factory.Object, NullLogger<Conduit>.Instance,
            functionDiscoveryService: discovery.Object, agenticOrchestrationService: orchestration.Object);
        var request = new ChatCompletionRequest
        {
            Model = "test", FunctionConfigurationIds = [7],
            Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
        };
        Assert.Null(request.EnableAgenticMode);
        Assert.Null(request.MaxAgenticIterations);

        if (streaming)
        {
            await foreach (var _ in conduit.StreamChatCompletionAsync(request, virtualKeyId: 1)) { }
        }
        else
        {
            var response = await conduit.CreateChatCompletionAsync(request, virtualKeyId: 1);
            Assert.Equal(5, response.AgenticMetrics.TotalIterations);
        }

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, iterations);
        Assert.Equal(6, providerCalls);
        Assert.Contains(request.Messages, message => message.Content?.ToString()?.Contains("Maximum iteration limit (5)") == true);
    }

    private static ChatCompletionResponse Response(bool toolCall) => new()
    {
        Id = "defaults", Created = 0, Model = "test", Object = "chat.completion", Usage = new Usage(),
        Choices = [new Choice { Index = 0, FinishReason = toolCall ? FinishReason.ToolCalls : FinishReason.Stop,
            Message = new Message { Role = MessageRole.Assistant,
                ToolCalls = toolCall ? [new ToolCall { Id = "call", Type = "function",
                    Function = new FunctionCall { Name = "lookup", Arguments = "{}" } }] : null,
                Content = toolCall ? null : "done" } }]
    };

    private static async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(bool toolCall)
    {
        await Task.Yield();
        yield return new ChatCompletionChunk
        {
            Id = "defaults", Created = 0, Model = "test", Object = "chat.completion.chunk",
            Choices = [new StreamingChoice { Index = 0,
                FinishReason = toolCall ? FinishReason.ToolCalls : FinishReason.Stop,
                Delta = new DeltaContent { Role = MessageRole.Assistant,
                    Content = toolCall ? null : "done",
                    ToolCalls = toolCall ? [new ToolCallChunk { Index = 0, Id = "call",
                        Function = new FunctionCallChunk { Name = "lookup", Arguments = "{}" } }] : null } }],
            Usage = new Usage()
        };
    }
}
