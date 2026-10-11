using System.Net;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Providers.MiniMax;
using ConduitLLM.Providers.OpenRouter;
using ConduitLLM.Providers.Replicate;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Core.Services;

public sealed class MediaProviderAcceptanceTests
{
    [Theory]
    [InlineData("replicate", false)] [InlineData("replicate", true)]
    [InlineData("minimax", true)] [InlineData("openrouter", true)]
    public async Task AcceptedOperationIsAwaitedBeforePolling_RecordingFailureNeverResubmits(string name, bool video)
    {
        var provider = new Provider { Id = 17, ProviderName = name, IsEnabled = true,
            ProviderType = name == "replicate" ? ProviderType.Replicate : name == "minimax" ? ProviderType.MiniMax : ProviderType.OpenRouter };
        var key = new ProviderKeyCredential { Id = 7, ProviderId = 17, ApiKey = "test-key", IsEnabled = true };
        var http = new AcceptanceHttp(name);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(http, disposeHandler: false));
        ILLMClient client = name switch
        {
            "replicate" => new ReplicateClient(provider, key, "owner/model", NullLogger.Instance, factory.Object),
            "minimax" => new MiniMaxClient(provider, key, "MiniMax-Hailuo-02", NullLogger<MiniMaxClient>.Instance, factory.Object),
            _ => new OpenRouterClient(provider, key, "google/veo-3", NullLogger.Instance, factory.Object)
        };
        MediaProviderOperation? recorded = null;
        using var scope = new MediaProviderOperationScope((operation, _) =>
        {
            recorded = operation;
            throw new IOException("Injected persistence failure after provider acceptance");
        });
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => video ? ((IVideoGenerationClient)client).CreateVideoAsync(new()
            { Model = "test-model", Prompt = "capture this operation" }) : client.CreateImageAsync(new()
            { Model = "test-model", Prompt = "capture this operation" }));
        Assert.True(recorded != null, failure.ToString()); Assert.Equal("original-operation", recorded.OperationId);
        Assert.Equal(17, recorded.ProviderId); Assert.Equal(7, recorded.CredentialId);
        Assert.Equal(1, http.Posts); Assert.Equal(0, http.Gets);
    }

    private sealed class AcceptanceHttp(string provider) : HttpMessageHandler
    {
        public int Posts, Gets;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post) Posts++; else Gets++;
            var json = provider == "minimax" ? """{"task_id":"original-operation","base_resp":{"status_code":0,"status_msg":"ok"}}""" :
                """{"id":"original-operation","status":"starting"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
