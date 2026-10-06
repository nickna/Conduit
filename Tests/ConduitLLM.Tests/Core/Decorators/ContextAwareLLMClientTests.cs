using ConduitLLM.Core.Decorators;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Core.Decorators;

/// <summary>
/// Regression tests for issue #976: optional capabilities (video generation, auth
/// verification) must survive being wrapped in a decorator chain instead of being
/// silently dropped by an intermediate decorator.
/// </summary>
public class ContextAwareLLMClientTests
{
    private readonly Mock<IServiceProvider> _serviceProvider = new();

    /// <summary>
    /// Fake provider client that supports video generation (like MiniMaxClient) and
    /// authentication verification.
    /// </summary>
    private sealed class FakeVideoCapableProviderClient : ILLMClient, IAuthenticationVerifiable, IVideoGenerationClient
    {
        private readonly VideoGenerationResponse _videoResponse;

        public VideoGenerationRequest? ReceivedVideoRequest { get; private set; }

        public FakeVideoCapableProviderClient(VideoGenerationResponse videoResponse)
        {
            _videoResponse = videoResponse;
        }

        // Exposed through the compile-time optional capability contract.
        public Task<VideoGenerationResponse> CreateVideoAsync(
            VideoGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedVideoRequest = request;
            return Task.FromResult(_videoResponse);
        }

        public Task<ChatCompletionResponse> CreateChatCompletionAsync(
            ChatCompletionRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionAsync(
            ChatCompletionRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<List<string>> ListModelsAsync(
            string? apiKey = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string>());

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ImageGenerationResponse> CreateImageAsync(
            ImageGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AuthenticationResult> VerifyAuthenticationAsync(
            string? apiKey = null,
            string? baseUrl = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AuthenticationResult.Success("provider auth ok"));

        public string GetHealthCheckUrl(string? baseUrl = null)
            => "https://provider.example.com/health";
    }

    private static VideoGenerationRequest CreateVideoRequest()
        => new()
        {
            Prompt = "A cat surfing a wave",
            Model = "video-01"
        };

    private static VideoGenerationResponse CreateVideoResponse()
        => new()
        {
            Data = new List<VideoData>
            {
                new() { Url = "https://example.com/video.mp4" }
            }
        };

    private PromptCachingLLMClient WrapInPromptCaching(ILLMClient inner)
    {
        var settingsService = new Mock<ConduitLLM.Configuration.Interfaces.IGlobalSettingsCacheService>();
        var logger = new Mock<ILogger<PromptCachingLLMClient>>();
        return new PromptCachingLLMClient(inner, settingsService.Object, logger.Object);
    }

    private ContextAwareLLMClient WrapInContextAware(ILLMClient inner)
        => new(inner, keyId: 0, providerId: 0, _serviceProvider.Object);

    private static PerformanceTrackingLLMClient WrapInPerformanceTracking(ILLMClient inner)
    {
        var metricsService = new Mock<IPerformanceMetricsService>();
        var logger = new Mock<ILogger<PerformanceTrackingLLMClient>>();
        return new PerformanceTrackingLLMClient(
            inner, metricsService.Object, logger.Object, "testprovider", true);
    }

    [Fact]
    public async Task CreateVideoAsync_InnerClientIsPromptCachingDecorator_ReturnsProviderResponse()
    {
        // Arrange — chain mirrors DatabaseAwareLLMClientFactory: Context(Caching(provider))
        var expectedResponse = CreateVideoResponse();
        var providerClient = new FakeVideoCapableProviderClient(expectedResponse);
        var sut = WrapInContextAware(WrapInPromptCaching(providerClient));

        // Act
        var result = await sut.CreateVideoAsync(CreateVideoRequest());

        // Assert
        result.Should().BeSameAs(expectedResponse);
    }

    [Fact]
    public async Task CreateVideoAsync_InnerClientIsPromptCachingDecorator_ForwardsRequestToProvider()
    {
        // Arrange
        var providerClient = new FakeVideoCapableProviderClient(CreateVideoResponse());
        var sut = WrapInContextAware(WrapInPromptCaching(providerClient));
        var request = CreateVideoRequest();

        // Act
        await sut.CreateVideoAsync(request);

        // Assert
        providerClient.ReceivedVideoRequest.Should().BeSameAs(request);
    }

    [Fact]
    public async Task CreateVideoAsync_ProviderDirectlyWrapped_ReturnsProviderResponse()
    {
        // Arrange — no intermediate decorator (chain without prompt caching)
        var expectedResponse = CreateVideoResponse();
        var providerClient = new FakeVideoCapableProviderClient(expectedResponse);
        var sut = WrapInContextAware(providerClient);

        // Act
        var result = await sut.CreateVideoAsync(CreateVideoRequest());

        // Assert
        result.Should().BeSameAs(expectedResponse);
    }

    [Fact]
    public async Task CreateVideoAsync_PromptCachingDecoratorDirectly_ForwardsToProvider()
    {
        var expectedResponse = CreateVideoResponse();
        var providerClient = new FakeVideoCapableProviderClient(expectedResponse);
        var sut = WrapInPromptCaching(providerClient);

        var result = await sut.CreateVideoAsync(CreateVideoRequest());

        result.Should().BeSameAs(expectedResponse);
    }

    [Fact]
    public async Task CreateVideoAsync_PerformanceTrackingDecoratorDirectly_ForwardsToProvider()
    {
        var expectedResponse = CreateVideoResponse();
        var providerClient = new FakeVideoCapableProviderClient(expectedResponse);
        var sut = WrapInPerformanceTracking(WrapInPromptCaching(providerClient));

        var result = await sut.CreateVideoAsync(CreateVideoRequest());

        result.Should().BeSameAs(expectedResponse);
    }

    [Fact]
    public async Task CreateVideoAsync_InnermostClientLacksVideoSupport_ThrowsNotSupportedException()
    {
        // Arrange — innermost client has no CreateVideoAsync method
        var providerClient = new Mock<ILLMClient>();
        var sut = WrapInContextAware(WrapInPromptCaching(providerClient.Object));

        // Act
        var act = () => sut.CreateVideoAsync(CreateVideoRequest());

        // Assert
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*does not support video generation*");
    }

    [Fact]
    public void UnwrapInnermost_FullDecoratorChain_ReturnsProviderClient()
    {
        // Arrange — full factory chain: Perf(Context(Caching(provider)))
        var providerClient = new FakeVideoCapableProviderClient(CreateVideoResponse());
        ILLMClient chain = WrapInPerformanceTracking(
            WrapInContextAware(WrapInPromptCaching(providerClient)));

        // Act
        var innermost = chain.UnwrapInnermost();

        // Assert
        innermost.Should().BeSameAs(providerClient);
    }

    [Fact]
    public async Task VerifyAuthenticationAsync_FullDecoratorChain_DelegatesToProviderClient()
    {
        // Arrange — Perf(Context(Caching(provider))); previously Context/Caching dropped
        // IAuthenticationVerifiable so the chain reported "not supported"
        var providerClient = new FakeVideoCapableProviderClient(CreateVideoResponse());
        var chain = WrapInPerformanceTracking(
            WrapInContextAware(WrapInPromptCaching(providerClient)));

        // Act
        var result = await chain.VerifyAuthenticationAsync();

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void GetHealthCheckUrl_FullDecoratorChain_DelegatesToProviderClient()
    {
        // Arrange
        var providerClient = new FakeVideoCapableProviderClient(CreateVideoResponse());
        var chain = WrapInPerformanceTracking(
            WrapInContextAware(WrapInPromptCaching(providerClient)));

        // Act
        var url = chain.GetHealthCheckUrl();

        // Assert
        url.Should().Be("https://provider.example.com/health");
    }

    [Fact]
    public async Task CreateChatCompletionAsync_ProviderThrows_StampsProviderNameOnWholeChain()
    {
        // Arrange — a status-bearing exception wrapped in a status-less one, as
        // provider clients routinely produce
        var inner = new ConduitLLM.Core.Exceptions.LLMCommunicationException(
            "API returned an error", System.Net.HttpStatusCode.TooManyRequests, "slow down");
        var outer = new ConduitLLM.Core.Exceptions.LLMCommunicationException("wrapped", inner);
        var providerClient = new Mock<ILLMClient>();
        providerClient
            .Setup(c => c.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outer);
        var sut = new ContextAwareLLMClient(
            providerClient.Object, keyId: 0, providerId: 0, _serviceProvider.Object,
            providerName: "openai-prod");

        // Act
        var act = () => sut.CreateChatCompletionAsync(new ChatCompletionRequest
        {
            Model = "gpt-test",
            Messages = new List<Message>()
        });

        // Assert
        await act.Should().ThrowAsync<ConduitLLM.Core.Exceptions.LLMCommunicationException>();
        outer.ProviderName.Should().Be("openai-prod");
        inner.ProviderName.Should().Be("openai-prod");
    }

    [Fact]
    public async Task CreateChatCompletionAsync_ProviderNameAlreadySet_IsNotOverwritten()
    {
        var ex = new ConduitLLM.Core.Exceptions.LLMCommunicationException(
            "boom", System.Net.HttpStatusCode.BadGateway, "body")
        { ProviderName = "set-by-source" };
        var providerClient = new Mock<ILLMClient>();
        providerClient
            .Setup(c => c.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);
        var sut = new ContextAwareLLMClient(
            providerClient.Object, keyId: 0, providerId: 0, _serviceProvider.Object,
            providerName: "openai-prod");

        var act = () => sut.CreateChatCompletionAsync(new ChatCompletionRequest
        {
            Model = "gpt-test",
            Messages = new List<Message>()
        });

        await act.Should().ThrowAsync<ConduitLLM.Core.Exceptions.LLMCommunicationException>();
        ex.ProviderName.Should().Be("set-by-source");
    }

    [Fact]
    public async Task CreateEmbeddingAsync_WrappedCommunicationError_IsTracked()
    {
        var communicationException = new LLMCommunicationException(
            "rate limited",
            System.Net.HttpStatusCode.TooManyRequests,
            "slow down");
        var wrappedException = new AggregateException("provider call failed", communicationException);
        var providerClient = new Mock<ILLMClient>();
        providerClient
            .Setup(c => c.CreateEmbeddingAsync(
                It.IsAny<EmbeddingRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(wrappedException);

        var errorTracker = new Mock<IProviderErrorTrackingService>();
        _serviceProvider
            .Setup(serviceProvider => serviceProvider.GetService(typeof(IProviderErrorTrackingService)))
            .Returns(errorTracker.Object);
        var sut = new ContextAwareLLMClient(
            providerClient.Object,
            keyId: 42,
            providerId: 7,
            _serviceProvider.Object);

        var act = () => sut.CreateEmbeddingAsync(new EmbeddingRequest
        {
            Input = "hello",
            Model = "embedding-test"
        });

        await act.Should().ThrowAsync<AggregateException>();
        errorTracker.Verify(
            tracker => tracker.TrackErrorAsync(It.Is<ProviderErrorInfo>(error =>
                error.KeyCredentialId == 42 &&
                error.ProviderId == 7 &&
                error.ErrorType == ProviderErrorType.RateLimitExceeded)),
            Times.Once);
    }
}
