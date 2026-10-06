using ConduitLLM.Admin.DTOs;

namespace ConduitLLM.Admin.Endpoints;

/// <summary>Standardized explicit results shared by Admin Minimal-API endpoint groups.</summary>
public static class AdminResults
{
    public static IResult BadRequest(string message, string? code = null) =>
        Problem(StatusCodes.Status400BadRequest, message, code ?? AdminErrorCodes.ValidationError);

    public static IResult NotFound(string message, string? code = null) =>
        Problem(StatusCodes.Status404NotFound, message, code ?? AdminErrorCodes.NotFound);

    public static IResult NotFoundEntity(string entityType, object? entityId = null)
    {
        var message = entityId is null
            ? $"{entityType} not found"
            : $"{entityType} with ID '{entityId}' not found";
        return NotFound(message, AdminErrorCodes.NotFound);
    }

    public static IResult Conflict(string message, string? code = null) =>
        Problem(StatusCodes.Status409Conflict, message, code ?? AdminErrorCodes.Conflict);

    public static IResult ValidationError(string message) =>
        Problem(StatusCodes.Status400BadRequest, message, AdminErrorCodes.ValidationError);

    public static IResult ServiceUnavailable(string message, string? code = null) =>
        Problem(StatusCodes.Status503ServiceUnavailable, message, code ?? AdminErrorCodes.ServiceUnavailable);

    public static IResult Problem(
        int status,
        string detail,
        string? code = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        string? traceId = null,
        IReadOnlyDictionary<string, object?>? extensions = null) =>
        new AdminProblemResult(
            status,
            detail,
            code ?? AdminErrorCodes.ForStatus(status),
            errors,
            traceId,
            extensions);

    /// <summary>
    /// Writes an <see cref="AdminProblemDetails"/> body whose TraceId matches the
    /// x-request-id response header. Both come from HttpContext.TraceIdentifier — the
    /// correlation ID set by CorrelationIdMiddleware, and the same source
    /// ExceptionHandlingMiddlewareBase uses — so explicit error returns and
    /// exception-mapped errors carry identical identifiers (#1262).
    /// </summary>
    private sealed class AdminProblemResult : IResult, IStatusCodeHttpResult
    {
        private readonly int _status;
        private readonly string _detail;
        private readonly string? _code;
        private readonly IReadOnlyDictionary<string, string[]>? _errors;
        private readonly string? _traceId;
        private readonly IReadOnlyDictionary<string, object?>? _extensions;

        public AdminProblemResult(
            int status,
            string detail,
            string? code,
            IReadOnlyDictionary<string, string[]>? errors,
            string? traceId,
            IReadOnlyDictionary<string, object?>? extensions)
        {
            _status = status;
            _detail = detail;
            _code = code;
            _errors = errors;
            _traceId = traceId;
            _extensions = extensions;
        }

        public int? StatusCode => _status;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            var traceId = _traceId ?? httpContext.TraceIdentifier;
            httpContext.Response.Headers["x-request-id"] = traceId;
            return Results.Json(
                new AdminProblemDetails
                {
                    Type = $"https://httpstatuses.com/{_status}",
                    Title = TitleFor(_status),
                    Status = _status,
                    Detail = _detail,
                    Code = _code,
                    TraceId = traceId,
                    Errors = _errors,
                    Extensions = _extensions is null
                        ? new Dictionary<string, object?>()
                        : new Dictionary<string, object?>(_extensions)
                },
                AdminHttpResponseJsonContext.Default.AdminProblemDetails,
                statusCode: _status,
                contentType: "application/problem+json").ExecuteAsync(httpContext);
        }
    }

    private static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status429TooManyRequests => "Too Many Requests",
        StatusCodes.Status501NotImplemented => "Not Implemented",
        StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
        _ => "Internal Server Error"
    };
}
