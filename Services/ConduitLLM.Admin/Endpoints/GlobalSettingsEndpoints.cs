using ConduitLLM.Core.OpenApi;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Events;
using ConduitLLM.Configuration.Messaging;

using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

public static class GlobalSettingsEndpoints
{
    public static IEndpointRouteBuilder MapGlobalSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/global-settings")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("GlobalSettings");
        group.MapGet("/", List).WithName("GlobalSettings_List")
            .WithSummary("List global setting values").Produces<IEnumerable<GlobalSettingDto>>();
        group.MapGet("/definitions", GetDefinitions)
            .WithName("GlobalSettings_GetDefinitions")
            .WithSummary("List typed global setting definitions")
            .Produces<IReadOnlyList<GlobalSettingDefinitionDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);
        group.MapGet("/{id:int}", GetById).WithName("GlobalSettings_GetById")
            .WithSummary("Get a global setting by ID")
            .WithParameterDescription("id", "Identifier of the GlobalSettings resource.")
            .Produces<GlobalSettingDto>().Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapGet("/by-key/{key}", GetByKey).WithName("GlobalSettings_GetByKey")
            .WithSummary("Get a global setting by key")
            .WithParameterDescription("key", "Global setting key.")
            .Produces<GlobalSettingDto>().Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPost("/", Create).WithName("GlobalSettings_Create")
            .WithSummary("Create a global setting")
            .Produces<GlobalSettingDto>(StatusCodes.Status201Created)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        group.MapPatch("/{id:int}", Update).AcceptsJsonMergePatch<UpdateGlobalSettingDto>().WithName("GlobalSettings_Update")
            .WithSummary("Update a global setting by ID")
            .WithParameterDescription("id", "Identifier of the GlobalSettings resource.")
            .Produces<GlobalSettingDto>().Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPut("/by-key", UpdateByKey).WithName("GlobalSettings_UpdateByKey")
            .WithSummary("Update a global setting by key")
            .Produces(StatusCodes.Status204NoContent).Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        group.MapDelete("/{id:int}", Delete).WithName("GlobalSettings_Delete")
            .WithSummary("Delete a global setting by ID")
            .WithParameterDescription("id", "Identifier of the GlobalSettings resource.")
            .Produces(StatusCodes.Status204NoContent).Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapDelete("/by-key/{key}", DeleteByKey).WithName("GlobalSettings_DeleteByKey")
            .WithSummary("Delete a global setting by key")
            .WithParameterDescription("key", "Global setting key.")
            .Produces(StatusCodes.Status204NoContent).Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapGet("/cache/stats", GetCacheStats).WithName("GlobalSettings_GetCacheStats")
            .WithSummary("Get global settings cache statistics")
            .WithMetadata(new ObsoleteAttribute(
                "Use conduit cache metrics in the Infrastructure Grafana dashboard."))
            .Produces<CacheStats>();
        group.MapPost("/cache/reload", ReloadCache).WithName("GlobalSettings_ReloadCache")
            .WithSummary("Reload the global settings cache")
            .Produces<GlobalSettingsReloadAcceptedResponse>(StatusCodes.Status202Accepted);
        group.MapPost("/cache/invalidate/{key}", InvalidateCache).WithName("GlobalSettings_InvalidateCache")
            .WithSummary("Invalidate the global settings cache")
            .WithParameterDescription("key", "Global setting key.")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }

    private static async Task<IResult> List([FromServices] IAdminGlobalSettingService service) =>
        Results.Ok(await service.GetAllSettingsAsync());

    private static IResult GetDefinitions() =>
        Results.Ok(GlobalSettingDefinitionRegistry.All);

    private static async Task<IResult> GetById(int id, [FromServices] IAdminGlobalSettingService service)
    {
        var setting = await service.GetSettingByIdAsync(id);
        return setting is null ? AdminResults.NotFoundEntity("Global setting", id) : Results.Ok(setting);
    }

    private static async Task<IResult> GetByKey(string key, [FromServices] IAdminGlobalSettingService service)
    {
        var setting = await service.GetSettingByKeyAsync(key);
        return setting is null ? AdminResults.NotFoundEntity("Global setting", key) : Results.Ok(setting);
    }

    private static async Task<IResult> Create(
        [FromBody] CreateGlobalSettingDto setting,
        [FromServices] IAdminGlobalSettingService service,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var created = await service.CreateSettingAsync(setting);
        AdminAudit.Log(context, Logger(loggerFactory), "Created", "GlobalSetting", created.Id,
            $"Key: {LoggingSanitizer.S(setting.Key)}");
        AdminOperationsMetricsService.RecordConfigurationChange("globalsetting", "create");
        return Results.Created($"/v1/admin/global-settings/{created.Id}", created);
    }

    private static async Task<IResult> Update(
        int id,
        JsonMergePatch<UpdateGlobalSettingDto> patch,
        [FromServices] IAdminGlobalSettingService service,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var setting = patch.Value;
        var preState = await service.GetSettingByIdAsync(id) ?? throw new KeyNotFoundException();
        if (!await service.UpdateSettingAsync(id, setting))
        {
            throw new KeyNotFoundException();
        }
        var changes = new List<(string Property, string? OldValue, string? NewValue)>();
        if (setting.IsDefined(nameof(setting.Value)) && preState.Value != setting.Value)
            changes.Add(("Value", preState.Value, setting.Value));
        if (setting.IsDefined(nameof(setting.Description)) && preState.Description != setting.Description)
            changes.Add(("Description", preState.Description, setting.Description));
        if (changes.Count > 0)
        {
            AdminAudit.LogWithChanges(context, Logger(loggerFactory), "GlobalSetting", id, changes,
                $"Key: {LoggingSanitizer.S(preState.Key)}");
        }
        else
        {
            AdminAudit.Log(context, Logger(loggerFactory), "Updated", "GlobalSetting", id);
        }
        AdminOperationsMetricsService.RecordConfigurationChange("globalsetting", "update");
        return Results.Ok(await service.GetSettingByIdAsync(id) ?? throw new KeyNotFoundException());
    }

    private static async Task<IResult> UpdateByKey(
        [FromBody] UpdateGlobalSettingByKeyDto setting,
        [FromServices] IAdminGlobalSettingService service,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        if (!await service.UpdateSettingByKeyAsync(setting))
            throw new InvalidOperationException("Failed to update or create global setting");
        AdminAudit.Log(context, Logger(loggerFactory), "Updated", "GlobalSetting",
            detail: $"Key: {LoggingSanitizer.S(setting.Key)}");
        AdminOperationsMetricsService.RecordConfigurationChange("globalsetting", "update");
        return Results.NoContent();
    }

    private static async Task<IResult> Delete(
        int id,
        [FromServices] IAdminGlobalSettingService service,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        if (!await service.DeleteSettingAsync(id)) throw new KeyNotFoundException();
        AdminAudit.Log(context, Logger(loggerFactory), "Deleted", "GlobalSetting", id);
        AdminOperationsMetricsService.RecordConfigurationChange("globalsetting", "delete");
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteByKey(
        string key,
        [FromServices] IAdminGlobalSettingService service,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        if (!await service.DeleteSettingByKeyAsync(key)) throw new KeyNotFoundException();
        AdminAudit.Log(context, Logger(loggerFactory), "Deleted", "GlobalSetting",
            detail: $"Key: {LoggingSanitizer.S(key)}");
        AdminOperationsMetricsService.RecordConfigurationChange("globalsetting", "delete");
        return Results.NoContent();
    }

    private static async Task<IResult> GetCacheStats([FromServices] IGlobalSettingsCacheService cacheService)
    {
        return Results.Ok(await cacheService.GetCacheStatsAsync());
    }

    private static async Task<IResult> ReloadCache(
        [FromServices] IEventBus eventBus,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var requestId = Guid.NewGuid().ToString();
        await eventBus.PublishAsync(new GlobalSettingsReloadRequested
        {
            RequestedBy = "Admin User",
            CorrelationId = requestId
        });
        AdminAudit.Log(context, Logger(loggerFactory), "Reloaded", "GlobalSettingsCache");
        return Results.Accepted(value: new GlobalSettingsReloadAcceptedResponse(
            "Global settings cache reload was accepted for all Admin and Gateway instances.",
            requestId,
            DateTime.UtcNow));
    }

    private static async Task<IResult> InvalidateCache(
        string key,
        [FromServices] IGlobalSettingsCacheService cacheService,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        await cacheService.InvalidateSettingAsync(key);
        AdminAudit.Log(context, Logger(loggerFactory), "Invalidated", "GlobalSettingsCache",
            detail: $"Key: {LoggingSanitizer.S(key)}");
        return Results.NoContent();
    }

    private static ILogger Logger(ILoggerFactory factory) =>
        factory.CreateLogger("ConduitLLM.Admin.Endpoints.GlobalSettings");
}
