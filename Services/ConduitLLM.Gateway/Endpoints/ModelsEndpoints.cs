using ConduitLLM.Core.OpenApi;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Gateway.Endpoints;

/// <summary>Minimal-API model discovery endpoints.</summary>
public static class ModelsEndpoints
{
    public static IEndpointRouteBuilder MapModelsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1")
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Models");

        group.MapGet("/models", ListModels)
            .WithName("Models_ListModels")
            .WithSummary("List models available to the virtual key")
            .WithDescription("Lists model aliases permitted by the authenticated virtual key. Disabled or unavailable mappings are excluded.")
            .Produces<ModelListResponse>(StatusCodes.Status200OK)
            .Produces<OpenAIErrorResponse>(StatusCodes.Status500InternalServerError);

        group.MapGet("/models/{model}", RetrieveModel)
            .WithName("Models_RetrieveModel")
            .WithDescription("Returns an accessible model alias or 404 if the model is unavailable to this virtual key.")
            .WithParameterDescription("model", "Model alias exposed by Conduit.")
            .WithSummary("Retrieve a model")
            .Produces<ModelListItemDto>(StatusCodes.Status200OK)
            .Produces<OpenAIErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<OpenAIErrorResponse>(StatusCodes.Status500InternalServerError);

        app.MapGet("/v1/conduit/models/{modelId}/metadata", GetModelMetadata)
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Models")
            .WithName("Models_GetModelMetadata")
            .WithSummary("Get metadata for an available model")
            .WithParameterDescription("modelId", "Identifier of the model.")
            .Produces<ModelMetadataResponse>(StatusCodes.Status200OK)
            .Produces<OpenAIErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<OpenAIErrorResponse>(StatusCodes.Status500InternalServerError);

        return app;
    }

    private static async Task<IResult> ListModels(
        [FromServices] IModelProviderMappingRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ConduitLLM.Gateway.Endpoints.Models");
        logger.LogInformation("Getting available models");

        var allMappings = new List<Configuration.Entities.ModelProviderMapping>();
        var pageNumber = 1;
        const int pageSize = 100;
        while (true)
        {
            var (mappings, totalCount) = await repository.GetPaginatedAsync(
                pageNumber, pageSize, cancellationToken);
            allMappings.AddRange(mappings);
            if (allMappings.Count >= totalCount || mappings.Count == 0)
            {
                break;
            }
            pageNumber++;
        }

        var data = allMappings
            .Where(mapping => mapping.IsEnabled)
            .GroupBy(mapping => mapping.ModelAlias, StringComparer.Ordinal)
            .Select(group => ToModel(group.OrderBy(mapping => mapping.CreatedAt).First()))
            .ToList();
        logger.LogDebug("Returning {ModelCount} available models", data.Count);
        return Results.Ok(new ModelListResponse(data, "list"));
    }

    private static async Task<IResult> RetrieveModel(
        string model,
        [FromServices] IModelProviderMappingRepository repository,
        CancellationToken cancellationToken)
    {
        var mapping = await repository.GetByModelNameAsync(model, cancellationToken);
        return mapping is null || !mapping.IsEnabled
            ? GatewayResults.OpenAIError(
                StatusCodes.Status404NotFound,
                $"The model '{model}' does not exist or is not available.",
                "model_not_found",
                "invalid_request_error",
                "model")
            : Results.Ok(ToModel(mapping));
    }

    private static ModelListItemDto ToModel(Configuration.Entities.ModelProviderMapping mapping)
    {
        var owner = mapping.Provider?.ProviderType.ToString().ToLowerInvariant();
        return new ModelListItemDto(
            mapping.ModelAlias,
            "model",
            new DateTimeOffset(mapping.CreatedAt.ToUniversalTime()).ToUnixTimeSeconds(),
            string.IsNullOrWhiteSpace(owner) ? "conduit" : owner);
    }

    private static async Task<IResult> GetModelMetadata(
        string modelId,
        [FromServices] IModelMetadataService metadataService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ConduitLLM.Gateway.Endpoints.Models");
        logger.LogInformation("Getting metadata for model {ModelId}", modelId);
        var metadata = await metadataService.GetModelMetadataAsync(modelId);
        return metadata is null
            ? GatewayResults.OpenAIError(
                StatusCodes.Status404NotFound,
                $"No metadata found for model '{modelId}'",
                "model_not_found")
            : Results.Ok(new ModelMetadataResponse(modelId, metadata));
    }
}
