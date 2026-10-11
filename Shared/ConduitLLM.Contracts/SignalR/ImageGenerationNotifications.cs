namespace ConduitLLM.Contracts.SignalR;

// Lower-camel CLR names preserve the existing contractless MessagePack map keys.
public sealed record ImageGenerationStartedNotification(
    string taskId, string prompt, int numberOfImages, string size, string? style, DateTime startedAt);

public sealed record ImageGenerationProgressNotification(
    string taskId, int progressPercentage, string status, int imagesCompleted, int totalImages,
    string? message, DateTime timestamp);

public sealed record ImageGenerationCompletedNotification(
    string taskId, string[] imageUrls, double durationSeconds, decimal cost, DateTime completedAt);

public sealed record ImageGenerationFailedNotification(
    string taskId, string error, bool isRetryable, DateTime failedAt);

public sealed record ImageGenerationCancelledNotification(
    string taskId, string? reason, DateTime cancelledAt);
