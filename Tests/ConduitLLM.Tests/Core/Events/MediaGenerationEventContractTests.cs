using System.Text.Json;

using ConduitLLM.Core.Events;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Tests.Core.Events;

public class MediaGenerationEventContractTests
{
    [Fact]
    public void VideoEvent_SourceGeneratedTransport_RoundTripsDirectAndLegacyRequests()
    {
        var transport = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = CoreMessagingJsonContext.Default };
        var original = new VideoGenerationRequested { RequestId = "generated-video", VirtualKeyId = "12", IsAsync = true,
            Request = new() { Model = "video-model", Prompt = "video", Duration = 7, WebhookHeaders = new() { ["Authorization"] = "Bearer test" } } };
        var serialized = JsonSerializer.Serialize(original, transport);
        var result = JsonSerializer.Deserialize<VideoGenerationRequested>(serialized, transport)!;
        Assert.Equal(7, result.ResolveRequest().Duration);
        Assert.Equal("Bearer test", result.ResolveRequest().WebhookHeaders!["Authorization"]);
        var legacy = JsonSerializer.Deserialize<VideoGenerationRequested>("""{"requestId":"old","virtualKeyId":"12","model":"old-model","prompt":"old prompt","parameters":{"duration":8}}""", transport)!;
        Assert.Equal("old-model", legacy.ResolveRequest().Model);
        Assert.Equal(8, legacy.ResolveRequest().Duration);
    }

    [Fact]
    public void ImageEvent_DirectRequest_RoundTripsEveryRequestField()
    {
        var eventRequest = new ImageGenerationRequested
        {
            TaskId = "image-task",
            VirtualKeyId = 12,
            Request = new ImageGenerationRequest
            {
                Model = "image-model",
                Prompt = "edit this",
                Image = "base64-image",
                Mask = "base64-mask",
                Operation = "edit",
                Background = "transparent",
                OutputCompression = 80,
                OutputFormat = "webp",
                PartialImages = 2,
                Stream = true,
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["seed"] = JsonSerializer.SerializeToElement(42)
                }
            }
        };

        var json = JsonSerializer.Serialize(eventRequest, ConduitJsonOptions.Wire);
        var roundTrip = JsonSerializer.Deserialize<ImageGenerationRequested>(
            json, ConduitJsonOptions.Wire)!;

        Assert.Equal("base64-image", roundTrip.Request.Image);
        Assert.Equal("base64-mask", roundTrip.Request.Mask);
        Assert.Equal("edit", roundTrip.Request.Operation);
        Assert.Equal("transparent", roundTrip.Request.Background);
        Assert.Equal(80, roundTrip.Request.OutputCompression);
        Assert.Equal("webp", roundTrip.Request.OutputFormat);
        Assert.Equal(2, roundTrip.Request.PartialImages);
        Assert.True(roundTrip.Request.Stream);
        Assert.Equal(42, roundTrip.Request.ExtensionData!["seed"].GetInt32());
    }

    [Fact]
    public void ImageEvent_LegacyNestedExtensionData_IsStillRead()
    {
        const string json = """
            {
              "taskId": "legacy-image",
              "virtualKeyId": 12,
              "request": {
                "prompt": "legacy prompt",
                "model": "legacy-model",
                "image": "base64-image",
                "mask": "base64-mask",
                "operation": "edit",
                "extensionData": { "seed": 7 }
              }
            }
            """;

        var result = JsonSerializer.Deserialize<ImageGenerationRequested>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal("edit", result.Request.Operation);
        Assert.Equal("base64-image", result.Request.Image);
        Assert.Equal(7, result.Request.ExtensionData!["seed"].GetInt32());
    }

    [Fact]
    public void VideoEvent_DirectRequest_RoundTripsEveryRequestField()
    {
        var eventRequest = new VideoGenerationRequested
        {
            RequestId = "video-task",
            VirtualKeyId = "12",
            IsAsync = true,
            Request = new VideoGenerationRequest
            {
                Model = "video-model",
                Prompt = "make a video",
                Duration = 8,
                Size = "1920x1080",
                Fps = 30,
                User = "end-user",
                Seed = 1234,
                N = 2,
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["with_audio"] = JsonSerializer.SerializeToElement(true)
                }
            }
        };

        var json = JsonSerializer.Serialize(eventRequest, ConduitJsonOptions.Wire);
        var roundTrip = JsonSerializer.Deserialize<VideoGenerationRequested>(
            json, ConduitJsonOptions.Wire)!;
        var request = roundTrip.ResolveRequest();

        Assert.Equal("end-user", request.User);
        Assert.Equal(1234, request.Seed);
        Assert.Equal(2, request.N);
        Assert.True(request.ExtensionData!["with_audio"].GetBoolean());
    }

    [Fact]
    public void VideoEvent_LegacyShape_IsStillRead()
    {
        const string json = """
            {
              "requestId": "legacy-video",
              "model": "legacy-model",
              "prompt": "legacy prompt",
              "virtualKeyId": "12",
              "isAsync": true,
              "parameters": {
                "duration": 7,
                "size": "1280x720",
                "fps": 24,
                "style": "cinematic",
                "responseFormat": "url",
                "startImage": "base64-start",
                "providerOptions": {
                  "with_audio": true
                }
              }
            }
            """;

        var result = JsonSerializer.Deserialize<VideoGenerationRequested>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var request = result.ResolveRequest();

        Assert.Equal("legacy-model", request.Model);
        Assert.Equal("legacy prompt", request.Prompt);
        Assert.Equal(7, request.Duration);
        Assert.Equal("1280x720", request.Size);
        Assert.Equal(24, request.Fps);
        Assert.Equal("cinematic", request.Style);
        Assert.Equal("url", request.ResponseFormat);
        Assert.Equal("base64-start", request.ExtensionData!["start_image"].GetString());
        Assert.True(request.ExtensionData["with_audio"].GetBoolean());
    }
}
