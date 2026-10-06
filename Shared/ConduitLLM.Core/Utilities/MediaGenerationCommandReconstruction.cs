using System.Text.Json;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Core.Utilities;

/// <summary>One persisted-request mapping for automatic recovery and operator-authorized retry.</summary>
public static class MediaGenerationCommandReconstruction
{
    public static ReconstructedMediaCommand Build(string taskId, string taskType, TaskMetadata metadata)
    {
        if (taskType == "image_generation")
        {
            var request = JsonSerializer.Deserialize(metadata.Payload ?? "null",
                CoreMessagingJsonContext.Default.ImageGenerationRequested)
                ?? throw new InvalidOperationException("No persisted image request.");
            if (request.VirtualKeyId != metadata.VirtualKeyId || string.IsNullOrWhiteSpace(request.Request?.Model)
                || string.IsNullOrWhiteSpace(request.Request.Prompt))
                throw new InvalidOperationException("Persisted image request is incomplete or has a different owner.");
            return new(request with { TaskId = taskId, RequestedAt = DateTime.UtcNow,
                CorrelationId = metadata.CorrelationId ?? request.CorrelationId ?? taskId }, null);
        }
        if (taskType != "video_generation") throw new InvalidOperationException("Unsupported media task type.");
        VideoGenerationRequest? video = null;
        if (!string.IsNullOrWhiteSpace(metadata.Payload))
            video = JsonSerializer.Deserialize(metadata.Payload, CoreHttpJsonContext.Default.VideoGenerationRequest);
        if (video == null && metadata.ExtensionData?.TryGetValue("Request", out var stored) == true)
            video = stored is JsonElement element
                ? element.Deserialize(CoreHttpJsonContext.Default.VideoGenerationRequest)
                : JsonSerializer.Deserialize(JsonSerializer.Serialize(stored, CoreHttpJsonContext.Default.Object),
                    CoreHttpJsonContext.Default.VideoGenerationRequest);
        if (video == null || string.IsNullOrWhiteSpace(video.Model) || string.IsNullOrWhiteSpace(video.Prompt))
            throw new InvalidOperationException("No complete persisted video request.");
        return new(null, new VideoGenerationRequested
        {
            RequestId = taskId, Request = video, VirtualKeyId = metadata.VirtualKeyId.ToString(), IsAsync = true,
            RequestedAt = DateTime.UtcNow, CorrelationId = metadata.CorrelationId ?? taskId,
            WebhookUrl = metadata.WebhookUrl ?? video.WebhookUrl,
            WebhookHeaders = metadata.WebhookHeaders ?? video.WebhookHeaders
        });
    }
}

public sealed record ReconstructedMediaCommand(ImageGenerationRequested? Image, VideoGenerationRequested? Video);
