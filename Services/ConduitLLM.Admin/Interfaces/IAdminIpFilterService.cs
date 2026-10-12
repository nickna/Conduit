using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Configuration.DTOs.IpFilter;

namespace ConduitLLM.Admin.Interfaces;

/// <summary>
/// Service interface for managing IP filters through the Admin API
/// </summary>
public interface IAdminIpFilterService
{
    /// <summary>
    /// Gets all IP filters
    /// </summary>
    /// <returns>Collection of IP filters</returns>
    Task<IEnumerable<IpFilterDto>> GetAllFiltersAsync();

    /// <summary>
    /// Gets all enabled IP filters
    /// </summary>
    /// <returns>Collection of enabled IP filters</returns>
    Task<IEnumerable<IpFilterDto>> GetEnabledFiltersAsync();

    /// <summary>
    /// Gets an IP filter by ID
    /// </summary>
    /// <param name="id">The ID of the filter to get</param>
    /// <returns>The IP filter if found, null otherwise</returns>
    Task<IpFilterDto?> GetFilterByIdAsync(int id);

    /// <summary>
    /// Gets the IP filters scoped to a specific virtual key.
    /// </summary>
    /// <param name="virtualKeyId">The virtual key ID</param>
    /// <returns>The virtual key's IP filters</returns>
    Task<IEnumerable<IpFilterDto>> GetFiltersByVirtualKeyIdAsync(int virtualKeyId);

    /// <summary>
    /// Creates a new IP filter
    /// </summary>
    /// <param name="filter">The filter to create</param>
    /// <returns>Success result with the created filter or error message</returns>
    Task<(bool Success, string? ErrorMessage, IpFilterDto? Filter)> CreateFilterAsync(CreateIpFilterDto filter);

    /// <summary>
    /// Updates an existing IP filter
    /// </summary>
    /// <param name="id">The route ID of the filter to update</param>
    /// <param name="filter">The filter to update</param>
    /// <returns>Success result with error message if failed</returns>
    Task<(bool Success, string? ErrorMessage)> UpdateFilterAsync(int id, UpdateIpFilterDto filter);
    Task<(bool Success, string? ErrorMessage, IpFilterDto? Filter)> UpdateFilterConditionalAsync(
        int id, UpdateIpFilterDto filter, ResourcePrecondition precondition, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an IP filter
    /// </summary>
    /// <param name="id">The ID of the filter to delete</param>
    /// <returns>Success result with error message if failed</returns>
    Task<(bool Success, string? ErrorMessage)> DeleteFilterAsync(int id);
    Task<(bool Success, string? ErrorMessage)> DeleteFilterConditionalAsync(
        int id, ResourcePrecondition precondition, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current IP filter settings
    /// </summary>
    /// <returns>The current IP filter settings</returns>
    Task<IpFilterSettingsDto> GetIpFilterSettingsAsync();

    /// <summary>
    /// Updates the IP filter settings
    /// </summary>
    /// <param name="settings">The new settings</param>
    /// <returns>Success result with error message if failed</returns>
    Task<(bool Success, string? ErrorMessage)> UpdateIpFilterSettingsAsync(IpFilterSettingsDto settings);

    /// <summary>
    /// Checks if an IP address is allowed based on current filter rules
    /// </summary>
    /// <param name="ipAddress">The IP address to check</param>
    /// <returns>Result indicating if the IP is allowed and reason if denied</returns>
    Task<IpCheckResult> CheckIpAddressAsync(string ipAddress);

    /// <summary>
    /// Checks if an IP address is allowed (simplified method for security service)
    /// </summary>
    /// <param name="ipAddress">The IP address to check</param>
    /// <returns>True if allowed, false otherwise</returns>
    Task<bool> IsIpAllowedAsync(string ipAddress);
}
