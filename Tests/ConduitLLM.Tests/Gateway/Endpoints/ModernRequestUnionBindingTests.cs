using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Audio;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Options;

using Microsoft.Extensions.DependencyInjection;

using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

[Trait("Category", "Integration")]
[Trait("Component", "Gateway")]
public sealed class ModernRequestUnionBindingTests
{
    private const string ModerationObject = """{"model":"omni-moderation-latest","policy":{"input":{"mode":"block"},"output":{"mode":"score"}}}""";

    public static IEnumerable<object[]> ModerationCases()
    {
        foreach (var path in new[] { "/v1/chat/completions", "/v1/conduit/chat/completions" })
        foreach (var moderation in new[] { "", "null", ModerationObject })
            yield return [path, moderation];
    }

    [Theory]
    [MemberData(nameof(ModerationCases))]
    public async Task ChatBindingPassesTypedModerationObjectNullAndOmissionToTheProvider(string path, string moderation)
    {
        await using var fixture = await BindingFixture.StartAsync();
        using var response = await PostAsync(fixture.Host, path, ChatBody(moderation));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var received = Assert.Single(fixture.ChatRequests);
        if (moderation == ModerationObject)
        {
            Assert.NotNull(received.Moderation);
            Assert.Equal("omni-moderation-latest", received.Moderation.Model);
            Assert.Equal(ChatModerationMode.Block, received.Moderation.Policy.Input.Mode);
            Assert.Equal(ChatModerationMode.Score, received.Moderation.Policy.Output.Mode);
        }
        else Assert.Null(received.Moderation);
        fixture.Provider.Verify(provider => provider.CreateChatCompletionAsync(
            It.IsAny<ChatCompletionRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("\"score\"")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"model\":\"omni\",\"policy\":{\"input\":{\"mode\":\"allow\"}}}")]
    [InlineData("{\"model\":\"omni\",\"policy\":{\"input\":{\"mode\":7}}}")]
    [InlineData("{\"model\":\"omni\",\"policy\":{\"input\":{}}}")]
    public async Task MalformedModerationReturnsAnOpenAI400BeforeProviderInvocation(string moderation)
    {
        await using var fixture = await BindingFixture.StartAsync();
        using var response = await PostAsync(fixture.Host, "/v1/chat/completions", ChatBody(moderation));

        await AssertBadRequestAsync(response);
        fixture.AssertNoProviderInvocation();
    }

    [Theory]
    [InlineData("\"alloy\"", "alloy", false)]
    [InlineData("{\"id\":\"voice_custom_123\"}", "voice_custom_123", true)]
    public async Task SpeechBindingPreservesStockOrCustomVoiceAndReachesTheCapableProvider(
        string voice, string expectedId, bool expectedCustom)
    {
        await using var fixture = await BindingFixture.StartAsync();
        using var response = await PostAsync(fixture.Host, "/v1/audio/speech", SpeechBody(voice));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0x49, 0x44, 0x33, 0x04 }, bytes);
        var received = Assert.Single(fixture.SpeechRequests);
        Assert.Equal(expectedId, received.Voice.Id);
        Assert.Equal(expectedCustom, received.Voice.IsCustom);
        Assert.Equal("provider-speech", received.Model);
        Assert.Equal("Say hello", received.Input);
        fixture.Speech.Verify(provider => provider.CreateSpeechAsync(
            It.IsAny<TextToSpeechRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("7")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"id\":7}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"id\":\"\"}")]
    public async Task MalformedOrMissingVoiceReturnsAnOpenAI400BeforeProviderInvocation(string voice)
    {
        await using var fixture = await BindingFixture.StartAsync();
        using var response = await PostAsync(fixture.Host, "/v1/audio/speech", SpeechBody(voice));

        await AssertBadRequestAsync(response);
        fixture.AssertNoProviderInvocation();
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/audio/speech")]
    public async Task ValidModernRequestsStillRequireVirtualKeyAuthentication(string path)
    {
        await using var fixture = await BindingFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Test-Anonymous", "true");
        request.Content = new StringContent(path == "/v1/audio/speech" ? SpeechBody("{\"id\":\"voice_custom_123\"}") : ChatBody(ModerationObject),
            Encoding.UTF8, "application/json");

        using var response = await fixture.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        fixture.AssertNoProviderInvocation();
    }

    private static string ChatBody(string moderation) =>
        "{\"model\":\"chat-union\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]" +
        (moderation.Length == 0 ? "" : ",\"moderation\":" + moderation) + "}";

    private static string SpeechBody(string voice) =>
        "{\"model\":\"speech-union\",\"input\":\"Say hello\"" +
        (voice is null ? "" : ",\"voice\":" + voice) + "}";

    private static async Task<HttpResponseMessage> PostAsync(GatewayEndpointTestHost host, string path, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await host.Client.PostAsync(path, content);
    }

    private static async Task AssertBadRequestAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    private sealed class BindingFixture : IAsyncDisposable
    {
        public GatewayEndpointTestHost Host { get; private set; }
        public Mock<ILLMClient> Provider { get; } = new();
        public Mock<ITextToSpeechClient> Speech { get; private set; }
        public Mock<ILLMClientFactory> Factory { get; } = new();
        public List<ChatCompletionRequest> ChatRequests { get; } = [];
        public List<TextToSpeechRequest> SpeechRequests { get; } = [];

        public static async Task<BindingFixture> StartAsync()
        {
            var fixture = new BindingFixture();
            fixture.Speech = fixture.Provider.As<ITextToSpeechClient>();
            fixture.Provider.Setup(provider => provider.CreateChatCompletionAsync(
                    It.IsAny<ChatCompletionRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<ChatCompletionRequest, string, CancellationToken>((request, _, _) => fixture.ChatRequests.Add(request))
                .ReturnsAsync(new ChatCompletionResponse
                {
                    Id = "binding-fixture", Object = "chat.completion", Created = 1, Model = "chat-union",
                    Choices = [new Choice { Index = 0, FinishReason = FinishReason.Stop,
                        Message = new Message { Role = MessageRole.Assistant, Content = "ok" } }],
                    Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 }
                });
            fixture.Speech.Setup(provider => provider.CreateSpeechAsync(
                    It.IsAny<TextToSpeechRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<TextToSpeechRequest, string, CancellationToken>((request, _, _) => fixture.SpeechRequests.Add(request))
                .ReturnsAsync(new TextToSpeechResponse { AudioData = [0x49, 0x44, 0x33, 0x04], ContentType = "audio/mpeg" });
            fixture.Factory.Setup(factory => factory.GetClientForChatAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(fixture.Provider.Object);
            fixture.Factory.Setup(factory => factory.GetClientAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(fixture.Provider.Object);
            var mappings = new Mock<IModelProviderMappingService>();
            mappings.Setup(service => service.GetMappingByModelAliasAsync(It.IsAny<string>()))
                .ReturnsAsync((string alias) => new ModelProviderMapping
                {
                    Id = 1, ModelAlias = alias, ProviderModelId = alias == "speech-union" ? "provider-speech" : "provider-chat",
                    ProviderId = 1, Provider = new Provider { Id = 1, ProviderName = "Binding fixture", ProviderType = ProviderType.OpenAI },
                    ModelProviderTypeAssociation = new ModelProviderTypeAssociation
                    {
                        Model = new Model { Id = 1, Name = alias, SupportsChat = true, SupportsTextToSpeech = true }
                    }
                });
            var settings = new Mock<IGlobalSettingsCacheService>();
            settings.Setup(service => service.GetDefaultAgenticModeEnabledAsync()).ReturnsAsync(false);
            settings.Setup(service => service.GetMaxAgenticIterationsAsync()).ReturnsAsync(1);
            settings.Setup(service => service.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
            var keys = new Mock<IVirtualKeyRuntimeService>();
            keys.Setup(service => service.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKey { Id = 1 }));
            fixture.Host = await GatewayEndpointTestHost.StartAsync(services =>
            {
                services.ConfigureHttpJsonOptions(options =>
                {
                    options.SerializerOptions.TypeInfoResolverChain.Clear();
                    GatewayJsonOptions.Configure(options.SerializerOptions);
                });
                services.AddSingleton(GatewayJsonOptions.Create());
                services.AddSingleton(fixture.Factory.Object);
                services.AddSingleton(mappings.Object);
                services.AddSingleton(settings.Object);
                services.AddSingleton(keys.Object);
                services.AddSingleton(Mock.Of<IUsageEstimationService>());
                services.AddSingleton(Mock.Of<IEventBus>());
                services.AddSingleton(new Mock<ConduitLLM.Persistence.Interfaces.IFunctionExecutionConfigurationStore>(MockBehavior.Strict).Object);
                services.AddScoped<Conduit>();
            });
            return fixture;
        }

        public void AssertNoProviderInvocation()
        {
            Assert.Empty(ChatRequests);
            Assert.Empty(SpeechRequests);
            Factory.Verify(factory => factory.GetClientForChatAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            Factory.Verify(factory => factory.GetClientAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
