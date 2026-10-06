using ConduitLLM.Core.Extensions;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Filters;
using ConduitLLM.Configuration.DTOs.IpFilter;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

/// <summary>
/// Controller for managing IP filters
/// </summary>
public class IpFilterEndpoints : AdminEndpointHandlerBase
{
    private readonly IAdminIpFilterService _ipFilterService;

    /// <summary>
    /// Initializes the IP Filter endpoint handler.
    /// </summary>
    /// <param name="ipFilterService">The IP filter service</param>
    /// <param name="httpContextAccessor">Accessor for the current request context</param>
    /// <param name="logger">The logger</param>
    public IpFilterEndpoints(
        IAdminIpFilterService ipFilterService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<IpFilterEndpoints> logger)
        : base(null, httpContextAccessor, logger)
    {
        _ipFilterService = ipFilterService ?? throw new ArgumentNullException(nameof(ipFilterService));
    }

    public static IEndpointRouteBuilder MapIpFilterEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/ip-filters")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<VersionedResourceEndpointFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("IP Filters");

        group.MapGet("/", ([FromServices] IpFilterEndpoints endpoints) => endpoints.GetAllFilters())
            .WithName("IpFilter_GetAll").Produces<IEnumerable<IpFilterDto>>();
        group.MapGet("/enabled", ([FromServices] IpFilterEndpoints endpoints) => endpoints.GetEnabledFilters())
            .WithName("IpFilter_GetEnabled").Produces<IEnumerable<IpFilterDto>>();
        group.MapGet("/by-virtual-key/{virtualKeyId}", ([FromServices] IpFilterEndpoints endpoints, int virtualKeyId) => endpoints.GetFiltersByVirtualKey(virtualKeyId))
            .WithName("IpFilter_GetByVirtualKey").Produces<IEnumerable<IpFilterDto>>();
        group.MapGet("/{id}", ([FromServices] IpFilterEndpoints endpoints, int id) => endpoints.GetFilterById(id))
            .WithName("IpFilter_GetById").Produces<IpFilterDto>().Produces(StatusCodes.Status404NotFound);
        group.MapPost("/", ([FromServices] IpFilterEndpoints endpoints, CreateIpFilterDto filter) => endpoints.CreateFilter(filter))
            .WithName("IpFilter_Create").Produces<IpFilterDto>(StatusCodes.Status201Created).Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
        group.MapPatch("/{id}", ([FromServices] IpFilterEndpoints endpoints, int id, JsonMergePatch<UpdateIpFilterDto> patch) => endpoints.UpdateFilter(id, patch.Value))
            .AcceptsJsonMergePatch<UpdateIpFilterDto>()
            .WithName("IpFilter_Update").Produces<IpFilterDto>().Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);
        group.MapDelete("/{id}", ([FromServices] IpFilterEndpoints endpoints, int id) => endpoints.DeleteFilter(id))
            .WithName("IpFilter_Delete").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);
        group.MapGet("/settings", ([FromServices] IpFilterEndpoints endpoints) => endpoints.GetSettings())
            .WithName("IpFilter_GetSettings").Produces<IpFilterSettingsDto>();
        group.MapPut("/settings", ([FromServices] IpFilterEndpoints endpoints, IpFilterSettingsDto settings) => endpoints.UpdateSettings(settings))
            .WithName("IpFilter_UpdateSettings").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/check/{ipAddress}", ([FromServices] IpFilterEndpoints endpoints, string ipAddress) => endpoints.CheckIpAddress(ipAddress))
            .WithName("IpFilter_Check").Produces<IpCheckResult>().Produces(StatusCodes.Status400BadRequest).AllowAnonymous();
        return app;
    }

    /// <summary>
    /// Gets all IP filters
    /// </summary>
    /// <returns>List of all IP filters</returns>
    public async Task<IResult> GetAllFilters()
    {
        var filters = await _ipFilterService.GetAllFiltersAsync();
        return Ok(filters);
    }

    /// <summary>
    /// Gets all enabled IP filters
    /// </summary>
    /// <returns>List of all enabled IP filters</returns>
    public async Task<IResult> GetEnabledFilters()
    {
        var filters = await _ipFilterService.GetEnabledFiltersAsync();
        return Ok(filters);
    }

    /// <summary>
    /// Gets the IP filters scoped to a specific virtual key
    /// </summary>
    /// <param name="virtualKeyId">The virtual key ID</param>
    /// <returns>List of the virtual key's IP filters</returns>
    public async Task<IResult> GetFiltersByVirtualKey(int virtualKeyId)
    {
        var filters = await _ipFilterService.GetFiltersByVirtualKeyIdAsync(virtualKeyId);
        return Ok(filters);
    }

    /// <summary>
    /// Gets an IP filter by ID
    /// </summary>
    /// <param name="id">The ID of the filter to get</param>
    /// <returns>The IP filter</returns>
    public async Task<IResult> GetFilterById(int id)
    {
        var filter = await _ipFilterService.GetFilterByIdAsync(id);
        if (filter == null)
        {
            return AdminResults.NotFoundEntity("IP filter", id);
        }
        return Ok(filter);
    }

    /// <summary>
    /// Creates a new IP filter
    /// </summary>
    /// <param name="filter">The filter to create</param>
    /// <returns>The created filter</returns>
    public async Task<IResult> CreateFilter(CreateIpFilterDto filter)
    {
        var (success, errorMessage, createdFilter) = await _ipFilterService.CreateFilterAsync(filter);

        if (!success)
        {
            throw new InvalidOperationException(errorMessage);
        }

        LogAdminAudit("Created", "IpFilter", createdFilter!.Id, $"CIDR: {LoggingSanitizer.S(filter.IpAddressOrCidr)}, Type: {filter.FilterType}");
        return Results.Created($"/v1/admin/ip-filters/{createdFilter.Id}", createdFilter);
    }

    /// <summary>
    /// Updates an existing IP filter
    /// </summary>
    /// <param name="id">The ID of the filter to update</param>
    /// <param name="filter">The updated filter data</param>
    /// <returns>No content if successful</returns>
    public async Task<IResult> UpdateFilter(int id, UpdateIpFilterDto filter)
    {
        var (success, errorMessage) = await _ipFilterService.UpdateFilterAsync(id, filter);

        if (!success)
        {
            if (errorMessage?.Contains("not found") == true)
            {
                throw new KeyNotFoundException(errorMessage);
            }

            throw new InvalidOperationException(errorMessage);
        }

        LogAdminAudit("Updated", "IpFilter", id, $"CIDR: {LoggingSanitizer.S(filter.IpAddressOrCidr)}, Type: {filter.FilterType}");
        return Ok(await _ipFilterService.GetFilterByIdAsync(id) ?? throw new KeyNotFoundException());
    }

    /// <summary>
    /// Deletes an IP filter
    /// </summary>
    /// <param name="id">The ID of the filter to delete</param>
    /// <returns>No content if successful</returns>
    public async Task<IResult> DeleteFilter(int id)
    {
        var (success, errorMessage) = await _ipFilterService.DeleteFilterAsync(id);

        if (!success)
        {
            if (errorMessage?.Contains("not found") == true)
            {
                throw new KeyNotFoundException(errorMessage);
            }

            throw new InvalidOperationException(errorMessage);
        }

        LogAdminAudit("Deleted", "IpFilter", id, $"Id: {id}");
        return NoContent();
    }

    /// <summary>
    /// Gets the current IP filter settings
    /// </summary>
    /// <returns>The current IP filter settings</returns>
    public async Task<IResult> GetSettings()
    {
        var settings = await _ipFilterService.GetIpFilterSettingsAsync();
        return Ok(settings);
    }

    /// <summary>
    /// Updates the IP filter settings
    /// </summary>
    /// <param name="settings">The new settings</param>
    /// <returns>No content if successful</returns>
    public async Task<IResult> UpdateSettings(IpFilterSettingsDto settings)
    {
        var (success, errorMessage) = await _ipFilterService.UpdateIpFilterSettingsAsync(settings);

        if (!success)
        {
            throw new InvalidOperationException(errorMessage);
        }

        LogAdminAudit("Updated", "IpFilterSettings", detail: $"Enabled: {settings.IsEnabled}, DefaultAllow: {settings.DefaultAllow}");
        return NoContent();
    }

    /// <summary>
    /// Checks if an IP address is allowed based on current filter rules
    /// </summary>
    /// <param name="ipAddress">The IP address to check</param>
    /// <returns>Result indicating if the IP is allowed and reason if denied</returns>
    public async Task<IResult> CheckIpAddress(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return BadRequest("IP address must be provided");
        }

        var result = await _ipFilterService.CheckIpAddressAsync(ipAddress);
        return Ok(result);
    }
}
