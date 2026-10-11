using System.Runtime.CompilerServices;

using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Core;

public class ConduitAgenticUsageTests
{
    [Fact]
    public async Task CreateChatCompletionAsync_AgenticIterations_AggregatesAllProviderUsage()
    {
        var client = new Mock<ILLMClient>();
        client.SetupSequence(x => x.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateResponse(FinishReason.ToolCalls, new Usage
            {
                PromptTokens = 100,
                CompletionTokens = 10,
                TotalTokens = 110,
                CachedInputTokens = 40,
                ReasoningTokens = 3,
                ProviderReportedCostUsd = 0.01m
            }, includeToolCall: true))
            .ReturnsAsync(CreateResponse(FinishReason.Stop, new Usage
            {
                PromptTokens = 150,
                CompletionTokens = 20,
                TotalTokens = 170,
                CachedInputTokens = 60,
                ReasoningTokens = 7,
                ProviderReportedCostUsd = 0.02m
            }));

        var conduit = CreateConduit(client.Object);

        var response = await conduit.CreateChatCompletionAsync(CreateRequest(), virtualKeyId: 42);

        Assert.NotNull(response.Usage);
        Assert.Equal(250, response.Usage.PromptTokens);
        Assert.Equal(30, response.Usage.CompletionTokens);
        Assert.Equal(280, response.Usage.TotalTokens);
        Assert.Equal(100, response.Usage.CachedInputTokens);
        Assert.Equal(10, response.Usage.ReasoningTokens);
        Assert.Equal(0.03m, response.Usage.ProviderReportedCostUsd);
        Assert.Equal(2, response.AgenticMetrics?.TotalIterations);
        Assert.Equal(2, response.AgenticMetrics?.ProviderCalls.Count);
        Assert.Equal(100, response.AgenticMetrics?.ProviderCalls[0].Usage.PromptTokens);
        Assert.Equal(150, response.AgenticMetrics?.ProviderCalls[1].Usage.PromptTokens);
    }

    [Fact]
    public async Task StreamChatCompletionAsync_AgenticIterations_EmitsCumulativeUsageWithoutDoubleCountingOneCall()
    {
        var client = new Mock<ILLMClient>();
        client.SetupSequence(x => x.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Stream(
                CreateChunk(FinishReason.ToolCalls, new Usage
                {
                    PromptTokens = 90,
                    CompletionTokens = 9,
                    TotalTokens = 99
                }, includeToolCall: true),
                // Some providers repeat or revise usage during a single stream. Only the latest
                // value from this provider call should contribute to the request total.
                CreateChunk(null, new Usage
                {
                    PromptTokens = 100,
                    CompletionTokens = 10,
                    TotalTokens = 110,
                    ProviderReportedCostUsd = 0.01m
                })))
            .Returns(Stream(CreateChunk(FinishReason.Stop, new Usage
            {
                PromptTokens = 150,
                CompletionTokens = 20,
                TotalTokens = 170,
                ProviderReportedCostUsd = 0.02m
            })));

        var conduit = CreateConduit(client.Object);
        var usageChunks = new List<Usage>();
        var providerCalls = new Dictionary<int, Usage>();

        await foreach (var chunk in conduit.StreamChatCompletionAsync(CreateRequest(), virtualKeyId: 42))
        {
            if (chunk.Usage != null)
            {
                usageChunks.Add(chunk.Usage);
            }
            if (chunk.ProviderCallUsage != null)
            {
                providerCalls[chunk.ProviderCallUsage.Iteration] = chunk.ProviderCallUsage.Usage;
            }
        }

        Assert.Equal(3, usageChunks.Count);
        Assert.Equal(99, usageChunks[0].TotalTokens);
        Assert.Equal(110, usageChunks[1].TotalTokens);
        Assert.Equal(280, usageChunks[2].TotalTokens);
        Assert.Equal(250, usageChunks[2].PromptTokens);
        Assert.Equal(30, usageChunks[2].CompletionTokens);
        Assert.Equal(0.03m, usageChunks[2].ProviderReportedCostUsd);
        Assert.Equal(2, providerCalls.Count);
        Assert.Equal(100, providerCalls[1].PromptTokens);
        Assert.Equal(150, providerCalls[2].PromptTokens);
    }

    [Fact]
    public async Task CreateChatCompletionAsync_IterationLimit_IncludesForcedFinalCallUsage()
    {
        var client = new Mock<ILLMClient>();
        client.SetupSequence(x => x.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateResponse(
                FinishReason.ToolCalls,
                new Usage { PromptTokens = 100, CompletionTokens = 10, TotalTokens = 110 },
                includeToolCall: true))
            .ReturnsAsync(CreateResponse(
                FinishReason.Stop,
                new Usage { PromptTokens = 150, CompletionTokens = 20, TotalTokens = 170 }));

        var request = CreateRequest();
        request.MaxAgenticIterations = 1;

        var response = await CreateConduit(client.Object)
            .CreateChatCompletionAsync(request, virtualKeyId: 42);

        Assert.Equal(280, response.Usage?.TotalTokens);
        client.Verify(x => x.CreateChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task StreamChatCompletionAsync_IterationLimit_IncludesForcedFinalCallUsage()
    {
        var client = new Mock<ILLMClient>();
        client.SetupSequence(x => x.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Stream(CreateChunk(
                FinishReason.ToolCalls,
                new Usage { PromptTokens = 100, CompletionTokens = 10, TotalTokens = 110 },
                includeToolCall: true)))
            .Returns(Stream(CreateChunk(
                FinishReason.Stop,
                new Usage { PromptTokens = 150, CompletionTokens = 20, TotalTokens = 170 })));

        var request = CreateRequest();
        request.MaxAgenticIterations = 1;
        Usage? lastUsage = null;

        await foreach (var chunk in CreateConduit(client.Object)
            .StreamChatCompletionAsync(request, virtualKeyId: 42))
        {
            lastUsage = chunk.Usage ?? lastUsage;
        }

        Assert.Equal(280, lastUsage?.TotalTokens);
        client.Verify(x => x.StreamChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public async Task AgenticFlow_MultipleIterationsAndForcedFinal_PreservesFunctionIterations(
        bool streaming, int maximumIterations)
    {
        var client = new Mock<ILLMClient>();
        client.SetupSequence(x => x.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateResponse(FinishReason.ToolCalls, new Usage { TotalTokens = 10 }, true))
            .ReturnsAsync(CreateResponse(FinishReason.ToolCalls, new Usage { TotalTokens = 20 }, true))
            .ReturnsAsync(CreateResponse(FinishReason.Stop, new Usage { TotalTokens = 30 }));
        client.SetupSequence(x => x.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Stream(CreateChunk(FinishReason.ToolCalls, new Usage { TotalTokens = 10 }, true)))
            .Returns(Stream(CreateChunk(FinishReason.ToolCalls, new Usage { TotalTokens = 20 }, true)))
            .Returns(Stream(CreateChunk(FinishReason.Stop, new Usage { TotalTokens = 30 })));
        var executionService = new Mock<IFunctionExecutionService>();
        var executionCount = 0;
        executionService.Setup(x => x.ExecuteAsync(
                1, 42, It.IsAny<Dictionary<string, object>>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FunctionExecution
            {
                FunctionConfigurationId = 1,
                State = ++executionCount == 1 ? ExecutionState.Completed : ExecutionState.Failed,
                ActualCost = 0.01m,
                ResponseJson = "{}",
                ErrorMessage = "provider failure"
            });
        var orchestration = new AgenticOrchestrationService(
            executionService.Object, Mock.Of<ILogger<AgenticOrchestrationService>>());
        var conduit = CreateConduit(client.Object, orchestration);
        var request = CreateRequest();
        request.MaxAgenticIterations = maximumIterations;
        var expectedIterations = maximumIterations == 1 ? new[] { 1 } : new[] { 1, 2 };

        if (streaming)
        {
            var events = new List<ToolExecutionEvent>();
            await foreach (var chunk in conduit.StreamChatCompletionAsync(
                request, virtualKeyId: 42, onToolExecutingEvent: (executionEvent, _) =>
                {
                    events.Add(executionEvent);
                    return Task.CompletedTask;
                }))
            {
            }

            Assert.Equal(expectedIterations, events.Where(item => item.Status != "started")
                .Select(item => item.Iteration!.Value));
            Assert.Equal(expectedIterations, events.Where(item => item.Status == "started")
                .Select(item => item.Iteration!.Value));
            Assert.Equal("completed", events[1].Status);
            if (maximumIterations > 1)
                Assert.Equal("failed", events[3].Status);
        }
        else
        {
            var response = await conduit.CreateChatCompletionAsync(request, virtualKeyId: 42);
            Assert.Equal(expectedIterations, response.AgenticMetrics!.FunctionCalls.Select(item => item.Iteration));
            Assert.Equal(expectedIterations.Length * 0.01m, response.AgenticMetrics.TotalFunctionCost);
            // Verify the public response contract uses generated metadata and preserves the values.
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, CoreHttpJsonContext.Default.ChatCompletionResponse));
            Assert.Equal(expectedIterations, json.RootElement.GetProperty("agentic_metrics")
                .GetProperty("function_calls").EnumerateArray().Select(item => item.GetProperty("iteration").GetInt32()));
        }
    }

    private static Conduit CreateConduit(ILLMClient client, IAgenticOrchestrationService? realOrchestration = null)
    {
        var clientFactory = new Mock<ILLMClientFactory>();
        clientFactory.Setup(x => x.GetClientAsync("test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var functionDiscovery = new Mock<IFunctionDiscoveryService>();
        functionDiscovery.Setup(x => x.GetToolsForFunctionConfigurationsAsync(
                It.IsAny<List<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Tool>());
        functionDiscovery.Setup(x => x.GetFunctionNameToIdMappingAsync(
                It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, FunctionRoute> { ["lookup"] = new FunctionRoute(1) });

        var orchestration = new Mock<IAgenticOrchestrationService>();
        orchestration.Setup(x => x.ExecuteToolCallsAsync(
                It.IsAny<List<ToolCall>>(),
                It.IsAny<int>(),
                It.IsAny<Dictionary<string, FunctionRoute>>(),
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticExecutionResult
            {
                AllSucceeded = true,
                ToolResultMessages = new List<Message>
                {
                    new() { Role = MessageRole.Tool, Content = "result", ToolCallId = "call-1" }
                }
            });

        return new Conduit(
            clientFactory.Object,
            Mock.Of<ILogger<Conduit>>(),
            functionDiscoveryService: functionDiscovery.Object,
            agenticOrchestrationService: realOrchestration ?? orchestration.Object);
    }

    private static ChatCompletionRequest CreateRequest() => new()
    {
        Model = "test-model",
        Messages = new List<Message> { new() { Role = MessageRole.User, Content = "hello" } },
        FunctionConfigurationIds = new List<int> { 1 },
        EnableAgenticMode = true,
        MaxAgenticIterations = 5
    };

    private static ChatCompletionResponse CreateResponse(
        string finishReason,
        Usage usage,
        bool includeToolCall = false) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Object = "chat.completion",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = "test-model",
            Usage = usage,
            Choices = new List<Choice>
            {
                new()
                {
                    Index = 0,
                    FinishReason = finishReason,
                    Message = new Message
                    {
                        Role = MessageRole.Assistant,
                        Content = includeToolCall ? null : "done",
                        ToolCalls = includeToolCall ? new List<ToolCall> { CreateToolCall() } : null
                    }
                }
            }
        };

    private static ChatCompletionChunk CreateChunk(
        string? finishReason,
        Usage usage,
        bool includeToolCall = false) => new()
        {
            Model = "test-model",
            Usage = usage,
            Choices = new List<StreamingChoice>
            {
                new()
                {
                    Index = 0,
                    FinishReason = finishReason,
                    Delta = new DeltaContent
                    {
                        ToolCalls = includeToolCall
                            ? new List<ToolCallChunk>
                            {
                                new()
                                {
                                    Index = 0,
                                    Id = "call-1",
                                    Type = "function",
                                    Function = new FunctionCallChunk { Name = "lookup", Arguments = "{}" }
                                }
                            }
                            : null
                    }
                }
            }
        };

    private static ToolCall CreateToolCall() => new()
    {
        Id = "call-1",
        Type = "function",
        Function = new FunctionCall { Name = "lookup", Arguments = "{}" }
    };

    private static async IAsyncEnumerable<ChatCompletionChunk> Stream(
        ChatCompletionChunk first,
        ChatCompletionChunk? second = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return first;

        if (second != null)
        {
            yield return second;
        }

        await Task.CompletedTask;
    }
}
