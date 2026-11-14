using System.Text.Json;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Extensions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Middleware;

/// <summary>
/// Base middleware for global exception handling. Catches unhandled exceptions,
/// logs them with request context, maps them via <see cref="ExceptionToResponseMapper"/>,
/// and writes a JSON error response.
/// Subclasses control the response format (RFC Problem Details vs the OpenAI error envelope).
/// </summary>
public abstract class ExceptionHandlingMiddlewareBase
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _environment;
    private readonly Interfaces.IProviderErrorTranslator? _providerErrorTranslator;
    protected readonly ILogger Logger;

    protected static readonly JsonSerializerOptions ErrorJsonOptions = Serialization.ConduitJsonOptions.Compact;

    /// <summary>
    /// Display name used in log messages (e.g., "AdminExceptionMiddleware").
    /// </summary>
    protected abstract string MiddlewareName { get; }
    protected virtual string ErrorContentType => "application/json";

    protected ExceptionHandlingMiddlewareBase(
        RequestDelegate next,
        ILogger logger,
        IWebHostEnvironment environment,
        Interfaces.IProviderErrorTranslator? providerErrorTranslator = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _providerErrorTranslator = providerErrorTranslator;
    }

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var traceId = context.TraceIdentifier;

        // Capture request body for debugging mutations
        string? requestBody = null;
        try
        {
            requestBody = await RequestBodyCapture.CaptureAsync(context);
        }
        catch
        {
            // Body capture should never prevent error handling
        }

        // Map exception using the shared mapper. Mapped first so the log severity matches the
        // response: client errors (404 model_not_found, 400 invalid_request) must not be logged as
        // errors, or every unknown-model request pages an operator.
        // Provider communication errors go through the customer-mode translator when one is
        // registered (Gateway): CONDUIT_CUSTOMER_MODE decides whether the customer sees raw
        // provider text or a classified generic message. Status mapping is identical either way.
        var mapping = RequestExceptionMapping.Resolve(context, exception, _providerErrorTranslator);

        // Log with or without body
        if (requestBody != null)
        {
            Logger.Log(mapping.LogLevel, exception,
                "{LogPrefix} caught by {MiddlewareName}. TraceId: {TraceId}, Method: {Method}, Path: {Path}, RequestBody: {RequestBody}",
                mapping.LogPrefix, MiddlewareName, traceId,
                LoggingSanitizer.S(context.Request.Method),
                LoggingSanitizer.S(context.Request.Path.ToString()),
                requestBody);
        }
        else
        {
            Logger.Log(mapping.LogLevel, exception,
                "{LogPrefix} caught by {MiddlewareName}. TraceId: {TraceId}, Method: {Method}, Path: {Path}",
                mapping.LogPrefix, MiddlewareName, traceId,
                LoggingSanitizer.S(context.Request.Method),
                LoggingSanitizer.S(context.Request.Path.ToString()));
        }

        // In development, show actual exception messages for redacted responses
        var message = mapping.IncludeExceptionMessageInLog
            ? mapping.ResponseMessage
            : (_environment.IsDevelopment() ? exception.Message : mapping.ResponseMessage);

        // Hook for subclass-specific behavior (metrics, security logging)
        await OnExceptionMappedAsync(context, exception, mapping);

        // Don't try to write if the response has already started
        if (context.Response.HasStarted)
        {
            Logger.LogWarning(
                "Response has already started, cannot write error response for TraceId: {TraceId}",
                traceId);
            return;
        }

        // Set common response headers
        context.Response.StatusCode = mapping.StatusCode;
        context.Response.ContentType = ErrorContentType;
        context.Response.Headers["x-request-id"] = traceId;

        if (exception is RateLimitExceededException rateLimitEx && rateLimitEx.RetryAfterSeconds.HasValue)
        {
            context.Response.Headers["Retry-After"] = rateLimitEx.RetryAfterSeconds.Value.ToString();
        }
        else if (exception is ConduitLLM.Configuration.Interfaces.RedisCircuitBreakerOpenException
                 {
                     RetryAfter: { } retryAfter
                 })
        {
            context.Response.Headers["Retry-After"] =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        // Serialize and write the format-specific response
        var json = CreateErrorResponseJson(message, mapping, traceId);
        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// Called after the exception is mapped but before the response is written.
    /// Override to add metrics, security logging, etc.
    /// </summary>
    protected virtual Task OnExceptionMappedAsync(
        HttpContext context,
        Exception exception,
        ExceptionToResponseMapper.ExceptionMappingResult mapping)
        => Task.CompletedTask;

    /// <summary>
    /// Creates the JSON response body for the error. Subclasses produce their format
    /// (RFC Problem Details or the OpenAI error envelope).
    /// </summary>
    protected abstract string CreateErrorResponseJson(
        string message,
        ExceptionToResponseMapper.ExceptionMappingResult mapping,
        string traceId);
}
