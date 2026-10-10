using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.DTOs.Monitoring;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.AspNetCore.Mvc;
using ConduitLLM.Admin.DTOs;

namespace ConduitLLM.Admin.Endpoints;

public static class SystemInfoEndpoints
{
    public static IEndpointRouteBuilder MapSystemInfoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/system-metadata")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("SystemInfo");
        group.MapGet("/info", GetInfo).WithName("SystemInfo_GetInfo")
            .WithSummary("Get deployment and system information")
            .Produces<SystemInfoDto>(StatusCodes.Status200OK);
        group.MapGet("/health", GetHealth).WithName("SystemInfo_GetHealth")
            .WithSummary("Get system health information")
            .Produces<HealthStatusDto>(StatusCodes.Status200OK);
        group.MapPost("/cache/invalidate-discovery", InvalidateDiscovery)
            .WithName("SystemInfo_InvalidateDiscoveryCache")
            .WithSummary("Invalidate the model discovery cache").Produces<CacheInvalidationPublishedResponse>(StatusCodes.Status200OK);
        group.MapGet("/cache/function-discovery/stats", GetFunctionDiscoveryStats)
            .WithName("SystemInfo_GetFunctionDiscoveryCacheStats")
            .WithSummary("Get function discovery cache statistics")
            .Produces<CacheStats>(StatusCodes.Status200OK)
            .Produces<CacheServiceUnavailableResponse>(StatusCodes.Status404NotFound);
        group.MapPost("/cache/invalidate-function-discovery", InvalidateFunctionDiscovery)
            .WithName("SystemInfo_InvalidateFunctionDiscoveryCache")
            .WithSummary("Invalidate the function discovery cache")
            .Produces<CacheInvalidationPublishedResponse>(StatusCodes.Status200OK);
        return app;
    }

    private static async Task<IResult> GetInfo([FromServices] IAdminSystemInfoService service) =>
        Results.Ok(await service.GetSystemInfoAsync());
    private static async Task<IResult> GetHealth([FromServices] IAdminSystemInfoService service) =>
        Results.Ok(await service.GetHealthStatusAsync());

    private static async Task<IResult> InvalidateDiscovery(
        [FromServices] IEventBus eventBus, HttpContext context, ILoggerFactory loggerFactory)
    {
        await eventBus.PublishAsync(new DiscoveryCacheInvalidationRequested
        {
            Reason = "Manual invalidation via Admin API",
            RequestedBy = "Admin User",
            CorrelationId = Guid.NewGuid().ToString()
        });
        Audit(context, loggerFactory, "DiscoveryCache");
        return Results.Ok(new CacheInvalidationPublishedResponse(
            "Discovery cache invalidation request published successfully",
            DateTime.UtcNow,
            "Cache invalidation is being processed asynchronously across all Gateway API instances"));
    }

    private static async Task<IResult> GetFunctionDiscoveryStats(IServiceProvider services)
    {
        var cache = services.GetService<IFunctionDiscoveryCacheService>();
        return cache is null
            ? AdminResults.NotFound(
                "Function discovery cache service is not configured. The cache service must be registered in the DI container.",
                "cache_service_unavailable")
            : Results.Ok(await cache.GetStatisticsAsync());
    }

    private static async Task<IResult> InvalidateFunctionDiscovery(
        [FromServices] IEventBus eventBus, HttpContext context, ILoggerFactory loggerFactory)
    {
        await eventBus.PublishAsync(new FunctionDiscoveryCacheInvalidationRequested
        {
            Reason = "Manual invalidation via Admin API",
            RequestedBy = "Admin User",
            CorrelationId = Guid.NewGuid().ToString()
        });
        Audit(context, loggerFactory, "FunctionDiscoveryCache");
        return Results.Ok(new CacheInvalidationPublishedResponse(
            "Function discovery cache invalidation request published successfully",
            DateTime.UtcNow,
            "Cache invalidation is being processed asynchronously across all Gateway API instances"));
    }

    private static void Audit(HttpContext context, ILoggerFactory factory, string entity) =>
        AdminAudit.Log(context, factory.CreateLogger("ConduitLLM.Admin.Endpoints.SystemInfo"),
            "Invalidated", entity);
}
