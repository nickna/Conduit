using System.Security.Claims;

using ConduitLLM.Admin.Auditing;
using ConduitLLM.Core.Services;

namespace ConduitLLM.Admin.Endpoints;

/// <summary>Shared non-MVC support for stateful Admin endpoint handlers.</summary>
public abstract class AdminEndpointHandlerBase
{
    private readonly IEventPublisher? _eventPublisher;
    private readonly IHttpContextAccessor _httpContextAccessor;

    protected AdminEndpointHandlerBase(
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
    protected ClaimsPrincipal User => HttpContext.User;

    protected void LogAdminAudit(string operation, string entityType, object? entityId = null, string? detail = null) =>
        AdminAudit.Log(HttpContext, Logger, operation, entityType, entityId, detail);

    protected void LogAdminAuditWithChanges(
        string entityType,
        object? entityId,
        IReadOnlyList<(string Property, string? OldValue, string? NewValue)> changes,
        string? detail = null) =>
        AdminAudit.LogWithChanges(HttpContext, Logger, entityType, entityId, changes, detail);

    protected void LogAdminAuditBulk(string operation, string entityType, int successCount, int failureCount) =>
        AdminAudit.LogBulk(HttpContext, Logger, operation, entityType, successCount, failureCount);

    protected void LogAdminAuditStateChange(string entityType, object? entityId, string property, object newValue) =>
        AdminAudit.LogStateChange(HttpContext, Logger, entityType, entityId, property, newValue);

    protected void PublishEventFireAndForget<TEvent>(TEvent domainEvent, string operationName, object? contextData = null)
        where TEvent : class
        => _eventPublisher?.PublishFireAndForget(domainEvent, operationName, contextData);

    protected static IResult Ok<T>(T value) => Results.Ok(value);
    protected static IResult BadRequest(string message) => AdminResults.BadRequest(message);
    protected static IResult NotFound(string message) => AdminResults.NotFound(message);
    protected static IResult Conflict(string message) => AdminResults.Conflict(message);
    protected static IResult NoContent() => Results.NoContent();
    protected static IResult StatusCode(int statusCode, string value) =>
        Results.Json(value, AdminInternalJsonContext.Default.String, statusCode: statusCode);
}
