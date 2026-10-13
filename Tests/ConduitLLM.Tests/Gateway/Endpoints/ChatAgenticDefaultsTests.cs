using System.Net;
using System.Text;
using System.Text.Json;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Gateway.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public class ChatAgenticDefaultsTests
{
    public static IEnumerable<object[]> BindingCases()
    {
        foreach (var path in new[] { "/v1/chat/completions", "/v1/conduit/chat/completions" })
        foreach (var streaming in new[] { false, true })
        {
            yield return [path, streaming, "", false, 3];
            yield return [path, streaming, ",\"enable_agentic_mode\":null,\"max_agentic_iterations\":null", false, 3];
            yield return [path, streaming, ",\"enable_agentic_mode\":true,\"max_agentic_iterations\":2", true, 2];
            yield return [path, streaming, ",\"enable_agentic_mode\":false,\"max_agentic_iterations\":1", false, 1];
            yield return [path, streaming, ",\"max_agentic_iterations\":0", null, 0];
            yield return [path, streaming, ",\"max_agentic_iterations\":4", null, 4];
        }
    }

    [Theory]
    [MemberData(nameof(BindingCases))]
    public async Task HttpBinding_ResolvesOmissionAndNull_PreservesExplicitValues(
        string path, bool streaming, string options, bool? expectedEnabled, int expectedIterations)
    {
        ChatCompletionRequest received = null;
        var client = new Mock<ILLMClient>();
        client.Setup(s => s.CreateChatCompletionAsync(It.IsAny<ChatCompletionRequest>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, string, CancellationToken>((request, _, _) => received = request)
            .ReturnsAsync(new ChatCompletionResponse
            {
                Id = "defaults", Model = "test", Created = 0, Object = "chat.completion", Usage = new Usage(),
                Choices = [new Choice { Index = 0, FinishReason = FinishReason.Stop,
                    Message = new Message { Role = MessageRole.Assistant, Content = "done" } }]
            });
        client.Setup(s => s.StreamChatCompletionAsync(It.IsAny<ChatCompletionRequest>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, string, CancellationToken>((request, _, _) => received = request)
            .Returns(StreamAsync());
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(s => s.GetClientAsync("test", It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(s => s.GetDefaultAgenticModeEnabledAsync()).ReturnsAsync(false);
        settings.Setup(s => s.GetMaxAgenticIterationsAsync()).ReturnsAsync(3);
        settings.Setup(s => s.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
        var functions = new Mock<IFunctionConfigurationRepository>();
        functions.Setup(s => s.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionConfiguration { Id = 7, ConfigurationName = "search", IsEnabled = true }]);
        var keys = new Mock<IVirtualKeyRuntimeService>();
        keys.Setup(s => s.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKey { Id = 1 }));
        var discovery = new Mock<IFunctionDiscoveryService>();
        discovery.Setup(s => s.GetToolsForFunctionConfigurationsAsync(It.IsAny<List<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Tool>());
        discovery.Setup(s => s.GetFunctionNameToIdMappingAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, FunctionRoute>());
        await using var host = await GatewayEndpointTestHost.StartAsync(services =>
        {
            services.ConfigureHttpJsonOptions(o => GatewayJsonOptions.Configure(o.SerializerOptions));
            services.AddSingleton(GatewayJsonOptions.Create());
            services.AddSingleton(factory.Object);
            services.AddSingleton(settings.Object);
            services.AddSingleton(functions.Object);
            services.AddSingleton(keys.Object);
            services.AddSingleton(discovery.Object);
            services.AddSingleton(Mock.Of<IAgenticOrchestrationService>());
            services.AddSingleton(Mock.Of<IModelProviderMappingService>());
            services.AddSingleton(Mock.Of<IUsageEstimationService>());
            services.AddSingleton(Mock.Of<IEventBus>());
            services.AddScoped<Conduit>();
        });
        using var response = await host.Client.PostAsync(path, new StringContent(
            "{\"model\":\"test\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"function_configuration_ids\":[7],\"stream\":" +
            (streaming ? "true" : "false") + options + "}",
            Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        if (expectedEnabled is null)
        {
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
            using var document = JsonDocument.Parse(body);
            Assert.Equal("invalid_max_agentic_iterations", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Null(received);
            settings.Verify(s => s.GetDefaultAgenticModeEnabledAsync(), Times.Never);
            settings.Verify(s => s.GetMinAgenticIterationsAsync(), Times.Once);
            settings.Verify(s => s.GetMaxAgenticIterationsAsync(), Times.Once);
            return;
        }
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        if (streaming)
        {
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("data: [DONE]", body);
        }
        Assert.NotNull(received);
        Assert.Equal(expectedEnabled, received.EnableAgenticMode);
        Assert.Equal(expectedIterations, received.MaxAgenticIterations);
        settings.Verify(s => s.GetDefaultAgenticModeEnabledAsync(), options.Contains("true") || options.Contains("false") ? Times.Never : Times.Once);
    }

    [Fact]
    public void GeneratedSerialization_OmitsUnspecifiedAgenticOptions()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ChatCompletionRequest
        {
            Model = "test", Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
        }, ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.ChatCompletionRequest);
        Assert.DoesNotContain("enable_agentic_mode", json);
        Assert.DoesNotContain("max_agentic_iterations", json);
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> StreamAsync()
    {
        await Task.Yield();
        yield return new ChatCompletionChunk
        {
            Id = "defaults", Model = "test", Created = 0, Object = "chat.completion.chunk",
            Choices = [new StreamingChoice { Index = 0, FinishReason = FinishReason.Stop,
                Delta = new DeltaContent { Role = MessageRole.Assistant, Content = "done" } }],
            Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 }
        };
    }
}
