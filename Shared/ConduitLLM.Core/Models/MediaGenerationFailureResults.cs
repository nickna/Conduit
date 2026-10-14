namespace ConduitLLM.Core.Models;

/// <summary>Persisted result of a failed asynchronous image generation.</summary>
public sealed record ImageGenerationFailureResult(
    string Error, string? ErrorCode, string Provider, bool IsRetryable, int AttemptCount);

/// <summary>Persisted result of a failed asynchronous video generation.</summary>
public sealed record VideoGenerationFailureResult(
    string Error, string? ErrorCode, string? Provider, bool IsRetryable, DateTime FailedAt);
