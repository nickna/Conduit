using System.Net;
using System.Text;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Providers;

public sealed class ProviderRuntimeSelectionTests
{
    /// <summary>
    /// Exercises fresh provider/secret rotation and primary selection beyond the management page size.
    /// </summary>
    [Fact]
    public async Task ChatRuntimeSelection_UsesFreshCompleteSnapshotOnEveryInvocation()
    {
        var service = new Mock<IProviderService>(MockBehavior.Strict);
        var reader = new Mock<IProviderRuntimeSelectionReader>(MockBehavior.Strict);
        var first = Snapshot("first.invalid", Enumerable.Range(1, 105).Select(id => new ProviderKeyCredential
        {
            Id = id, ProviderId = 7, ApiKey = "key-" + id, IsEnabled = id != 1, IsPrimary = id == 105
        }));
        var second = Snapshot("second.invalid", [new ProviderKeyCredential
        {
            Id = 200, ProviderId = 7, ApiKey = "rotated", IsEnabled = true, IsPrimary = true
        }]);
        reader.SetupSequence(value => value.GetByIdAsync(7, default))
            .ReturnsAsync(first).ReturnsAsync(second);
        using var handler = new SelectionHandler();
        var factory = Factory(service.Object, reader.Object, handler);

        foreach (var request in new[] { Request(), Request() })
        {
            var client = await factory.GetClientForChatAsync(request);
            await client.CreateChatCompletionAsync(request);
        }

        Assert.Equal(new[] { "key-105", "rotated" }, handler.Credentials);
        Assert.Equal(new[] { "first.invalid", "second.invalid" }, handler.Hosts);
        reader.Verify(value => value.GetByIdAsync(7, default), Times.Exactly(2));
        service.VerifyNoOtherCalls();
        Assert.Equal(105, first.ProviderKeyCredentials.Count);
    }

    /// <summary>
    /// Preserves route rejection for a missing or freshly disabled provider.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatRuntimeSelection_RejectsMissingOrDisabledFreshProvider(bool missing)
    {
        var service = new Mock<IProviderService>(MockBehavior.Strict);
        var reader = new Mock<IProviderRuntimeSelectionReader>(MockBehavior.Strict);
        var provider = Snapshot("disabled.invalid", []);
        provider.IsEnabled = false;
        reader.Setup(value => value.GetByIdAsync(7, default)).ReturnsAsync(missing ? null : provider);

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => Factory(service.Object, reader.Object).GetClientForChatAsync(Request()));

        service.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Preserves the existing no-enabled-credential error without trying a stale fallback read.
    /// </summary>
    [Fact]
    public async Task ChatRuntimeSelection_RejectsDisabledCredentials()
    {
        var service = new Mock<IProviderService>(MockBehavior.Strict);
        var reader = new Mock<IProviderRuntimeSelectionReader>(MockBehavior.Strict);
        reader.Setup(value => value.GetByIdAsync(7, default)).ReturnsAsync(Snapshot("disabled.invalid",
            [new ProviderKeyCredential { Id = 1, ProviderId = 7, IsEnabled = false, ApiKey = "disabled" }]));

        await Assert.ThrowsAsync<ConfigurationException>(
            () => Factory(service.Object, reader.Object).GetClientForChatAsync(Request()));

        service.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Forwards request cancellation and persistence failures without retrying through the paged service.
    /// </summary>
    [Fact]
    public async Task ChatRuntimeSelection_ForwardsCancellationAndDoesNotMaskReadErrors()
    {
        var service = new Mock<IProviderService>(MockBehavior.Strict);
        var reader = new Mock<IProviderRuntimeSelectionReader>(MockBehavior.Strict);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        reader.Setup(value => value.GetByIdAsync(7, cancelled.Token))
            .Returns(Task.FromCanceled<Provider?>(cancelled.Token));
        var factory = Factory(service.Object, reader.Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.GetClientForChatAsync(Request(), cancelled.Token));
        var error = new TimeoutException("Fresh provider selection failed.");
        reader.Setup(value => value.GetByIdAsync(7, default)).ThrowsAsync(error);
        Assert.Same(error, await Assert.ThrowsAsync<TimeoutException>(
            () => factory.GetClientForChatAsync(Request())));
        service.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Keeps the service's provider and credential reads when no runtime capability is registered.
    /// </summary>
    [Fact]
    public async Task ChatWithoutRuntimeCapability_RetainsServiceReadPath()
    {
        var service = new Mock<IProviderService>(MockBehavior.Strict);
        var provider = Snapshot("fallback.invalid", []);
        service.Setup(value => value.GetProviderByIdAsync(7)).ReturnsAsync(provider);
        service.Setup(value => value.GetKeyCredentialsByProviderIdAsync(7))
            .ReturnsAsync([new ProviderKeyCredential { Id = 2, ProviderId = 7, ApiKey = "fallback", IsEnabled = true }]);

        await Factory(service.Object, null).GetClientForChatAsync(Request());

        service.VerifyAll();
    }

    /// <summary>Creates an eligible cached mapping whose graph must not supply runtime credentials.</summary>
    private static DatabaseAwareLLMClientFactory Factory(
        IProviderService service, IProviderRuntimeSelectionReader? reader, HttpMessageHandler? handler = null)
    {
        var mappings = new Mock<IModelProviderMappingService>();
        mappings.Setup(value => value.GetMappingsByModelAliasAsync("model"))
            .ReturnsAsync([new ModelProviderMapping
            {
                Id = 928371, ModelAlias = "model", ProviderId = 7, ProviderModelId = "provider-model", IsEnabled = true,
                Provider = Snapshot("stale.invalid", [new ProviderKeyCredential { ApiKey = "stale", IsEnabled = true }]),
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation { IsEnabled = true }
            }]);
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(value => value.CreateClient(It.IsAny<string>()))
            .Returns(() => handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false));
        return new DatabaseAwareLLMClientFactory(service, mappings.Object,
            NullLoggerFactory.Instance, httpFactory.Object,
            NullLogger<DatabaseAwareLLMClientFactory>.Instance, Mock.Of<IServiceProvider>(),
            runtimeProviderSelectionReader: reader);
    }

    /// <summary>Creates an owned current provider graph for one simulated persistence read.</summary>
    private static Provider Snapshot(string host, IEnumerable<ProviderKeyCredential> credentials)
    {
        var provider = new Provider
        {
            Id = 7, ProviderName = "provider", ProviderType = ProviderType.OpenAI,
            BaseUrl = "https://" + host + "/v1", IsEnabled = true,
            ProviderKeyCredentials = credentials.ToList()
        };
        foreach (var credential in provider.ProviderKeyCredentials) credential.Provider = provider;
        return provider;
    }

    /// <summary>Creates a chat request accepted by the original OpenAI client contract.</summary>
    private static ChatCompletionRequest Request() => new()
    {
        Model = "model", Messages = [new Message { Role = "user", Content = "hello" }]
    };

    private sealed class SelectionHandler : HttpMessageHandler
    {
        public List<string?> Credentials { get; } = [];
        public List<string?> Hosts { get; } = [];

        /// <summary>Records the selected current credential without making a network request.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Credentials.Add(request.Headers.Authorization?.Parameter);
            Hosts.Add(request.RequestUri?.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"reply","object":"chat.completion","created":1,"model":"provider-model",
                    "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
                    "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                    """, Encoding.UTF8, "application/json")
            });
        }
    }
}
