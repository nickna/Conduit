using ConduitLLM.Core.OpenApi;
using System.Text.Json;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Functions.DTOs;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Utilities;

using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

public static class FunctionCostsEndpoints
{
    public static IEndpointRouteBuilder MapFunctionCostsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/function-costs")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("FunctionCosts");

        group.MapGet("/", List).WithName("FunctionCosts_List")
            .WithSummary("List function pricing records").Produces<List<FunctionCostDto>>();
        group.MapGet("/{id:int}", GetById).WithName("FunctionCosts_GetById")
            .WithSummary("Get a function pricing record")
            .WithParameterDescription("id", "Identifier of the FunctionCosts resource.")
            .Produces<FunctionCostDto>().Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapGet("/configuration/{functionConfigurationId:int}", GetByConfiguration)
            .WithName("FunctionCosts_GetByConfiguration")
            .WithSummary("List prices for a function configuration")
            .WithParameterDescription("functionConfigurationId", "Identifier of the function configuration.")
            .Produces<FunctionCostDto>().Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPost("/", Create).WithName("FunctionCosts_Create")
            .WithSummary("Create a function pricing record")
            .Produces<FunctionCostDto>(StatusCodes.Status201Created)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        group.MapPatch("/{id:int}", Update).AcceptsJsonMergePatch<UpdateFunctionCostDto>().WithName("FunctionCosts_Update")
            .WithSummary("Update a function pricing record")
            .WithParameterDescription("id", "Identifier of the FunctionCosts resource.")
            .Produces<FunctionCostDto>().Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapDelete("/{id:int}", Delete).WithName("FunctionCosts_Delete")
            .WithSummary("Delete a function pricing record")
            .WithParameterDescription("id", "Identifier of the FunctionCosts resource.")
            .Produces(StatusCodes.Status204NoContent).Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPost("/cache/clear", ClearCache).WithName("FunctionCosts_ClearCache")
            .WithSummary("Clear cached function prices")
            .Produces<FunctionCostCacheClearResultDto>();
        return app;
    }

    private static async Task<IResult> List([FromServices] IFunctionCostService service) =>
        Results.Ok((await service.ListCostsAsync()).Select(cost => cost.ToDto()).ToList());

    private static async Task<IResult> GetById(int id, [FromServices] IFunctionCostService service)
    {
        var dto = (await service.GetCostByIdAsync(id))?.ToDto();
        return dto is null ? AdminResults.NotFoundEntity("Function cost", id) : Results.Ok(dto);
    }

    private static async Task<IResult> GetByConfiguration(
        int functionConfigurationId,
        [FromServices] IFunctionCostService service)
    {
        var dto = (await service.GetCostForConfigurationAsync(functionConfigurationId))?.ToDto();
        return dto is null
            ? AdminResults.NotFoundEntity("Function cost for configuration", functionConfigurationId)
            : Results.Ok(dto);
    }

    private static async Task<IResult> Create(
        [FromBody] CreateFunctionCostDto createDto,
        [FromServices] IFunctionCostService service,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        var id = await service.CreateCostAsync(MapToEntity(createDto));
        var dto = (await service.GetCostByIdAsync(id))?.ToDto();
        Audit(httpContext, loggerFactory, "Created", id, createDto.CostName);
        return Results.Created($"/v1/admin/function-costs/{id}", dto);
    }

    private static async Task<IResult> Update(
        int id,
        JsonMergePatch<UpdateFunctionCostDto> patch,
        [FromServices] IFunctionCostService service,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        var updateDto = patch.Value;
        var existing = await service.GetCostByIdAsync(id) ?? throw new KeyNotFoundException();
        await service.UpdateCostAsync(MapToEntity(updateDto, existing));
        var updated = await service.GetCostByIdAsync(id);
        Audit(httpContext, loggerFactory, "Updated", id, updateDto.CostName ?? existing.CostName);
        return Results.Ok(updated?.ToDto());
    }

    private static async Task<IResult> Delete(
        int id,
        [FromServices] IFunctionCostService service,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        var existing = await service.GetCostByIdAsync(id);
        await service.DeleteCostAsync(id);
        Audit(httpContext, loggerFactory, "Deleted", id, existing?.CostName);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearCache(
        [FromServices] IFunctionCostService service,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        await service.ClearCacheAsync();
        AdminAudit.Log(httpContext, Logger(loggerFactory), "Cleared", "FunctionCostCache");
        return Results.Ok(new FunctionCostCacheClearResultDto
        {
            Message = "Function cost cache cleared successfully"
        });
    }

    private static void Audit(
        HttpContext context,
        ILoggerFactory loggerFactory,
        string operation,
        int id,
        string? costName) =>
        AdminAudit.Log(context, Logger(loggerFactory), operation, "FunctionCost", id,
            costName is null ? null : $"CostName: {LoggingSanitizer.S(costName)}");

    private static ILogger Logger(ILoggerFactory factory) =>
        factory.CreateLogger("ConduitLLM.Admin.Endpoints.FunctionCosts");

    private static FunctionCost MapToEntity(CreateFunctionCostDto dto) => new()
    {
        CostName = dto.CostName,
        ProviderType = dto.ProviderType,
        Purpose = dto.Purpose,
        Description = dto.Description,
        BaseCost = dto.BaseCost,
        PricingModel = dto.PricingModel,
        PricingConfiguration = StructuredJson.SerializeObject(dto.PricingConfiguration),
        IsActive = dto.IsActive,
        Priority = dto.Priority,
        EffectiveDate = dto.EffectiveDate,
        ExpiryDate = dto.ExpiryDate,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static FunctionCost MapToEntity(UpdateFunctionCostDto dto, FunctionCost existing)
    {
        if (dto.TryGetPatchedProperty(nameof(dto.CostName), existing.CostName, out string? costName))
            existing.CostName = costName ?? throw new InvalidOperationException("costName cannot be null.");
        if (dto.TryGetPatchedProperty(nameof(dto.Purpose), existing.Purpose, out FunctionPurpose? purpose))
            existing.Purpose = purpose;
        if (dto.TryGetPatchedProperty(nameof(dto.Description), existing.Description, out string? description))
            existing.Description = description;
        if (dto.TryGetPatchedProperty(nameof(dto.BaseCost), existing.BaseCost, out decimal? baseCost))
            existing.BaseCost = baseCost;
        if (dto.TryGetPatchedProperty(nameof(dto.PricingModel), existing.PricingModel, out FunctionPricingModel pricingModel))
            existing.PricingModel = pricingModel;
        if (dto.TryGetPatchedProperty(
                nameof(dto.PricingConfiguration),
                StructuredJson.ParseObject(existing.PricingConfiguration),
                out Dictionary<string, JsonElement>? pricingConfiguration))
        {
            existing.PricingConfiguration = StructuredJson.SerializeObject(pricingConfiguration);
        }
        if (dto.TryGetPatchedProperty(nameof(dto.IsActive), existing.IsActive, out bool isActive))
            existing.IsActive = isActive;
        if (dto.TryGetPatchedProperty(nameof(dto.Priority), existing.Priority, out int priority))
            existing.Priority = priority;
        if (dto.TryGetPatchedProperty(nameof(dto.EffectiveDate), existing.EffectiveDate, out DateTime effectiveDate))
            existing.EffectiveDate = effectiveDate;
        if (dto.TryGetPatchedProperty(nameof(dto.ExpiryDate), existing.ExpiryDate, out DateTime? expiryDate))
            existing.ExpiryDate = expiryDate;
        existing.UpdatedAt = DateTime.UtcNow;
        return existing;
    }
}
