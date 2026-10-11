using System.Runtime.CompilerServices;
using System.Text.Json;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public class ChatStreamingToolEvidenceTests
{
    [Fact]
    public async Task CreateChatCompletion_ReusedToolIndex_RetainsSeparateCallsAndArgumentFragments()
    {
        var snapshot = await RunAsync([
            [ToolChunk("first", "look", "{\"id\":"), ToolChunk(null, "up", "1}", FinishReason.ToolCalls)],
            [ToolChunk("second", "lookup", "{}", FinishReason.ToolCalls)],
            [FinalChunk()]
        ]);

        Assert.Collection(snapshot.StreamingToolCalls,
            first =>
            {
                Assert.Equal("first", first.Id);
                Assert.Equal("lookup", first.Function.Name);
                Assert.Equal("{\"id\":1}", first.Function.Arguments);
            },
            second =>
            {
                Assert.Equal("second", second.Id);
                Assert.Equal("lookup", second.Function.Name);
                Assert.Equal("{}", second.Function.Arguments);
            });
        Assert.Empty(snapshot.FunctionExecutions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateChatCompletion_ForcedFinalProviderCall_HasSeparateToolScope(bool hasFunctionRoute)
    {
        var snapshot = await RunAsync([
            [ToolChunk("first", "lookup", "{}", FinishReason.ToolCalls)],
            [ToolChunk("final", "lookup", "{}", FinishReason.Stop), FinalChunk()]
        ], maximumIterations: hasFunctionRoute ? 1 : 2, hasFunctionRoute: hasFunctionRoute);

        Assert.Equal(new[] { "first", "final" }, snapshot.StreamingToolCalls.Select(call => call.Id));
    }

    [Fact]
    public async Task CreateChatCompletion_MultipleChoices_RetainsSameToolIndexSeparately()
    {
        var chunk = ToolChunk("choice-zero", "lookup", "{}");
        chunk.Choices.Add(ToolChunk("choice-one", "lookup", "{}").Choices[0]);
        chunk.Choices[1].Index = 1;

        var snapshot = await RunAsync([[chunk, FinalChunk()]], configuredFunctions: false);

        Assert.Equal(new[] { "choice-zero", "choice-one" }, snapshot.StreamingToolCalls.Select(call => call.Id));
    }

    [Fact]
    public async Task CreateChatCompletion_ToolLimitAcrossIterations_MarksEvidenceIndeterminate()
    {
        var snapshot = await RunAsync([
            [ToolChunk("first", "lookup", "{}", FinishReason.ToolCalls)],
            [ToolChunk("second", "lookup", "{}", FinishReason.ToolCalls)],
            [new ChatCompletionChunk { Choices = [] }]
        ], maximumToolCalls: 1);

        Assert.Single(snapshot.StreamingToolCalls);
        Assert.True(snapshot.Transport!.EvidenceTruncated);
        Assert.True(snapshot.IsIndeterminate);
    }

    [Fact]
    public void ProviderIteration_Serialization_RemainsServerOnly()
    {
        var json = JsonSerializer.Serialize(new ChatCompletionChunk { ProviderIteration = 3 }, GatewayJsonOptions.Create());

        Assert.DoesNotContain("ProviderIteration", json);
        Assert.DoesNotContain("provider_iteration", json);
    }

    private static async Task<RequestAccountingSnapshot> RunAsync(
        ChatCompletionChunk[][] providerCalls,
        bool configuredFunctions = true,
        int maximumIterations = 5,
        bool hasFunctionRoute = true,
        int maximumToolCalls = 128)
    {
        var client = new Mock<ILLMClient>();
        var sequence = client.SetupSequence(service => service.StreamChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));
        foreach (var call in providerCalls)
        {
            sequence.Returns(Stream(call));
        }
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientAsync("test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        var discovery = new Mock<IFunctionDiscoveryService>();
        discovery.Setup(service => service.GetToolsForFunctionConfigurationsAsync(
                It.IsAny<List<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        discovery.Setup(service => service.GetFunctionNameToIdMappingAsync(
                It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasFunctionRoute
                ? new Dictionary<string, FunctionRoute> { ["lookup"] = new(1) }
                : []);
        var orchestration = new Mock<IAgenticOrchestrationService>();
        orchestration.Setup(service => service.ExecuteToolCallsAsync(
                It.IsAny<List<ToolCall>>(), It.IsAny<int>(), It.IsAny<Dictionary<string, FunctionRoute>>(),
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticExecutionResult { AllSucceeded = true, ToolResultMessages = [] });
        var repository = new Mock<IFunctionConfigurationRepository>();
        repository.Setup(service => service.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionConfiguration { Id = 1, ConfigurationName = "lookup", IsEnabled = true }]);
        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(service => service.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
        settings.Setup(service => service.GetMaxAgenticIterationsAsync()).ReturnsAsync(5);
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        context.Response.Body = new MemoryStream();
        var endpoints = new ChatEndpoints(
            new Conduit(factory.Object, NullLogger<Conduit>.Instance,
                functionDiscoveryService: discovery.Object, agenticOrchestrationService: orchestration.Object),
            NullLogger<ChatEndpoints>.Instance,
            Mock.Of<IModelProviderMappingService>(), GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(),
            settings.Object, Mock.Of<IUsageEstimationService>(), repository.Object,
            Options.Create(new UsageTrackingOptions { MaximumStreamingToolCalls = maximumToolCalls }),
            httpContextAccessor: new HttpContextAccessor { HttpContext = context });

        await endpoints.CreateChatCompletion(new ChatCompletionRequest
        {
            Model = "test-model", Stream = true, EnableAgenticMode = true,
            MaxAgenticIterations = maximumIterations,
            FunctionConfigurationIds = configuredFunctions ? [1] : null,
            Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
        });

        client.Verify(service => service.StreamChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(providerCalls.Length));
        return context.GetOrCreateRequestAccountingContext().Snapshot();
    }

    private static ChatCompletionChunk ToolChunk(string id, string name, string arguments, string finishReason = null) => new()
    {
        Model = "test-model",
        Choices = [new StreamingChoice
        {
            Index = 0, FinishReason = finishReason,
            Delta = new DeltaContent
            {
                ToolCalls = [new ToolCallChunk
                {
                    Index = 0, Id = id,
                    Function = new FunctionCallChunk { Name = name, Arguments = arguments }
                }]
            }
        }]
    };

    private static ChatCompletionChunk FinalChunk() => new()
    {
        Model = "test-model", Choices = [],
        Usage = new Usage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 }
    };

    private static async IAsyncEnumerable<ChatCompletionChunk> Stream(
        ChatCompletionChunk[] chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
        await Task.CompletedTask;
    }
}
