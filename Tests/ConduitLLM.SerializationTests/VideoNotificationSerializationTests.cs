using System.Text.Json;
using ConduitLLM.Core.Constants;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class VideoNotificationSerializationTests
{
    [Theory]
    [InlineData("started", 0)]
    [InlineData("started", 1)]
    [InlineData("started", 2)]
    [InlineData("progress", 0)]
    [InlineData("progress", 1)]
    [InlineData("progress", 2)]
    [InlineData("completed", 0)]
    [InlineData("completed", 1)]
    [InlineData("completed", 2)]
    [InlineData("failed", 0)]
    [InlineData("failed", 1)]
    [InlineData("failed", 2)]
    [InlineData("cancelled", 0)]
    [InlineData("cancelled", 1)]
    [InlineData("cancelled", 2)]
    public async Task LiveNotifications_PreservePrivateAndPublicWireContracts(string operation, int variant)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var privateCapture = new SignalRProtocolCapture<VideoGenerationHub>();
        var publicCapture = new SignalRProtocolCapture<PublicVideoGenerationHub>();
        var service = new VideoGenerationNotificationService(privateCapture, publicCapture, NullLogger<VideoGenerationNotificationService>.Instance);
        var fixedTime = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);
        var before = DateTime.UtcNow;
        int? optionalCount = variant == 0 ? null : variant == 1 ? 0 : 12;
        string? optionalText = variant == 0 ? null : variant == 1 ? "" : "fixture";
        var percentage = variant == 2 ? 75 : 0;
        string privateMethod, publicMethod;
        string[] privateFields, publicFields;
        switch (operation)
        {
            case "started":
                await service.NotifyVideoGenerationStartedAsync("video-task", "fixture-provider", fixedTime, optionalCount);
                privateMethod = SignalRConstants.ClientMethods.VideoGenerationStarted;
                publicMethod = SignalRConstants.ClientMethods.TaskProgress;
                privateFields = ["taskId", "provider", "startedAt", "estimatedSeconds"];
                publicFields = ["taskId", "status", "progress", "message", "startedAt", "estimatedSeconds"];
                break;
            case "progress":
                await service.NotifyVideoGenerationProgressAsync("video-task", percentage, "rendering", optionalText, optionalCount, optionalCount);
                privateMethod = SignalRConstants.ClientMethods.VideoGenerationProgress;
                publicMethod = SignalRConstants.ClientMethods.TaskProgress;
                privateFields = ["taskId", "progressPercentage", "status", "message", "framesCompleted", "totalFrames", "timestamp"];
                publicFields = ["taskId", "status", "progress", "message", "framesCompleted", "totalFrames", "timestamp"];
                break;
            case "completed":
                await service.NotifyVideoGenerationCompletedAsync("video-task", "https://media.invalid/video", variant == 2 ? TimeSpan.FromMilliseconds(1250) : TimeSpan.Zero,
                    variant == 2 ? 0.0123m : 0, optionalText, optionalText, optionalCount, optionalText, optionalText,
                    variant == 0 ? null : fixedTime, variant == 0 ? null : variant == 1 ? 0 : 1.5);
                privateMethod = SignalRConstants.ClientMethods.VideoGenerationCompleted;
                publicMethod = SignalRConstants.ClientMethods.TaskCompleted;
                privateFields = ["taskId", "status", "videoUrl", "previewUrl", "duration", "resolution", "fileSize", "cost", "provider", "model", "completedAt", "generationDuration"];
                publicFields = ["taskId", "videoUrl"];
                break;
            case "failed":
                await service.NotifyVideoGenerationFailedAsync("video-task", "fixture failure", variant == 2, optionalText, optionalCount, optionalCount,
                    variant == 0 ? null : fixedTime, variant == 0 ? null : fixedTime);
                privateMethod = SignalRConstants.ClientMethods.VideoGenerationFailed;
                publicMethod = SignalRConstants.ClientMethods.TaskFailed;
                privateFields = ["taskId", "status", "error", "errorCode", "isRetryable", "retryCount", "maxRetries", "nextRetryAt", "failedAt"];
                publicFields = ["taskId", "error"];
                break;
            default:
                await service.NotifyVideoGenerationCancelledAsync("video-task", optionalText);
                privateMethod = SignalRConstants.ClientMethods.VideoGenerationCancelled;
                publicMethod = SignalRConstants.ClientMethods.TaskFailed;
                privateFields = ["taskId", "reason", "cancelledAt"];
                publicFields = ["taskId", "error"];
                break;
        }

        Assert.Empty(privateCapture.Errors);
        Assert.Empty(publicCapture.Errors);
        var privateInvocation = Assert.Single(privateCapture.Invocations);
        var publicInvocation = Assert.Single(publicCapture.Invocations);
        AssertWire(privateInvocation, privateMethod, privateFields);
        AssertWire(publicInvocation, publicMethod, publicFields);
        var json = privateInvocation.Json;
        var publicJson = publicInvocation.Json;
        switch (operation)
        {
            case "started":
                Assert.Equal("fixture-provider", json.GetProperty("provider").GetString());
                Assert.Equal(fixedTime, json.GetProperty("startedAt").GetDateTime());
                AssertOptionalNumber(json, "estimatedSeconds", optionalCount);
                AssertOptionalNumber(publicJson, "estimatedSeconds", optionalCount);
                Assert.Equal(fixedTime, publicJson.GetProperty("startedAt").GetDateTime());
                Assert.Equal("started", publicJson.GetProperty("status").GetString());
                Assert.Equal(0, publicJson.GetProperty("progress").GetInt32());
                Assert.Equal("Video generation started with fixture-provider", publicJson.GetProperty("message").GetString());
                break;
            case "progress":
                Assert.Equal(percentage, json.GetProperty("progressPercentage").GetInt32());
                Assert.Equal(percentage, publicJson.GetProperty("progress").GetInt32());
                foreach (var progress in new[] { json, publicJson })
                {
                    Assert.Equal("rendering", progress.GetProperty("status").GetString());
                    Assert.Equal(optionalText, progress.GetProperty("message").GetString());
                    AssertOptionalNumber(progress, "framesCompleted", optionalCount);
                    AssertOptionalNumber(progress, "totalFrames", optionalCount);
                    Assert.InRange(progress.GetProperty("timestamp").GetDateTime(), before, DateTime.UtcNow);
                }
                break;
            case "completed":
                Assert.Equal("completed", json.GetProperty("status").GetString());
                Assert.Equal("https://media.invalid/video", json.GetProperty("videoUrl").GetString());
                Assert.Equal("https://media.invalid/video", publicJson.GetProperty("videoUrl").GetString());
                Assert.Equal(variant == 2 ? 1.25 : 0, json.GetProperty("duration").GetDouble());
                Assert.Equal(variant == 2 ? 0.0123m : 0, json.GetProperty("cost").GetDecimal());
                foreach (var field in new[] { "previewUrl", "resolution", "provider", "model" })
                    Assert.Equal(optionalText, json.GetProperty(field).GetString());
                AssertOptionalNumber(json, "fileSize", optionalCount);
                if (variant == 0) Assert.Equal(JsonValueKind.Null, json.GetProperty("generationDuration").ValueKind);
                else Assert.Equal(variant == 1 ? 0 : 1.5, json.GetProperty("generationDuration").GetDouble());
                AssertTimestamp(json, "completedAt");
                break;
            case "failed":
                Assert.Equal("failed", json.GetProperty("status").GetString());
                Assert.Equal("fixture failure", json.GetProperty("error").GetString());
                Assert.Equal("fixture failure", publicJson.GetProperty("error").GetString());
                Assert.Equal(optionalText, json.GetProperty("errorCode").GetString());
                Assert.Equal(variant == 2, json.GetProperty("isRetryable").GetBoolean());
                AssertOptionalNumber(json, "retryCount", optionalCount);
                AssertOptionalNumber(json, "maxRetries", optionalCount);
                if (variant == 0) Assert.Equal(JsonValueKind.Null, json.GetProperty("nextRetryAt").ValueKind);
                else Assert.Equal(fixedTime, json.GetProperty("nextRetryAt").GetDateTime());
                AssertTimestamp(json, "failedAt");
                break;
            default:
                Assert.Equal(optionalText, json.GetProperty("reason").GetString());
                Assert.Equal(optionalText ?? "Video generation was cancelled.", publicJson.GetProperty("error").GetString());
                Assert.InRange(json.GetProperty("cancelledAt").GetDateTime(), before, DateTime.UtcNow);
                break;
        }
        return;

        void AssertTimestamp(JsonElement payload, string field)
        {
            if (variant == 0) Assert.InRange(payload.GetProperty(field).GetDateTime(), before, DateTime.UtcNow);
            else Assert.Equal(fixedTime, payload.GetProperty(field).GetDateTime());
        }
    }

    private static void AssertOptionalNumber(JsonElement json, string field, int? expected)
    {
        if (expected is null) Assert.Equal(JsonValueKind.Null, json.GetProperty(field).ValueKind);
        else Assert.Equal(expected.Value, json.GetProperty(field).GetInt64());
    }

    private static void AssertWire(CapturedInvocation invocation, string method, string[] fields)
    {
        Assert.Equal(SignalRConstants.Groups.VideoTask("video-task"), invocation.Group);
        Assert.Equal(method, invocation.Method);
        Assert.Equal("video-task", invocation.Json.GetProperty("taskId").GetString());
        Assert.Equal(fields.Order(), invocation.Json.EnumerateObject().Select(p => p.Name).Order());
        var options = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
        var bytes = MessagePackSerializer.Serialize(invocation.Payload.GetType(), invocation.Payload, options);
        using var messagePack = JsonDocument.Parse(MessagePackSerializer.ConvertToJson(bytes, options));
        Assert.Equal(fields.Order(), messagePack.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }
}
