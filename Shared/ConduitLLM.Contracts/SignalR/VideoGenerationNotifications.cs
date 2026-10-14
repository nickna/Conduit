namespace ConduitLLM.Contracts.SignalR;

// Lower-camel CLR names preserve the existing contractless MessagePack map keys.
public sealed record VideoGenerationStartedNotification(
    string taskId, string provider, DateTime startedAt, int? estimatedSeconds);

public sealed record VideoGenerationProgressNotification(
    string taskId, int progressPercentage, string status, string? message,
    int? framesCompleted, int? totalFrames, DateTime timestamp);

public sealed record VideoGenerationCompletedNotification(
    string taskId, string status, string videoUrl, string? previewUrl, double duration,
    string? resolution, long? fileSize, decimal cost, string? provider, string? model,
    DateTime completedAt, double? generationDuration);

public sealed record VideoGenerationFailedNotification(
    string taskId, string status, string error, string? errorCode, bool isRetryable,
    int? retryCount, int? maxRetries, DateTime? nextRetryAt, DateTime failedAt);

public sealed record VideoGenerationCancelledNotification(
    string taskId, string? reason, DateTime cancelledAt);

public sealed record PublicVideoGenerationStartedNotification(
    string taskId, string status, int progress, string message, DateTime startedAt, int? estimatedSeconds);

public sealed record PublicVideoGenerationProgressNotification(
    string taskId, string status, int progress, string? message,
    int? framesCompleted, int? totalFrames, DateTime timestamp);

public sealed record PublicVideoGenerationCompletedNotification(string taskId, string videoUrl);

public sealed record PublicVideoGenerationFailedNotification(string taskId, string error);
