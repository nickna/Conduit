using System.Text.Json;
using ConduitLLM.Core.Constants;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class ImageNotificationSerializationTests
{
    [Theory]
    [InlineData("started", false)]
    [InlineData("started", true)]
    [InlineData("progress", false)]
    [InlineData("progress", true)]
    [InlineData("completed", false)]
    [InlineData("completed", true)]
    [InlineData("failed", false)]
    [InlineData("failed", true)]
    [InlineData("cancelled", false)]
    [InlineData("cancelled", true)]
    public async Task LiveNotification_PreservesJsonAndMessagePackContracts(string operation, bool populated)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var capture = new SignalRProtocolCapture<ImageGenerationHub>();
        var service = new ImageGenerationNotificationService(capture, NullLogger<ImageGenerationNotificationService>.Instance);
        var before = DateTime.UtcNow;
        string method;
        string timestamp;
        string[] fields;
        switch (operation)
        {
            case "started":
                await service.NotifyImageGenerationStartedAsync("image-task", "fixture prompt", populated ? 2 : 0, "1024x1024", populated ? "vivid" : null);
                method = SignalRConstants.ClientMethods.ImageGenerationStarted;
                timestamp = "startedAt";
                fields = ["taskId", "prompt", "numberOfImages", "size", "style", timestamp];
                break;
            case "progress":
                await service.NotifyImageGenerationProgressAsync("image-task", populated ? 50 : 0, "rendering", populated ? 1 : 0, populated ? 2 : 0, populated ? "working" : null);
                method = SignalRConstants.ClientMethods.ImageGenerationProgress;
                timestamp = "timestamp";
                fields = ["taskId", "progressPercentage", "status", "imagesCompleted", "totalImages", "message", timestamp];
                break;
            case "completed":
                await service.NotifyImageGenerationCompletedAsync("image-task", populated ? ["https://media.invalid/1", "https://media.invalid/2"] : [], populated ? TimeSpan.FromMilliseconds(1250) : TimeSpan.Zero, populated ? 0.0123m : 0);
                method = SignalRConstants.ClientMethods.ImageGenerationCompleted;
                timestamp = "completedAt";
                fields = ["taskId", "imageUrls", "durationSeconds", "cost", timestamp];
                break;
            case "failed":
                await service.NotifyImageGenerationFailedAsync("image-task", "fixture failure", populated);
                method = SignalRConstants.ClientMethods.ImageGenerationFailed;
                timestamp = "failedAt";
                fields = ["taskId", "error", "isRetryable", timestamp];
                break;
            default:
                await service.NotifyImageGenerationCancelledAsync("image-task", populated ? "fixture reason" : null);
                method = SignalRConstants.ClientMethods.ImageGenerationCancelled;
                timestamp = "cancelledAt";
                fields = ["taskId", "reason", timestamp];
                break;
        }

        Assert.Empty(capture.Errors);
        var invocation = Assert.Single(capture.Invocations);
        Assert.Equal(SignalRConstants.Groups.ImageTask("image-task"), invocation.Group);
        Assert.Equal(method, invocation.Method);
        var json = invocation.Json;
        Assert.Equal(fields.Order(), json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("image-task", json.GetProperty("taskId").GetString());
        Assert.InRange(json.GetProperty(timestamp).GetDateTime(), before, DateTime.UtcNow);
        switch (operation)
        {
            case "started":
                Assert.Equal("fixture prompt", json.GetProperty("prompt").GetString());
                Assert.Equal("1024x1024", json.GetProperty("size").GetString());
                Assert.Equal(populated ? 2 : 0, json.GetProperty("numberOfImages").GetInt32());
                Assert.Equal(populated ? "vivid" : null, json.GetProperty("style").GetString());
                break;
            case "progress":
                Assert.Equal(populated ? 50 : 0, json.GetProperty("progressPercentage").GetInt32());
                Assert.Equal(populated ? 1 : 0, json.GetProperty("imagesCompleted").GetInt32());
                Assert.Equal(populated ? 2 : 0, json.GetProperty("totalImages").GetInt32());
                Assert.Equal("rendering", json.GetProperty("status").GetString());
                Assert.Equal(populated ? "working" : null, json.GetProperty("message").GetString());
                break;
            case "completed":
                Assert.Equal(populated ? 2 : 0, json.GetProperty("imageUrls").GetArrayLength());
                Assert.Equal(populated ? 1.25 : 0, json.GetProperty("durationSeconds").GetDouble());
                Assert.Equal(populated ? 0.0123m : 0, json.GetProperty("cost").GetDecimal());
                break;
            case "failed":
                Assert.Equal("fixture failure", json.GetProperty("error").GetString());
                Assert.Equal(populated, json.GetProperty("isRetryable").GetBoolean());
                break;
            default:
                Assert.Equal(populated ? "fixture reason" : null, json.GetProperty("reason").GetString());
                break;
        }

        var options = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
        var bytes = MessagePackSerializer.Serialize(invocation.Payload.GetType(), invocation.Payload, options);
        using var messagePack = JsonDocument.Parse(MessagePackSerializer.ConvertToJson(bytes, options));
        Assert.Equal(fields.Order(), messagePack.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }
}
