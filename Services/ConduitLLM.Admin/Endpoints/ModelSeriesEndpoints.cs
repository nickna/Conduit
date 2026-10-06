using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Models.ModelSeries;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Events;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Functions.Utilities;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ConduitLLM.Admin.Endpoints;

public static class ModelSeriesEndpoints
{
    public static IEndpointRouteBuilder MapModelSeriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/model-series")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("ModelSeries");

        group.MapGet("/", GetAll).WithName("ModelSeries_GetAll")
            .Produces<IEnumerable<ModelSeriesDto>>(StatusCodes.Status200OK);
        group.MapGet("/{id}", GetById).WithName("ModelSeries_GetById")
            .Produces<ModelSeriesDto>(StatusCodes.Status200OK)
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapGet("/{id}/models", GetModelsInSeries).WithName("ModelSeries_GetModels")
            .Produces<IEnumerable<SeriesSimpleModelDto>>(StatusCodes.Status200OK)
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPost("/", Create).WithName("ModelSeries_Create")
            .Produces<ModelSeriesDto>(StatusCodes.Status201Created)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        group.MapPatch("/{id}", Update).AcceptsJsonMergePatch<UpdateModelSeriesDto>().WithName("ModelSeries_Update")
            .Produces<ModelSeriesDto>(StatusCodes.Status200OK)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        group.MapDelete("/{id}", Delete).WithName("ModelSeries_Delete")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        return app;
    }

    private static async Task<IResult> GetAll([FromServices] IModelSeriesRepository repository) =>
        Results.Ok((await repository.GetAllWithAuthorAsync()).Select(series => series.ToDto()));

    private static async Task<IResult> GetById(int id, [FromServices] IModelSeriesRepository repository)
    {
        var series = await repository.GetByIdWithAuthorAsync(id);
        return series is null ? AdminResults.NotFoundEntity("Model series", id) : Results.Ok(series.ToDto());
    }

    private static async Task<IResult> GetModelsInSeries(int id, [FromServices] IModelSeriesRepository repository)
    {
        var models = await repository.GetModelsInSeriesAsync(id);
        if (models is null)
        {
            return AdminResults.NotFoundEntity("Model series", id);
        }
        return Results.Ok(models.Select(model => new SeriesSimpleModelDto
        {
            Id = model.Id,
            Name = model.Name,
            Version = model.Version,
            IsActive = model.IsActive
        }));
    }

    private static async Task<IResult> Create(
        CreateModelSeriesDto dto,
        [FromServices] IModelSeriesRepository repository,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        if (await repository.GetByNameAndAuthorAsync(dto.Name, dto.AuthorId) is not null)
        {
            throw new InvalidOperationException(
                $"A model series with name '{dto.Name}' already exists for this author");
        }
        var series = new ModelSeries
        {
            AuthorId = dto.AuthorId,
            Name = dto.Name,
            Description = dto.Description,
            TokenizerType = dto.TokenizerType,
            Parameters = dto.Parameters is null ? "{}" : AdminJson.Serialize(dto.Parameters)
        };
        await repository.CreateAsync(series);
        await ExpireRoutingAsync(context);
        var reloaded = await repository.GetByIdWithAuthorAsync(series.Id)
            ?? throw new InvalidOperationException("Failed to reload created series");
        AdminAudit.Log(context, Logger(loggerFactory), "Created", "ModelSeries", reloaded.Id,
            $"Name: {LoggingSanitizer.S(reloaded.Name)}");
        return Results.Created($"/v1/admin/model-series/{reloaded.Id}", reloaded.ToDto());
    }

    private static async Task<IResult> Update(
        int id,
        JsonMergePatch<UpdateModelSeriesDto> patch,
        [FromServices] IModelSeriesRepository repository,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var dto = patch.Value;
        var series = await repository.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Model series with ID {id} not found");
        if (JsonMergePatchState.TryGetPatchedProperty(dto, nameof(dto.Name), series.Name, out var name)
            && name != series.Name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("name cannot be null or empty.");
            }
            var existing = await repository.GetByNameAndAuthorAsync(name, series.AuthorId);
            if (existing is not null && existing.Id != id)
            {
                throw new InvalidOperationException(
                    $"A model series with name '{name}' already exists for this author");
            }
            series.Name = name;
        }
        if (JsonMergePatchState.TryGetPatchedProperty(
                dto,
                nameof(dto.Description),
                series.Description,
                out string? description))
        {
            series.Description = description;
        }
        if (JsonMergePatchState.TryGetPatchedProperty(
                dto,
                nameof(dto.TokenizerType),
                series.TokenizerType,
                out var tokenizerType))
        {
            series.TokenizerType = tokenizerType;
        }
        if (JsonMergePatchState.TryGetPatchedProperty(
                dto,
                nameof(dto.Parameters),
                StructuredJson.ParseObject(series.Parameters),
                out Dictionary<string, JsonElement>? parameters))
        {
            series.Parameters = AdminJson.Serialize(parameters ?? []);
        }
        await repository.UpdateAsync(series);
        await ExpireRoutingAsync(context);
        AdminAudit.Log(context, Logger(loggerFactory), "Updated", "ModelSeries", id,
            $"Name: {LoggingSanitizer.S(series.Name)}");
        return Results.Ok(series.ToDto());
    }

    private static async Task<IResult> Delete(
        int id,
        [FromServices] IModelSeriesRepository repository,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var series = await repository.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Model series with ID {id} not found");
        var models = await repository.GetModelsInSeriesAsync(id);
        if (models is not null && models.Any())
        {
            throw new InvalidOperationException(
                $"Cannot delete model series with {models.Count()} associated models. Delete the models first.");
        }
        await repository.DeleteAsync(id);
        await ExpireRoutingAsync(context);
        AdminAudit.Log(context, Logger(loggerFactory), "Deleted", "ModelSeries", id,
            $"Name: {LoggingSanitizer.S(series.Name)}");
        return Results.NoContent();
    }

    private static ILogger Logger(ILoggerFactory factory) =>
        factory.CreateLogger("ConduitLLM.Admin.Endpoints.ModelSeries");

    private static Task ExpireRoutingAsync(HttpContext context) =>
        context.RequestServices.GetRequiredService<IEventBus>().PublishAsync(new DiscoveryCacheInvalidationRequested
        { Reason = "Model series defaults changed", RequestedBy = "model-series" }, context.RequestAborted);
}
