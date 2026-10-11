using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.UsageTracking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public class ChatFunctionAccountingTests
{
    [Fact]
    public async Task CreateChatCompletion_AgenticMetrics_PreservesFunctionIterationsAndExecutionIds()
    {
        var context = new DefaultHttpContext();
        context.Items["VirtualKeyId"] = 42;
        var executionId = Guid.NewGuid();
        var client = new Mock<ILLMClient>();
        client.Setup(service => service.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse
            {
                Id = "chat",
                Created = 1,
                Object = "chat.completion",
                Model = "test-model",
                Usage = new Usage { PromptTokens = 100 },
                Choices = [new Choice
                {
                    Index = 0, FinishReason = FinishReason.Stop,
                    Message = new Message { Role = MessageRole.Assistant, Content = "done" }
                }],
                AgenticMetrics = new AgenticExecutionMetrics
                {
                    TotalFunctionCost = 0.02m,
                    FunctionCalls =
                    [
                        new FunctionCallSummary
                        {
                            Iteration = 3, ToolCallId = "call", FunctionName = "lookup",
                            Success = false, FunctionExecutionId = executionId,
                            Cost = 0.02m, ErrorMessage = "provider failed"
                        }
                    ]
                }
            });
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientAsync("test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        var endpoint = new ChatEndpoints(
            new Conduit(factory.Object, Mock.Of<ILogger<Conduit>>()),
            Mock.Of<ILogger<ChatEndpoints>>(), Mock.Of<IModelProviderMappingService>(),
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(),
            Mock.Of<IGlobalSettingsCacheService>(), Mock.Of<IUsageEstimationService>(),
            httpContextAccessor: new HttpContextAccessor { HttpContext = context });

        await endpoint.CreateChatCompletion(new ChatCompletionRequest
        {
            Model = "test-model", Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
        });

        var snapshot = context.GetRequestAccountingSnapshot()!;
        var execution = Assert.Single(snapshot.FunctionExecutions);
        Assert.Equal(3, execution.Iteration);
        Assert.Equal(executionId, execution.FunctionExecutionId);
        Assert.Equal("failed", execution.Status);
        Assert.Equal("provider failed", execution.ErrorMessage);
        Assert.Equal(0.02m, snapshot.FunctionExecutionCost);
        Assert.Equal(snapshot.BillingRequestId, context.Response.Headers["x-request-id"].ToString());
    }
}
