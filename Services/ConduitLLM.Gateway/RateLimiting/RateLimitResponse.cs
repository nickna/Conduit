using System.Text.Json;

using ConduitLLM.Core.Models;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.RateLimiting;

/// <summary>
/// Single source of the 429 and fail-closed 503 contracts. Limits are enforced in two places —
/// middleware for the windows knowable before the body is read, an endpoint filter for the ones
/// that need the bound request — and a client must not be able to tell which one rejected it.
/// </summary>
public static class RateLimitResponse
{
    /// <summary>
    /// Retry hint for the fail-closed 503: the store outage is expected to be brief, so a
    /// short fixed pause beats a window-derived one the gateway cannot compute anyway.
    /// </summary>
    private const string DegradedRetryAfterSeconds = "5";

    /// <summary>
    /// Sets the informational X-RateLimit-* family. Written on allowed responses too, so
    /// clients can pace themselves before they are ever throttled.
    /// </summary>
    public static void SetHeaders(HttpContext context, long limit, long remaining, DateTime resetsAt, string scope)
        => RateLimitResponseContract.SetHeaders(context, limit, remaining, resetsAt, scope);

    /// <summary>
    /// Seconds a client should wait, rounded up. Truncating would advertise an instant
    /// fractionally before the window frees, so a client retrying on the hint is rejected again.
    /// </summary>
    public static int RetryAfterSeconds(DateTime resetsAt) =>
        RateLimitResponseContract.RetryAfterSeconds(resetsAt);

    /// <summary>
    /// Reset instant as whole unix seconds, rounded up for the same reason.
    /// </summary>
    public static long ResetUnixSeconds(DateTime resetsAt)
        => RateLimitResponseContract.ResetUnixSeconds(resetsAt);

    /// <summary>
    /// Writes the 429 body and Retry-After directly to the response. Used by middleware, which
    /// has no <c>IResult</c> to return.
    /// </summary>
    public static async Task WriteAsync(HttpContext context, string scope, long limit, DateTime resetsAt, string? message = null)
        => await RateLimitResponseContract.WriteAsync(context, scope, limit, resetsAt, message);

    /// <summary>
    /// Builds the identical response as an <see cref="IResult"/> for endpoint filters.
    /// </summary>
    public static IResult AsResult(HttpContext context, string scope, long limit, DateTime resetsAt, string? message = null)
    {
        var retryAfter = RetryAfterSeconds(resetsAt);
        context.Response.Headers["Retry-After"] = retryAfter.ToString();

        return Results.Json(
            RateLimitResponseContract.BuildError(scope, limit, retryAfter, message),
            GatewayHttpJsonContext.Default.OpenAIErrorResponse,
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Refuses a request whose limits could not be evaluated, under a fail-closed policy.
    /// 503 rather than 429: the caller has not exceeded anything, the gateway simply cannot
    /// tell. Conflating the two would have clients back off against a quota they may be
    /// nowhere near. Used by middleware, which has no <c>IResult</c> to return.
    /// </summary>
    public static Task WriteDegradedAsync(HttpContext context, string scope)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = DegradedRetryAfterSeconds;
        context.Response.Headers["X-RateLimit-Scope"] = scope;
        context.Response.ContentType = "application/json";

        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            BuildDegradedError(),
            GatewayHttpJsonContext.Default.OpenAIErrorResponse);
    }

    /// <summary>
    /// The identical fail-closed 503 as an <see cref="IResult"/> for endpoint filters.
    /// </summary>
    public static IResult DegradedResult(HttpContext context, string scope)
    {
        context.Response.Headers["Retry-After"] = DegradedRetryAfterSeconds;
        context.Response.Headers["X-RateLimit-Scope"] = scope;

        return Results.Json(
            BuildDegradedError(),
            GatewayHttpJsonContext.Default.OpenAIErrorResponse,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static OpenAIErrorResponse BuildDegradedError() =>
        new()
        {
            Error = new OpenAIError
            {
                Message = "Rate limits cannot be verified right now and this deployment is configured to fail closed. Retry shortly.",
                Type = "service_unavailable",
                Code = "rate_limit_unavailable"
            }
        };
}
