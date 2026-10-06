using System.Text.Json;

using ConduitLLM.Core.Models;

using Microsoft.AspNetCore.Http;

namespace ConduitLLM.Core.Utilities;

/// <summary>
/// Builds the canonical OpenAI-compatible rate-limit response and its timing headers.
/// </summary>
public static class RateLimitResponseContract
{
    public static void SetHeaders(
        HttpContext context,
        long limit,
        long remaining,
        DateTime resetsAt,
        string scope)
    {
        context.Response.Headers["X-RateLimit-Limit"] = limit.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, remaining).ToString();
        context.Response.Headers["X-RateLimit-Reset"] = ResetUnixSeconds(resetsAt).ToString();
        if (!string.IsNullOrEmpty(scope))
        {
            context.Response.Headers["X-RateLimit-Scope"] = scope;
        }
    }

    public static int RetryAfterSeconds(DateTime resetsAt) =>
        Math.Max(1, (int)Math.Ceiling((NormalizeUtc(resetsAt) - DateTime.UtcNow).TotalSeconds));

    public static long ResetUnixSeconds(DateTime resetsAt)
    {
        var milliseconds = new DateTimeOffset(NormalizeUtc(resetsAt)).ToUnixTimeMilliseconds();
        return (milliseconds + 999) / 1000;
    }

    public static async Task WriteAsync(
        HttpContext context,
        string scope,
        long limit,
        DateTime resetsAt,
        string? message = null)
    {
        var retryAfter = RetryAfterSeconds(resetsAt);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = retryAfter.ToString();
        context.Response.ContentType = "application/json";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            BuildError(scope, limit, retryAfter, message),
            Serialization.CoreHttpJsonContext.Default.OpenAIErrorResponse,
            cancellationToken: context.RequestAborted);
    }

    public static OpenAIErrorResponse BuildError(
        string scope,
        long limit,
        int retryAfterSeconds,
        string? message = null) =>
        new()
        {
            Error = new OpenAIError
            {
                Message = message ?? $"{scope} rate limit exceeded ({limit}). Retry after {retryAfterSeconds} seconds.",
                Type = "rate_limit_exceeded",
                Code = "rate_limit_exceeded"
            }
        };

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
