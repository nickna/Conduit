using ConduitLLM.Configuration.Interfaces;

using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Core.Extensions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ConduitLLM.Admin.Endpoints;

/// <summary>
/// Controller for managing model provider mappings
/// </summary>
public class ModelProviderMappingEndpoints
{
    private readonly IAdminModelProviderMappingService _mappingService;
    private readonly IProviderService _providerService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ModelProviderMappingEndpoints> _logger;

    /// <summary>
    /// Initializes the Model Provider Mapping endpoint handler.
    /// </summary>
    /// <param name="mappingService">The model provider mapping service</param>
    /// <param name="providerService">The provider service</param>
    /// <param name="httpContextAccessor">Accessor for the current request context</param>
    /// <param name="logger">The logger</param>
    public ModelProviderMappingEndpoints(
        IAdminModelProviderMappingService mappingService,
        IProviderService providerService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ModelProviderMappingEndpoints> logger)
    {
        _mappingService = mappingService ?? throw new ArgumentNullException(nameof(mappingService));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public static IEndpointRouteBuilder MapModelProviderMappingEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1/admin/model-provider-mappings").RequireAuthorization("MasterKeyPolicy").AddEndpointFilter<OperationLoggingEndpointFilter>().WithTags("Model Provider Mappings");
        g.MapGet("/", ([FromServices] ModelProviderMappingEndpoints e) => e.GetAllMappings()).WithName("ModelProviderMapping_GetAll").Produces<IEnumerable<ModelProviderMappingDto>>();
        g.MapGet("/{id}", ([FromServices] ModelProviderMappingEndpoints e, int id) => e.GetMappingById(id)).WithName("ModelProviderMapping_GetById").Produces<ModelProviderMappingDto>().Produces(StatusCodes.Status404NotFound);
        g.MapPost("/", ([FromServices] ModelProviderMappingEndpoints e, CreateModelProviderMappingDto dto) => e.CreateMapping(dto)).WithName("ModelProviderMapping_Create").Produces<ModelProviderMappingDto>(StatusCodes.Status201Created).Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status409Conflict);
        g.MapPatch("/{id}", ([FromServices] ModelProviderMappingEndpoints e, int id, JsonMergePatch<UpdateModelProviderMappingDto> patch) => e.UpdateMapping(id, patch.Value)).AcceptsJsonMergePatch<UpdateModelProviderMappingDto>().WithName("ModelProviderMapping_Update").Produces<ModelProviderMappingDto>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
        g.MapDelete("/{id}", ([FromServices] ModelProviderMappingEndpoints e, int id) => e.DeleteMapping(id)).WithName("ModelProviderMapping_Delete").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
        g.MapGet("/providers", ([FromServices] ModelProviderMappingEndpoints e) => e.GetProviders()).WithName("ModelProviderMapping_GetProviders").Produces<IEnumerable<ProviderDto>>();
        g.MapPost("/bulk/preview", ([FromServices] ModelProviderMappingEndpoints e, BulkModelMappingPreviewRequest d) => e.PreviewBulkMappings(d)).WithName("ModelProviderMapping_PreviewBulk").Produces<BulkModelMappingPreviewResponse>().Produces(StatusCodes.Status400BadRequest);
        g.MapPost("/bulk", ([FromServices] ModelProviderMappingEndpoints e, BulkModelMappingCreateRequest d) => e.CreateBulkMappings(d)).WithName("ModelProviderMapping_CreateBulk").Produces<BulkModelMappingCreateResponse>().Produces(StatusCodes.Status400BadRequest);
        g.MapPost("/bulk/delete", ([FromServices] ModelProviderMappingEndpoints e, List<int> ids) => e.DeleteBulkMappings(ids)).WithName("ModelProviderMapping_DeleteBulk").Produces<BulkDeleteResult>().Produces(StatusCodes.Status400BadRequest);
        g.MapPost("/bulk/enable", ([FromServices] ModelProviderMappingEndpoints e, List<int> ids) => e.EnableBulkMappings(ids)).WithName("ModelProviderMapping_EnableBulk").Produces<BulkUpdateResult>().Produces(StatusCodes.Status400BadRequest);
        g.MapPost("/bulk/disable", ([FromServices] ModelProviderMappingEndpoints e, List<int> ids) => e.DisableBulkMappings(ids)).WithName("ModelProviderMapping_DisableBulk").Produces<BulkUpdateResult>().Produces(StatusCodes.Status400BadRequest);
        return app;
    }

    /// <summary>
    /// Gets all model provider mappings
    /// </summary>
    /// <returns>A list of all model provider mappings</returns>
    public async Task<IResult> GetAllMappings()
    {
        var mappings = await _mappingService.GetAllMappingsAsync();
        var result = mappings.Select(m => m.ToDto());
        return Results.Ok(result);
    }

    /// <summary>
    /// Gets a specific model provider mapping by ID
    /// </summary>
    /// <param name="id">The ID of the mapping to retrieve</param>
    /// <returns>The model provider mapping</returns>
    public async Task<IResult> GetMappingById(int id)
    {
        var mapping = await _mappingService.GetMappingByIdAsync(id);
        if (mapping == null) { return AdminResults.NotFoundEntity("Model provider mapping", id); }
        return Results.Ok(mapping.ToDto());
    }

    /// <summary>
    /// Creates a new model provider mapping
    /// </summary>
    /// <param name="mappingDto">The mapping to create</param>
    /// <returns>The created mapping</returns>
    public async Task<IResult> CreateMapping(CreateModelProviderMappingDto mappingDto)
    {
        // An alias may have multiple providers, but never duplicate an alias/provider pair.
        var existingMappings = await _mappingService.GetAllMappingsAsync();
        var existingMapping = existingMappings.FirstOrDefault(m =>
            m.ModelAlias.Equals(mappingDto.ModelAlias, StringComparison.OrdinalIgnoreCase) &&
            m.ProviderId == mappingDto.ProviderId);
        if (existingMapping != null)
        {
            return AdminResults.Conflict($"A mapping for alias '{mappingDto.ModelAlias}' and provider {mappingDto.ProviderId} already exists");
        }

        var optionsError = ValidateProviderOptions(mappingDto.ProviderOptions);
        if (optionsError != null)
        {
            return AdminResults.BadRequest(optionsError);
        }

        var mapping = mappingDto.ToEntity();
        var success = await _mappingService.AddMappingAsync(mapping);

        if (!success)
        {
            return AdminResults.BadRequest("Failed to create model provider mapping. Please check the provider ID.");
        }

        var createdMapping = await _mappingService.GetMappingByIdAsync(mapping.Id);

        LogAdminAudit("Created", "ModelProviderMapping", createdMapping?.Id,
            $"ModelAlias: {LoggingSanitizer.S(mappingDto.ModelAlias)}, ProviderId: {mappingDto.ProviderId}");
        AdminOperationsMetricsService.RecordModelMappingOperation("create", "success");
        AdminOperationsMetricsService.RecordConfigurationChange("modelmapping", "create");

        return Results.Created($"/v1/admin/model-provider-mappings/{createdMapping?.Id}", createdMapping?.ToDto());
    }

    /// <summary>
    /// Updates an existing model provider mapping
    /// </summary>
    /// <param name="id">The ID of the mapping to update</param>
    /// <param name="mappingDto">The updated mapping data</param>
    /// <returns>No content on success</returns>
    public async Task<IResult> UpdateMapping(int id, UpdateModelProviderMappingDto mappingDto)
    {
        var existingMapping = await _mappingService.GetMappingByIdAsync(id);
        if (existingMapping == null)
        {
            throw new KeyNotFoundException($"Model provider mapping with ID '{id}' not found");
        }

        JsonMergePatchState.TryGetPatchedProperty(
            mappingDto,
            nameof(mappingDto.ModelAlias),
            existingMapping.ModelAlias,
            out var effectiveAlias);
        JsonMergePatchState.TryGetPatchedProperty(
            mappingDto,
            nameof(mappingDto.ProviderId),
            existingMapping.ProviderId,
            out var effectiveProviderId);
        var mappings = await _mappingService.GetAllMappingsAsync();
        if (mappings.Any(mapping =>
            mapping.Id != id &&
            mapping.ModelAlias.Equals(effectiveAlias, StringComparison.OrdinalIgnoreCase) &&
            mapping.ProviderId == effectiveProviderId))
        {
            return AdminResults.Conflict(
                $"A mapping for alias '{effectiveAlias}' and provider {effectiveProviderId} already exists");
        }

        var optionsError = ValidateProviderOptions(
            ModelProviderMappingMergePatch.GetEffectiveProviderOptions(mappingDto, existingMapping));
        if (optionsError != null)
        {
            return AdminResults.BadRequest(optionsError);
        }

        ModelProviderMappingMergePatch.Apply(mappingDto, existingMapping);
        var success = await _mappingService.UpdateMappingAsync(existingMapping);

        if (!success)
        {
            throw new InvalidOperationException("Failed to update model provider mapping");
        }

        LogAdminAudit("Updated", "ModelProviderMapping", id);
        AdminOperationsMetricsService.RecordModelMappingOperation("update", "success");
        AdminOperationsMetricsService.RecordConfigurationChange("modelmapping", "update");

        return Results.Ok((await _mappingService.GetMappingByIdAsync(id) ?? throw new KeyNotFoundException()).ToDto());
    }

    private static readonly string[] ForbiddenProviderOptionKeys = { "model", "messages", "stream", "stream_options" };

    /// <summary>
    /// Validates a mapping's ProviderOptions JSON. Returns an error message if invalid, else null.
    /// The value must be a JSON object and may not contain keys that would hijack the request
    /// (model/messages/stream/stream_options).
    /// </summary>
    private static string? ValidateProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null)
        {
            return null;
        }

        foreach (var key in providerOptions.Keys)
        {
            if (ForbiddenProviderOptionKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return $"ProviderOptions may not contain the reserved key '{key}'.";
            }
        }

        return null;
    }

    /// <summary>
    /// Deletes a model provider mapping
    /// </summary>
    /// <param name="id">The ID of the mapping to delete</param>
    /// <returns>No content on success</returns>
    public async Task<IResult> DeleteMapping(int id)
    {
        var existingMapping = await _mappingService.GetMappingByIdAsync(id);
        if (existingMapping == null)
        {
            throw new KeyNotFoundException($"Model provider mapping with ID '{id}' not found");
        }

        var success = await _mappingService.DeleteMappingAsync(id);

        if (!success)
        {
            throw new InvalidOperationException("Failed to delete model provider mapping");
        }

        LogAdminAudit("Deleted", "ModelProviderMapping", id);
        AdminOperationsMetricsService.RecordModelMappingOperation("delete", "success");
        AdminOperationsMetricsService.RecordConfigurationChange("modelmapping", "delete");

        return Results.NoContent();
    }

    /// <summary>
    /// Gets all available providers
    /// </summary>
    /// <returns>List of providers with IDs and names</returns>
    public async Task<IResult> GetProviders()
    {
        var result = await _mappingService.GetProvidersAsync();
        return Results.Ok(result.Select(provider => new ProviderDto
        {
            Id = provider.Id,
            ProviderType = provider.ProviderType,
            ProviderName = provider.ProviderName,
            BaseUrl = provider.BaseUrl,
            KeyCount = provider.ProviderKeyCredentials?.Count ?? 0,
            TrustProviderReportedCosts = provider.TrustProviderReportedCosts,
            ProviderCostMarkupMultiplier = provider.ProviderCostMarkupMultiplier,
            IsEnabled = provider.IsEnabled,
            CreatedAt = provider.CreatedAt,
            UpdatedAt = provider.UpdatedAt
        }).ToList());
    }

    /// <summary>
    /// Resolves model associations and conflicts before bulk creation.
    /// </summary>
    public async Task<IResult> PreviewBulkMappings(BulkModelMappingPreviewRequest request)
    {
        if (request?.Mappings == null || request.Mappings.Count == 0)
        {
            return AdminResults.BadRequest("No mappings provided");
        }

        return Results.Ok(await _mappingService.PreviewBulkMappingsAsync(request));
    }

    /// <summary>
    /// Resolves and creates multiple model provider mappings using partial-success semantics.
    /// Equivalent existing mappings satisfy retries without creating duplicates.
    /// </summary>
    public async Task<IResult> CreateBulkMappings(BulkModelMappingCreateRequest request)
    {
        if (request?.Mappings == null || request.Mappings.Count == 0)
        {
            return AdminResults.BadRequest("No mappings provided");
        }

        var result = await _mappingService.CreateBulkMappingsAsync(request);

        LogAdminAuditBulk("BulkCreated", "ModelProviderMapping", result.SuccessCount, result.FailureCount);
        AdminOperationsMetricsService.RecordModelMappingOperation(
            "bulk_create",
            result.IsSuccess ? "success" : result.IsPartialSuccess ? "partial" : "failed");

        return Results.Ok(result);
    }

    /// <summary>
    /// Deletes multiple model provider mappings in a single operation
    /// </summary>
    /// <param name="ids">The IDs of the mappings to delete</param>
    /// <returns>The bulk delete response with results</returns>
    public async Task<IResult> DeleteBulkMappings(List<int> ids)
    {
        if (ids == null || ids.Count == 0)
        {
            return AdminResults.BadRequest("No mapping IDs provided");
        }

        var deleted = new List<int>();
        var errors = new List<string>();

        foreach (var id in ids)
        {
            try
            {
                var existingMapping = await _mappingService.GetMappingByIdAsync(id);
                if (existingMapping == null)
                {
                    errors.Add($"Mapping with ID {id} not found");
                    continue;
                }

                var success = await _mappingService.DeleteMappingAsync(id);
                if (success)
                {
                    deleted.Add(id);
                }
                else
                {
                    errors.Add($"Failed to delete mapping with ID {id}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting mapping with ID {Id}", id);
                errors.Add($"Error deleting mapping with ID {id}: {ex.Message}");
            }
        }

        var result = new BulkDeleteResult
        {
            DeletedIds = deleted,
            Errors = errors,
            TotalProcessed = ids.Count,
            SuccessCount = deleted.Count,
            FailureCount = errors.Count
        };

        LogAdminAuditBulk("BulkDeleted", "ModelProviderMapping", result.SuccessCount, result.FailureCount);
        AdminOperationsMetricsService.RecordModelMappingOperation("bulk_delete", "success");

        return Results.Ok(result);
    }

    /// <summary>
    /// Enables multiple model provider mappings in a single operation
    /// </summary>
    /// <param name="ids">The IDs of the mappings to enable</param>
    /// <returns>The bulk update response with results</returns>
    public async Task<IResult> EnableBulkMappings(List<int> ids)
    {
        return await UpdateBulkMappingsStatus(ids, true);
    }

    /// <summary>
    /// Disables multiple model provider mappings in a single operation
    /// </summary>
    /// <param name="ids">The IDs of the mappings to disable</param>
    /// <returns>The bulk update response with results</returns>
    public async Task<IResult> DisableBulkMappings(List<int> ids)
    {
        return await UpdateBulkMappingsStatus(ids, false);
    }

    private async Task<IResult> UpdateBulkMappingsStatus(List<int> ids, bool isEnabled)
    {
        if (ids == null || ids.Count == 0)
        {
            return AdminResults.BadRequest("No mapping IDs provided");
        }

        var updated = new List<ModelProviderMappingDto>();
        var errors = new List<string>();

        foreach (var id in ids)
        {
            try
            {
                var existingMapping = await _mappingService.GetMappingByIdAsync(id);
                if (existingMapping == null)
                {
                    errors.Add($"Mapping with ID {id} not found");
                    continue;
                }

                existingMapping.IsEnabled = isEnabled;
                var success = await _mappingService.UpdateMappingAsync(existingMapping);

                if (success)
                {
                    updated.Add(existingMapping.ToDto());
                }
                else
                {
                    errors.Add($"Failed to update mapping with ID {id}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating mapping with ID {Id}", id);
                errors.Add($"Error updating mapping with ID {id}: {ex.Message}");
            }
        }

        var result = new BulkUpdateResult
        {
            Updated = updated,
            Errors = errors,
            TotalProcessed = ids.Count,
            SuccessCount = updated.Count,
            FailureCount = errors.Count
        };

        LogAdminAuditBulk(isEnabled ? "BulkEnabled" : "BulkDisabled", "ModelProviderMapping", result.SuccessCount, result.FailureCount);
        AdminOperationsMetricsService.RecordModelMappingOperation(isEnabled ? "bulk_enable" : "bulk_disable", "success");

        return Results.Ok(result);
    }

    private void LogAdminAudit(string operation, string entityType, object? entityId = null, string? detail = null) => AdminAudit.Log(_httpContextAccessor.HttpContext!, _logger, operation, entityType, entityId, detail);
    private void LogAdminAuditBulk(string operation, string entityType, int successCount, int failureCount) => AdminAudit.LogBulk(_httpContextAccessor.HttpContext!, _logger, operation, entityType, successCount, failureCount);

}

/// <summary>
/// Result of a bulk delete operation
/// </summary>
public class BulkDeleteResult
{
    /// <summary>
    /// IDs of successfully deleted mappings
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public List<int> DeletedIds { get; set; } = new();

    /// <summary>
    /// Error messages for failed deletions
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Total number of mappings processed
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public int TotalProcessed { get; set; }

    /// <summary>
    /// Number of successful deletions
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed deletions
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public int FailureCount { get; set; }
}

/// <summary>
/// Result of a bulk update operation
/// </summary>
public class BulkUpdateResult
{
    /// <summary>
    /// Successfully updated mappings
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public List<ModelProviderMappingDto> Updated { get; set; } = new();

    /// <summary>
    /// Error messages for failed updates
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Total number of mappings processed
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public int TotalProcessed { get; set; }

    /// <summary>
    /// Number of successful updates
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed updates
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    public int FailureCount { get; set; }
}
