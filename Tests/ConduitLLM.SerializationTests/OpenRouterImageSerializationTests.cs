using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.OpenRouter;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class OpenRouterImageSerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageSubmission_SerializesInputReferencesAndPreservesUsage(bool edit)
    {
        using var handler = new ImageHandler();
        var client = new OpenRouterClient(
            new Provider { Id = 1, ProviderName = "fixture", ProviderType = ProviderType.OpenRouter, BaseUrl = "https://provider.invalid/api/v1" },
            new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "fixture-key" },
            "fixture-image", NullLogger<OpenRouterClient>.Instance, new ClientFactory(handler));
        using var extension = JsonDocument.Parse("""{"enabled":false,"strength":0.5}""");
        var response = await client.CreateImageAsync(new ImageGenerationRequest
        {
            Model = "fixture-image", Prompt = "fixture prompt", Image = edit ? "AQID" : null,
            N = 2, Quality = "hd", Size = "1024x1024",
            ExtensionData = new() { ["options"] = extension.RootElement.Clone() }
        });

        Assert.Equal("AQID", Assert.Single(response.Data).B64Json);
        Assert.Equal(123, response.Created);
        Assert.Equal(0.123m, response.Usage!.ProviderReportedCostUsd);
        Assert.Equal(1, response.Usage.ImageCount);
        Assert.False(response.Usage.ExtensionData?.ContainsKey("cost") ?? false);
        Assert.Equal("/api/v1/images", handler.Endpoint!.AbsolutePath);
        using var document = JsonDocument.Parse(handler.SubmittedBody!);
        var submitted = document.RootElement;
        Assert.Equal("fixture-image", submitted.GetProperty("model").GetString());
        Assert.Equal("fixture prompt", submitted.GetProperty("prompt").GetString());
        Assert.Equal(2, submitted.GetProperty("n").GetInt32());
        Assert.Equal("high", submitted.GetProperty("quality").GetString());
        Assert.Equal("png", submitted.GetProperty("output_format").GetString());
        Assert.False(submitted.GetProperty("options").GetProperty("enabled").GetBoolean());
        if (edit)
            Assert.Equal("data:image/png;base64,AQID", Assert.Single(submitted.GetProperty("input_references").EnumerateArray()).GetString());
        else Assert.False(submitted.TryGetProperty("input_references", out _));
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        public string? SubmittedBody { get; private set; }
        public Uri? Endpoint { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri;
            SubmittedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"created":123,"data":[{"b64_json":"AQID"}],"usage":{"cost":0.123}}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
