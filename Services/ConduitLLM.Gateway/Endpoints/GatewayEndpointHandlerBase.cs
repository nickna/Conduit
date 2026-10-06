using System.Security.Claims;
using System.Text.Json;

using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Endpoints;

/// <summary>Shared non-MVC support for stateful Gateway endpoint handlers.</summary>
public abstract class GatewayEndpointHandlerBase
{
    private readonly IEventPublisher? _eventPublisher;
    private readonly IHttpContextAccessor _httpContextAccessor;

    protected GatewayEndpointHandlerBase(
        IEventPublisher? eventPublisher,
        IHttpContextAccessor httpContextAccessor,
        ILogger logger)
    {
        _eventPublisher = eventPublisher;
        _httpContextAccessor = httpContextAccessor;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected ILogger Logger { get; }
    protected HttpContext HttpContext => _httpContextAccessor.HttpContext
        ?? throw new InvalidOperationException("No active HTTP context is available.");
    protected HttpRequest Request => HttpContext.Request;
    protected HttpResponse Response => HttpContext.Response;
    protected ClaimsPrincipal User => HttpContext.User;

    protected int? CurrentVirtualKeyId
    {
        get
        {
            if (HttpContext.Items.TryGetValue("VirtualKeyId", out var value) && value is int id) return id;
            return int.TryParse(User.FindFirst("VirtualKeyId")?.Value, out var parsed) ? parsed : null;
        }
    }

    protected string? CurrentVirtualKey =>
        HttpContext.Items.TryGetValue("VirtualKey", out var value) && value is string key && key.Length > 0
            ? key
            : User.FindFirst("VirtualKey")?.Value;

    protected static IResult Ok<T>(T value) => Results.Ok(value);
    protected static IResult Ok() => Results.Ok();
    protected static IResult BadRequest<T>(T value) => Results.BadRequest(value);
    protected static IResult NotFound<T>(T value) => Results.NotFound(value);
    protected static IResult NotFound() =>
        GatewayResults.OpenAIError(StatusCodes.Status404NotFound, "Resource not found", "not_found", "not_found_error");
    protected static IResult Conflict<T>(T value) => Results.Conflict(value);
    protected static IResult Accepted(AsyncTaskResponse value) =>
        Results.Json(value, GatewayHttpJsonContext.Default.AsyncTaskResponse, statusCode: StatusCodes.Status202Accepted);
    protected static IResult Forbid(params string[] messages) =>
        GatewayResults.OpenAIError(
            StatusCodes.Status403Forbidden,
            messages.FirstOrDefault() ?? "Access forbidden",
            "forbidden",
            "permission_error");
    protected static IResult NoContent() => Results.NoContent();
    protected static IResult File(Stream stream, string contentType, string? fileDownloadName = null, bool enableRangeProcessing = false) =>
        Results.Stream(stream, contentType, fileDownloadName, enableRangeProcessing: enableRangeProcessing);
    protected static IResult File(byte[] contents, string contentType, string? fileDownloadName = null, bool enableRangeProcessing = false) =>
        Results.File(contents, contentType, fileDownloadName, enableRangeProcessing: enableRangeProcessing);
    protected static IResult Content(string content, string contentType) => Results.Text(content, contentType);
    protected static IResult StatusCode(int statusCode, string value) =>
        Results.Json(value, ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.String, statusCode: statusCode);
    protected static IResult OpenAIError(
        int statusCode,
        string message,
        string code,
        string type = "invalid_request_error",
        string? param = null,
        JsonElement? metadata = null) =>
        GatewayResults.OpenAIError(statusCode, message, code, type, param, metadata);

    protected void PublishEventFireAndForget<TEvent>(TEvent domainEvent, string operationName, object? contextData = null)
        where TEvent : class
        => _eventPublisher?.PublishFireAndForget(domainEvent, operationName, contextData);
}
