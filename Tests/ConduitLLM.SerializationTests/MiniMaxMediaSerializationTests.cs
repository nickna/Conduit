using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.MiniMax;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class MiniMaxMediaSerializationTests
{
    [Theory]
    [InlineData("urls", false)]
    [InlineData("urls", true)]
    [InlineData("base64", true)]
    [InlineData("images", true)]
    public async Task ImageGeneration_UsesGeneratedHttpContracts(string responseShape, bool base64)
    {
        using var handler = new MediaHandler(responseShape);
        var response = await CreateClient(handler).CreateImageAsync(new ImageGenerationRequest
        {
            Model = "image-01", Prompt = "fixture image", Size = "1024x1024", N = 1,
            ResponseFormat = base64 ? "b64_json" : "url"
        });

        var image = Assert.Single(response.Data);
        Assert.Equal(base64 ? "AQID" : null, image.B64Json);
        Assert.Equal(base64 ? null : "https://cdn.invalid/image.png", image.Url);
        using var document = JsonDocument.Parse(handler.SubmittedBody!);
        var submitted = document.RootElement;
        Assert.Equal("image-01", submitted.GetProperty("model").GetString());
        Assert.Equal("fixture image", submitted.GetProperty("prompt").GetString());
        Assert.Equal("1:1", submitted.GetProperty("aspect_ratio").GetString());
        Assert.Equal("url", submitted.GetProperty("response_format").GetString());
        Assert.Equal(1, submitted.GetProperty("n").GetInt32());
        Assert.True(submitted.GetProperty("prompt_optimizer").GetBoolean());
        Assert.Equal(base64 && responseShape == "urls" ? 2 : 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VideoGeneration_SubmitsAndPollsThroughGeneratedHttpContracts(bool base64)
    {
        using var extension = JsonDocument.Parse("""{"strength":0.5,"enabled":false}""");
        using var handler = new MediaHandler("video");
        var response = await CreateClient(handler).CreateVideoAsync(new VideoGenerationRequest
        {
            Model = "video-01", Prompt = "fixture video", Duration = 6, Size = "1280x720",
            ResponseFormat = base64 ? "b64_json" : "url",
            ExtensionData = new() { ["camera"] = extension.RootElement.Clone() }
        });

        var video = Assert.Single(response.Data);
        Assert.Equal(base64 ? "AQID" : null, video.B64Json);
        Assert.Equal(base64 ? null : "https://provider.invalid/v1/files/retrieve?file_id=file-fixture", video.Url);
        Assert.Equal(640, video.Metadata!.Width);
        Assert.Equal(480, video.Metadata.Height);
        Assert.Equal(6, response.Usage!.TotalDurationSeconds);
        Assert.Null(response.Usage.EstimatedCost);
        Assert.Equal(base64 ? 3 : 2, handler.Requests.Count);
        Assert.Equal("/v1/query/video_generation?task_id=task-fixture", handler.Requests[1].PathAndQuery);
        using var document = JsonDocument.Parse(handler.SubmittedBody!);
        var submitted = document.RootElement;
        Assert.Equal("video-01", submitted.GetProperty("model").GetString());
        Assert.Equal("fixture video", submitted.GetProperty("prompt").GetString());
        Assert.Equal(6, submitted.GetProperty("video_length").GetInt32());
        Assert.Equal("768P", submitted.GetProperty("resolution").GetString());
        Assert.Equal(0.5, submitted.GetProperty("camera").GetProperty("strength").GetDouble());
        Assert.False(submitted.GetProperty("camera").GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaProviderError_PreservesCommunicationError(bool video)
    {
        using var handler = new MediaHandler("error");
        var client = CreateClient(handler);
        var error = video
            ? await Assert.ThrowsAsync<LLMCommunicationException>(() => client.CreateVideoAsync(
                new VideoGenerationRequest { Model = "video-01", Prompt = "fixture" }))
            : await Assert.ThrowsAsync<LLMCommunicationException>(() => client.CreateImageAsync(
                new ImageGenerationRequest { Model = "image-01", Prompt = "fixture" }));
        Assert.Contains("fixture rejection", error.Message);
        Assert.Single(handler.Requests);
    }

    private static MiniMaxClient CreateClient(MediaHandler handler) => new(
        new Provider { Id = 1, ProviderName = "fixture", ProviderType = ProviderType.MiniMax, BaseUrl = "https://provider.invalid" },
        new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "fixture-key" },
        "fixture-model", NullLogger<MiniMaxClient>.Instance, new ClientFactory(handler));

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MediaHandler(string shape) : HttpMessageHandler
    {
        public string? SubmittedBody { get; private set; }
        public List<Uri> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "cdn.invalid")
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            string json;
            if (request.Method == HttpMethod.Post)
            {
                SubmittedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                json = shape switch
                {
                    "urls" => """{"data":{"image_urls":["https://cdn.invalid/image.png"]},"base_resp":{"status_code":0}}""",
                    "base64" => """{"data":{"image_base64":["AQID"]},"base_resp":{"status_code":0}}""",
                    "images" => """{"data":{"images":[{"b64":"AQID"}]},"base_resp":{"status_code":0}}""",
                    "video" => """{"task_id":"task-fixture","base_resp":{"status_code":0}}""",
                    "error" => """{"base_resp":{"status_code":1004,"status_msg":"fixture rejection"}}""",
                    _ => throw new InvalidOperationException("Unexpected fixture shape")
                };
            }
            else if (request.RequestUri.AbsolutePath == "/v1/query/video_generation")
                json = """{"status":"Success","file_id":"file-fixture","video_width":640,"video_height":480,"video":{"url":"https://cdn.invalid/video.mp4","duration":6},"base_resp":{"status_code":0}}""";
            else throw new InvalidOperationException($"Unexpected fixture request: {request.RequestUri}");
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
