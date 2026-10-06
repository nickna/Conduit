using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Core.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

public static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/notifications")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Notifications");
        group.MapGet("/", GetAll).WithName("Notifications_GetAll")
            .Produces<IEnumerable<NotificationDto>>(StatusCodes.Status200OK);
        group.MapGet("/unread", GetUnread).WithName("Notifications_GetUnread")
            .Produces<IEnumerable<NotificationDto>>(StatusCodes.Status200OK);
        group.MapGet("/{id}", GetById).WithName("Notifications_GetById")
            .Produces<NotificationDto>(StatusCodes.Status200OK)
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPost("/", Create).WithName("Notifications_Create")
            .Produces<NotificationDto>(StatusCodes.Status201Created)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        group.MapPatch("/{id}", Update).AcceptsJsonMergePatch<UpdateNotificationDto>().WithName("Notifications_Update")
            .Produces<NotificationDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPost("/{id}/read", MarkAsRead).WithName("Notifications_MarkAsRead")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPost("/mark-all-read", MarkAllAsRead).WithName("Notifications_MarkAllAsRead")
            .Produces<int>(StatusCodes.Status200OK);
        group.MapDelete("/{id}", Delete).WithName("Notifications_Delete")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        return app;
    }

    private static async Task<IResult> GetAll([FromServices] IAdminNotificationService service) =>
        Results.Ok(await service.GetAllNotificationsAsync());
    private static async Task<IResult> GetUnread([FromServices] IAdminNotificationService service) =>
        Results.Ok(await service.GetUnreadNotificationsAsync());
    private static async Task<IResult> GetById(int id, [FromServices] IAdminNotificationService service)
    {
        var value = await service.GetNotificationByIdAsync(id);
        return value is null ? AdminResults.NotFoundEntity("Notification", id) : Results.Ok(value);
    }
    private static async Task<IResult> Create(
        CreateNotificationDto notification,
        [FromServices] IAdminNotificationService service,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var result = await service.CreateNotificationAsync(notification);
        AdminAudit.Log(context, Logger(loggerFactory), "Created", "Notification", result.Id,
            $"Type: {result.Type}, Message: {LoggingSanitizer.S(result.Message)}");
        return Results.Created($"/v1/admin/notifications/{result.Id}", result);
    }
    private static async Task<IResult> Update(
        int id,
        JsonMergePatch<UpdateNotificationDto> patch,
        [FromServices] IAdminNotificationService service,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var notification = patch.Value;
        if (!await service.UpdateNotificationAsync(id, notification)) throw new KeyNotFoundException();
        AdminAudit.Log(context, Logger(loggerFactory), "Updated", "Notification", id,
            notification.Message is null ? null : $"Message: {LoggingSanitizer.S(notification.Message)}");
        return Results.Ok(await service.GetNotificationByIdAsync(id) ?? throw new KeyNotFoundException());
    }
    private static async Task<IResult> MarkAsRead(
        int id, [FromServices] IAdminNotificationService service, HttpContext context, ILoggerFactory loggerFactory)
    {
        if (!await service.MarkNotificationAsReadAsync(id)) throw new KeyNotFoundException();
        AdminAudit.Log(context, Logger(loggerFactory), "MarkedAsRead", "Notification", id, "IsRead: true");
        return Results.NoContent();
    }
    private static async Task<IResult> MarkAllAsRead(
        [FromServices] IAdminNotificationService service, HttpContext context, ILoggerFactory loggerFactory)
    {
        var count = await service.MarkAllNotificationsAsReadAsync();
        AdminAudit.Log(context, Logger(loggerFactory), "MarkedAllAsRead", "Notification", detail: $"Count: {count}");
        return Results.Ok(count);
    }
    private static async Task<IResult> Delete(
        int id, [FromServices] IAdminNotificationService service, HttpContext context, ILoggerFactory loggerFactory)
    {
        if (!await service.DeleteNotificationAsync(id)) throw new KeyNotFoundException();
        AdminAudit.Log(context, Logger(loggerFactory), "Deleted", "Notification", id, $"Id: {id}");
        return Results.NoContent();
    }
    private static ILogger Logger(ILoggerFactory factory) =>
        factory.CreateLogger("ConduitLLM.Admin.Endpoints.Notifications");
}
