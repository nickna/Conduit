using System.Text.Json;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Admin.Filters;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.DTOs.VirtualKey;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

/// <summary>
/// Controller for managing virtual keys
/// </summary>
public partial class VirtualKeysEndpoints : AdminEndpointHandlerBase
{
    private readonly IAdminVirtualKeyService _virtualKeyService;

    /// <summary>
    /// Initializes the Virtual Keys endpoint handler.
    /// </summary>
    /// <param name="virtualKeyService">The virtual key service</param>
    /// <param name="httpContextAccessor">Accessor for the current request context</param>
    /// <param name="logger">The logger</param>
    public VirtualKeysEndpoints(
        IAdminVirtualKeyService virtualKeyService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<VirtualKeysEndpoints> logger)
        : base(null, httpContextAccessor, logger)
    {
        _virtualKeyService = virtualKeyService ?? throw new ArgumentNullException(nameof(virtualKeyService));
    }

    public static IEndpointRouteBuilder MapVirtualKeysEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/virtual-keys")
            .AddEndpointFilter<VersionedResourceEndpointFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Virtual Keys");

        group.MapPost("/", ([FromServices] VirtualKeysEndpoints endpoints, CreateVirtualKeyRequestDto request) => endpoints.GenerateKey(request))
            .WithName("VirtualKeys_Generate").Produces<CreateVirtualKeyResponseDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization("MasterKeyPolicy");
        group.MapGet("/", ([FromServices] VirtualKeysEndpoints endpoints, int? virtualKeyGroupId = null) => endpoints.ListKeys(virtualKeyGroupId))
            .WithName("VirtualKeys_GetAll").Produces<List<VirtualKeyDto>>().RequireAuthorization("MasterKeyPolicy");
        group.MapGet("/{id}", ([FromServices] VirtualKeysEndpoints endpoints, int id) => endpoints.GetKeyById(id))
            .WithName("VirtualKeys_GetById").Produces<VirtualKeyDto>().Produces(StatusCodes.Status404NotFound).RequireAuthorization("MasterKeyPolicy");
        group.MapPatch("/{id}", ([FromServices] VirtualKeysEndpoints endpoints, int id, JsonMergePatch<UpdateVirtualKeyRequestDto> patch) => endpoints.UpdateKey(id, patch.Value))
            .AcceptsJsonMergePatch<UpdateVirtualKeyRequestDto>()
            .WithName("VirtualKeys_Update").Produces<VirtualKeyDto>().Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization("MasterKeyPolicy");
        group.MapDelete("/{id}", ([FromServices] VirtualKeysEndpoints endpoints, int id, CancellationToken cancellationToken) => endpoints.DeleteKey(id, cancellationToken))
            .WithName("VirtualKeys_Delete").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound).RequireAuthorization("MasterKeyPolicy");
        group.MapPost("/validate", ([FromServices] VirtualKeysEndpoints endpoints, ValidateVirtualKeyRequest request) => endpoints.ValidateKey(request))
            .WithName("VirtualKeys_Validate").Produces<VirtualKeyValidationResult>().Produces(StatusCodes.Status400BadRequest).AllowAnonymous();
        group.MapGet("/{id}/validation-info", ([FromServices] VirtualKeysEndpoints endpoints, int id) => endpoints.GetValidationInfo(id))
            .WithName("VirtualKeys_GetValidationInfo").Produces<VirtualKeyValidationInfoDto>().Produces(StatusCodes.Status404NotFound).RequireAuthorization("MasterKeyPolicy");
        group.MapPost("/maintenance", ([FromServices] VirtualKeysEndpoints endpoints) => endpoints.PerformMaintenance())
            .WithName("VirtualKeys_Maintenance").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden).RequireAuthorization("MasterKeyPolicy");
        group.MapGet("/{id}/discovery-preview", ([FromServices] VirtualKeysEndpoints endpoints, int id, string? capability = null) => endpoints.PreviewDiscovery(id, capability))
            .WithName("VirtualKeys_PreviewDiscovery").Produces<DiscoveryModelsResponse>().Produces(StatusCodes.Status404NotFound).RequireAuthorization("MasterKeyPolicy");
        group.MapGet("/{id}/group", ([FromServices] VirtualKeysEndpoints endpoints, int id) => endpoints.GetKeyGroup(id))
            .WithName("VirtualKeys_GetGroup").Produces<VirtualKeyGroupDto>().Produces(StatusCodes.Status404NotFound).RequireAuthorization("MasterKeyPolicy");
        group.MapGet("/{id}/rate-limit-usage", (
                [FromServices] VirtualKeysEndpoints endpoints,
                int id,
                [FromServices] ConduitLLM.Configuration.Interfaces.IVirtualKeyRepository keyRepository,
                [FromServices] ConduitLLM.Configuration.Interfaces.IVirtualKeyGroupRepository groupRepository,
                [FromServices] ConduitLLM.Core.Services.IVirtualKeyRateLimitService? rateLimitService) =>
                endpoints.GetRateLimitUsage(id, keyRepository, groupRepository, rateLimitService))
            .WithName("VirtualKeys_GetRateLimitUsage")
            .WithSummary("Get a virtual key's current rate limit usage")
            .Produces<VirtualKeyRateLimitUsageDto>()
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status429TooManyRequests)
            .RequireAuthorization("MasterKeyPolicy");
        group.MapGet("/usage/by-key/{key}", ([FromServices] VirtualKeysEndpoints endpoints, string key) => endpoints.GetUsageByKey(key))
            .WithName("VirtualKeys_GetUsageByKey").Produces<VirtualKeyUsageDto>().Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization("MasterKeyPolicy");
        return app;
    }

    /// <summary>
    /// Generates a new virtual API key
    /// </summary>
    /// <param name="request">Details for the key to be created</param>
    /// <returns>The generated key details or an error response</returns>
    public async Task<IResult> GenerateKey(CreateVirtualKeyRequestDto request)
    {
        var response = await _virtualKeyService.GenerateVirtualKeyAsync(request);
        LogAdminAudit("Created", "VirtualKey", response.KeyInfo.Id, $"Name: {LoggingSanitizer.S(request.KeyName)}");
        AdminOperationsMetricsService.RecordVirtualKeyOperation("create", "success");
        AdminOperationsMetricsService.RecordConfigurationChange("virtualkey", "create");
        return Results.Created($"/v1/admin/virtual-keys/{response.KeyInfo.Id}", response);
    }

    /// <summary>
    /// Retrieves a list of all virtual keys
    /// </summary>
    /// <param name="virtualKeyGroupId">Optional filter by virtual key group ID</param>
    /// <returns>List of all virtual keys</returns>
    public async Task<IResult> ListKeys(int? virtualKeyGroupId = null)
    {
        var result = await _virtualKeyService.ListVirtualKeysAsync(virtualKeyGroupId);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves details for a specific virtual key by ID
    /// </summary>
    /// <param name="id">The ID of the key to retrieve</param>
    /// <returns>The virtual key details</returns>
    public async Task<IResult> GetKeyById(int id)
    {
        var result = await _virtualKeyService.GetVirtualKeyInfoAsync(id);
        if (result == null)
        {
            return AdminResults.NotFoundEntity("Virtual key", id);
        }
        return Ok(result);
    }

    /// <summary>
    /// Updates an existing virtual key
    /// </summary>
    /// <param name="id">The ID of the key to update</param>
    /// <param name="request">The updated key details</param>
    /// <returns>No content if successful</returns>
    public async Task<IResult> UpdateKey(int id, UpdateVirtualKeyRequestDto request)
    {
        // Fetch pre-state for change tracking
        var preState = await _virtualKeyService.GetVirtualKeyInfoAsync(id);
        if (preState == null)
            throw new KeyNotFoundException();

        if (request.TryGetPatchedProperty(
                nameof(request.Metadata),
                preState.Metadata,
                out Dictionary<string, JsonElement>? metadata))
            request.Metadata = metadata;
        if (request.TryGetPatchedProperty(
                nameof(request.ModelRateLimits),
                preState.ModelRateLimits,
                out Dictionary<string, ModelRateLimitDto>? modelRateLimits))
            request.ModelRateLimits = modelRateLimits;

        if (!await _virtualKeyService.UpdateVirtualKeyAsync(id, request))
            throw new KeyNotFoundException();

        // Build change list from pre-state vs request
        var changes = new List<(string Property, string? OldValue, string? NewValue)>();

        if (request.HasKeyName && preState.KeyName != request.KeyName)
            changes.Add(("KeyName", preState.KeyName, request.KeyName));
        if (request.HasIsEnabled && preState.IsEnabled != request.IsEnabled)
            changes.Add(("IsEnabled", preState.IsEnabled.ToString(), request.IsEnabled?.ToString()));
        if (request.HasAllowedModels &&
            !(preState.AllowedModels ?? []).SequenceEqual(request.AllowedModels ?? []))
        {
            changes.Add((
                "AllowedModels",
                preState.AllowedModels is null ? "null" : string.Join(',', preState.AllowedModels),
                request.AllowedModels is null ? "null" : string.Join(',', request.AllowedModels)));
        }
        if (request.HasExpiresAt && preState.ExpiresAt != request.ExpiresAt)
            changes.Add(("ExpiresAt", preState.ExpiresAt?.ToString("o") ?? "null", request.ExpiresAt?.ToString("o") ?? "null"));
        if (request.HasRateLimitRpm && preState.RateLimitRpm != request.RateLimitRpm)
            changes.Add(("RateLimitRpm", preState.RateLimitRpm?.ToString() ?? "null", request.RateLimitRpm?.ToString() ?? "null"));
        if (request.HasRateLimitRpd && preState.RateLimitRpd != request.RateLimitRpd)
            changes.Add(("RateLimitRpd", preState.RateLimitRpd?.ToString() ?? "null", request.RateLimitRpd?.ToString() ?? "null"));
        if (request.HasRateLimitTpm && preState.RateLimitTpm != request.RateLimitTpm)
            changes.Add(("RateLimitTpm", preState.RateLimitTpm?.ToString() ?? "null", request.RateLimitTpm?.ToString() ?? "null"));
        if (request.HasMaxParallelRequests && preState.MaxParallelRequests != request.MaxParallelRequests)
            changes.Add(("MaxParallelRequests", preState.MaxParallelRequests?.ToString() ?? "null", request.MaxParallelRequests?.ToString() ?? "null"));
        if (request.HasRateLimitPriority && preState.RateLimitPriority != request.RateLimitPriority)
            changes.Add(("RateLimitPriority", preState.RateLimitPriority?.ToString() ?? "null", request.RateLimitPriority?.ToString() ?? "null"));
        if (request.HasModelRateLimits &&
            DescribeModelLimits(preState.ModelRateLimits) != DescribeModelLimits(request.ModelRateLimits))
        {
            changes.Add(("ModelRateLimits",
                DescribeModelLimits(preState.ModelRateLimits),
                DescribeModelLimits(request.ModelRateLimits)));
        }
        if (request.HasVirtualKeyGroupId && preState.VirtualKeyGroupId != request.VirtualKeyGroupId)
            changes.Add(("VirtualKeyGroupId", preState.VirtualKeyGroupId.ToString(), request.VirtualKeyGroupId?.ToString()));

        if (changes.Count > 0)
        {
            LogAdminAuditWithChanges("VirtualKey", id, changes);
        }
        else
        {
            LogAdminAudit("Updated", "VirtualKey", id, "No changes detected");
        }
        AdminOperationsMetricsService.RecordVirtualKeyOperation("update", "success");
        AdminOperationsMetricsService.RecordConfigurationChange("virtualkey", "update");
        return Ok(await _virtualKeyService.GetVirtualKeyInfoAsync(id) ?? throw new KeyNotFoundException());
    }

    /// <summary>
    /// Deletes a virtual key by ID
    /// </summary>
    /// <param name="id">The ID of the key to delete</param>
    /// <param name="cancellationToken">Request/shutdown cancellation.</param>
    /// <returns>No content if successful</returns>
    public async Task<IResult> DeleteKey(int id, CancellationToken cancellationToken = default)
    {
        if (!await _virtualKeyService.DeleteVirtualKeyAsync(id, cancellationToken))
            throw new KeyNotFoundException();
        LogAdminAudit("Deleted", "VirtualKey", id);
        AdminOperationsMetricsService.RecordVirtualKeyOperation("delete", "success");
        AdminOperationsMetricsService.RecordConfigurationChange("virtualkey", "delete");
        return NoContent();
    }


    /// <summary>
    /// Validates a virtual key
    /// </summary>
    /// <param name="request">The validation request containing the key and optional model</param>
    /// <returns>The validation result</returns>
    // lgtm [cs/web/missing-function-level-access-control]
    public async Task<IResult> ValidateKey(ValidateVirtualKeyRequest request)
    {
        var result = await _virtualKeyService.ValidateVirtualKeyAsync(request.Key, request.RequestedModel);
        return Ok(result);
    }




    /// <summary>
    /// Gets detailed information about a virtual key for validation purposes
    /// </summary>
    /// <param name="id">The ID of the virtual key</param>
    /// <returns>The virtual key validation information</returns>
    public async Task<IResult> GetValidationInfo(int id)
    {
        var result = await _virtualKeyService.GetValidationInfoAsync(id);
        if (result == null)
        {
            return AdminResults.NotFoundEntity("Virtual key", id);
        }
        return Ok(result);
    }

    /// <summary>
    /// Performs maintenance tasks on all virtual keys
    /// </summary>
    /// <remarks>
    /// This endpoint performs the following maintenance tasks:
    /// - Disables keys that have passed their expiration date
    /// Budget resets are no longer performed in the bank account model.
    /// This is typically called by a background service.
    /// </remarks>
    /// <returns>No content if successful</returns>
    public async Task<IResult> PerformMaintenance()
    {
        await _virtualKeyService.PerformMaintenanceAsync();
        return NoContent();
    }

    /// <summary>
    /// Previews the discovery results for a virtual key
    /// </summary>
    /// <param name="id">The ID of the virtual key</param>
    /// <param name="capability">Optional capability filter (e.g. "chat", "vision", "audio_transcription")</param>
    /// <returns>The discovery results as the virtual key would see them</returns>
    public async Task<IResult> PreviewDiscovery(int id, string? capability = null)
    {
        var result = await _virtualKeyService.PreviewDiscoveryAsync(id, capability);
        if (result == null)
        {
            return AdminResults.NotFoundEntity("Virtual key", id);
        }
        return Ok(result);
    }

    /// <summary>
    /// Get the virtual key group for a specific key
    /// </summary>
    /// <param name="id">The ID of the virtual key</param>
    /// <returns>The virtual key group information</returns>
    public async Task<IResult> GetKeyGroup(int id)
    {
        var key = await _virtualKeyService.GetVirtualKeyByIdAsync(id);
        if (key == null)
        {
            throw new KeyNotFoundException("Virtual key not found");
        }

        var groupInfo = await _virtualKeyService.GetKeyGroupAsync(id);
        if (groupInfo == null)
        {
            throw new KeyNotFoundException("Virtual key group not found");
        }

        return Ok(groupInfo);
    }

    /// <summary>
    /// Get usage information for a virtual key by its key value
    /// </summary>
    /// <param name="key">The virtual key value (with prefix)</param>
    /// <returns>Usage information including balance, spending, and request counts</returns>
    /// <remarks>
    /// This endpoint allows administrators to check the usage and balance of a virtual key
    /// using the actual key value instead of the database ID. This is useful for support
    /// scenarios where users provide their key value.
    /// </remarks>
    public async Task<IResult> GetUsageByKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return BadRequest("Key value is required");
        }

        var result = await _virtualKeyService.GetUsageByKeyAsync(key);
        if (result == null)
        {
            return AdminResults.NotFoundEntity("Virtual key", null);
        }
        return Ok(result);
    }

    /// <summary>
    /// Renders per-model overrides as a stable one-line summary for the audit trail.
    /// </summary>
    private static string DescribeModelLimits(Dictionary<string, ModelRateLimitDto>? limits)
    {
        if (limits is null || limits.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", limits
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}:rpm={entry.Value.Rpm?.ToString() ?? "-"},tpm={entry.Value.Tpm?.ToString() ?? "-"}"));
    }
}
