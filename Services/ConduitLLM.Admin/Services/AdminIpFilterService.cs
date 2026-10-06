using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.DTOs.IpFilter;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Services;

using ConduitLLM.Configuration.Messaging;
using Microsoft.Extensions.Options;

using ConduitLLM.Configuration.Interfaces;
namespace ConduitLLM.Admin.Services;

/// <summary>
/// Service for managing IP filters through the Admin API
/// </summary>
public class AdminIpFilterService : EventPublishingServiceBase, IAdminIpFilterService
{
    private readonly IIpFilterRepository _ipFilterRepository;
    private readonly IGlobalSettingRepository _globalSettingRepository;
    private readonly IOptionsMonitor<IpFilterOptions> _ipFilterOptions;
    private readonly ILogger<AdminIpFilterService> _logger;

    // Setting keys for IP filter configuration
    private const string SettingKeyEnabled = "IpFilter:Enabled";
    private const string SettingKeyDefaultAllow = "IpFilter:DefaultAllow";
    private const string SettingKeyBypassForAdminUi = "IpFilter:BypassForAdminUi";
    private const string SettingKeyExcludedEndpoints = "IpFilter:ExcludedEndpoints";

    /// <summary>
    /// Initializes a new instance of the AdminIpFilterService class
    /// </summary>
    /// <param name="ipFilterRepository">The IP filter repository</param>
    /// <param name="globalSettingRepository">The global settings repository for persisting IP filter settings</param>
    /// <param name="ipFilterOptions">The IP filter options</param>
    /// <param name="eventBus">Optional event bus (null if not configured)</param>
    /// <param name="logger">The logger</param>
    public AdminIpFilterService(
        IIpFilterRepository ipFilterRepository,
        IGlobalSettingRepository globalSettingRepository,
        IOptionsMonitor<IpFilterOptions> ipFilterOptions,
        ILogger<AdminIpFilterService> logger,
        IEventBus? eventBus = null)
        : base(eventBus, logger)
    {
        _ipFilterRepository = ipFilterRepository ?? throw new ArgumentNullException(nameof(ipFilterRepository));
        _globalSettingRepository = globalSettingRepository ?? throw new ArgumentNullException(nameof(globalSettingRepository));
        _ipFilterOptions = ipFilterOptions ?? throw new ArgumentNullException(nameof(ipFilterOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        LogEventPublishingConfiguration(nameof(AdminIpFilterService));
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<IpFilterDto>> GetAllFiltersAsync()
    {
        try
        {
            _logger.LogDebug("Getting all IP filters");

            // Global IP filtering surface: return only global filters (VirtualKeyId == null). Per-key
            // filters are managed via GetFiltersByVirtualKeyIdAsync.
            var filters = await _ipFilterRepository.GetAllUnboundedAsync();
            return filters.Where(f => f.VirtualKeyId == null).Select(f => f.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all IP filters");
            return Enumerable.Empty<IpFilterDto>();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<IpFilterDto>> GetEnabledFiltersAsync()
    {
        try
        {
            _logger.LogDebug("Getting enabled IP filters");

            var filters = await _ipFilterRepository.GetEnabledAsync();
            return filters.Select(f => f.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting enabled IP filters");
            return Enumerable.Empty<IpFilterDto>();
        }
    }

    /// <inheritdoc/>
    public async Task<IpFilterDto?> GetFilterByIdAsync(int id)
    {
        try
        {
            _logger.LogDebug("Getting IP filter with ID: {FilterId}", id);

            var filter = await _ipFilterRepository.GetByIdAsync(id);
            return filter?.ToDto();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting IP filter with ID {FilterId}", id);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<IpFilterDto>> GetFiltersByVirtualKeyIdAsync(int virtualKeyId)
    {
        try
        {
            _logger.LogDebug("Getting IP filters for virtual key {VirtualKeyId}", virtualKeyId);

            var filters = await _ipFilterRepository.GetByVirtualKeyIdAsync(virtualKeyId);
            return filters.Select(f => f.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting IP filters for virtual key {VirtualKeyId}", virtualKeyId);
            return Enumerable.Empty<IpFilterDto>();
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string? ErrorMessage, IpFilterDto? Filter)> CreateFilterAsync(CreateIpFilterDto createFilter)
    {
        try
        {
            _logger.LogDebug("Creating new IP filter for {IpAddress}", (LoggingSanitizer.S(createFilter.IpAddressOrCidr ?? "")));

            // Validate the IP address format
            if (string.IsNullOrWhiteSpace(createFilter.IpAddressOrCidr) || !IsValidIpAddressOrCidr(createFilter.IpAddressOrCidr))
            {
                return (false, "Invalid IP address or CIDR format", null);
            }

            // Map to entity
            var entity = new IpFilterEntity
            {
                FilterType = createFilter.FilterType,
                IpAddressOrCidr = createFilter.IpAddressOrCidr,
                Name = createFilter.Name,
                Description = createFilter.Description,
                IsEnabled = createFilter.IsEnabled,
                VirtualKeyId = createFilter.VirtualKeyId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Save to database
            var createdFilter = await _ipFilterRepository.AddAsync(entity);

            // Publish IpFilterChanged event for cache invalidation and cross-service coordination
            await PublishEventAsync(
                new IpFilterChanged
                {
                    FilterId = createdFilter.Id,
                    IpAddressOrCidr = createdFilter.IpAddressOrCidr,
                    FilterType = createdFilter.FilterType,
                    IsEnabled = createdFilter.IsEnabled,
                    ChangeType = "Created",
                    ChangedProperties = Array.Empty<string>(),
                    Description = createdFilter.Description ?? string.Empty,
                    CorrelationId = Guid.NewGuid().ToString()
                },
                $"create IP filter {createdFilter.Id}",
                new { IpAddressOrCidr = createdFilter.IpAddressOrCidr, FilterType = createdFilter.FilterType });

            _logger.LogInformation("IP filter created: {FilterId} type={FilterType} target={IpAddress}",
                createdFilter.Id, createdFilter.FilterType, LoggingSanitizer.S(createdFilter.IpAddressOrCidr));

            // Return the created filter
            return (true, null, createdFilter.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating IP filter for {IpAddress}", (LoggingSanitizer.S(createFilter.IpAddressOrCidr ?? "")));
            return (false, "An unexpected error occurred", null);
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string? ErrorMessage)> UpdateFilterAsync(int id, UpdateIpFilterDto updateFilter)
    {
        try
        {
            _logger.LogDebug("Updating IP filter with ID: {FilterId}", id);

            // Check if the filter exists
            var existingFilter = await _ipFilterRepository.GetByIdAsync(id);
            if (existingFilter == null)
            {
                return (false, $"IP filter with ID {id} not found");
            }

            updateFilter.TryGetPatchedProperty(
                nameof(updateFilter.FilterType),
                existingFilter.FilterType,
                out string? filterType);
            updateFilter.TryGetPatchedProperty(
                nameof(updateFilter.IpAddressOrCidr),
                existingFilter.IpAddressOrCidr,
                out string? ipAddressOrCidr);
            updateFilter.TryGetPatchedProperty(
                nameof(updateFilter.Name),
                existingFilter.Name,
                out string? name);
            updateFilter.TryGetPatchedProperty(
                nameof(updateFilter.Description),
                existingFilter.Description,
                out string? description);
            updateFilter.TryGetPatchedProperty(
                nameof(updateFilter.IsEnabled),
                existingFilter.IsEnabled,
                out bool isEnabled);

            if (updateFilter.IsDefined(nameof(updateFilter.FilterType)) &&
                string.IsNullOrWhiteSpace(filterType))
            {
                return (false, "Filter type cannot be null or empty");
            }
            if (updateFilter.IsDefined(nameof(updateFilter.IpAddressOrCidr)) &&
                (string.IsNullOrWhiteSpace(ipAddressOrCidr) ||
                 !IsValidIpAddressOrCidr(ipAddressOrCidr)))
            {
                return (false, "Invalid IP address or CIDR format");
            }

            // Track changes for event publishing
            var changedProperties = new List<string>();

            if (updateFilter.IsDefined(nameof(updateFilter.FilterType)) &&
                existingFilter.FilterType != filterType)
            {
                existingFilter.FilterType = filterType!;
                changedProperties.Add(nameof(existingFilter.FilterType));
            }

            if (updateFilter.IsDefined(nameof(updateFilter.IpAddressOrCidr)) &&
                existingFilter.IpAddressOrCidr != ipAddressOrCidr)
            {
                existingFilter.IpAddressOrCidr = ipAddressOrCidr!;
                changedProperties.Add(nameof(existingFilter.IpAddressOrCidr));
            }

            // Normalize null vs empty so a null-named legacy row and an unset ("") DTO field are not
            // treated as a change (which would break the no-op-skip path).
            if (updateFilter.IsDefined(nameof(updateFilter.Name)) &&
                existingFilter.Name != name)
            {
                existingFilter.Name = name;
                changedProperties.Add(nameof(existingFilter.Name));
            }

            if (updateFilter.IsDefined(nameof(updateFilter.Description)) &&
                existingFilter.Description != description)
            {
                existingFilter.Description = description;
                changedProperties.Add(nameof(existingFilter.Description));
            }

            if (updateFilter.IsDefined(nameof(updateFilter.IsEnabled)) &&
                existingFilter.IsEnabled != isEnabled)
            {
                existingFilter.IsEnabled = isEnabled;
                changedProperties.Add(nameof(existingFilter.IsEnabled));
            }

            // Only proceed if there are actual changes
            if (!changedProperties.Any())
            {
                _logger.LogDebug("No changes detected for IP filter {FilterId} - skipping update", id);
                return (true, null);
            }

            existingFilter.UpdatedAt = DateTime.UtcNow;

            // Save to database
            var success = await _ipFilterRepository.UpdateAsync(existingFilter);

            if (success)
            {
                _logger.LogInformation("IP filter updated: {FilterId} changed=[{ChangedProperties}]",
                    existingFilter.Id, string.Join(", ", changedProperties));

                // Publish IpFilterChanged event for cache invalidation and cross-service coordination
                await PublishEventAsync(
                    new IpFilterChanged
                    {
                        FilterId = existingFilter.Id,
                        IpAddressOrCidr = existingFilter.IpAddressOrCidr,
                        FilterType = existingFilter.FilterType,
                        IsEnabled = existingFilter.IsEnabled,
                        ChangeType = "Updated",
                        ChangedProperties = changedProperties.ToArray(),
                        Description = existingFilter.Description ?? string.Empty,
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"update IP filter {existingFilter.Id}",
                    new { ChangedProperties = string.Join(", ", changedProperties) });

                return (true, null);
            }
            else
            {
                _logger.LogWarning("Failed to update IP filter {FilterId} in database", id);
                return (false, "Failed to update the IP filter");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating IP filter with ID {FilterId}", id);
            return (false, "An unexpected error occurred");
        }
    }

    public Task<(bool Success, string? ErrorMessage)> UpdateFilterAsync(UpdateIpFilterDto updateFilter) =>
        UpdateFilterAsync(updateFilter.Id, updateFilter);

    /// <inheritdoc/>
    public async Task<(bool Success, string? ErrorMessage)> DeleteFilterAsync(int id)
    {
        try
        {
            _logger.LogDebug("Deleting IP filter with ID: {FilterId}", id);

            // Check if the filter exists
            var existingFilter = await _ipFilterRepository.GetByIdAsync(id);
            if (existingFilter == null)
            {
                return (false, $"IP filter with ID {id} not found");
            }

            // Delete from database
            var success = await _ipFilterRepository.DeleteAsync(id);

            if (success)
            {
                _logger.LogInformation("IP filter deleted: {FilterId} type={FilterType} target={IpAddress}",
                    existingFilter.Id, existingFilter.FilterType, LoggingSanitizer.S(existingFilter.IpAddressOrCidr));

                // Publish IpFilterChanged event for cache invalidation and cross-service coordination
                await PublishEventAsync(
                    new IpFilterChanged
                    {
                        FilterId = existingFilter.Id,
                        IpAddressOrCidr = existingFilter.IpAddressOrCidr,
                        FilterType = existingFilter.FilterType,
                        IsEnabled = existingFilter.IsEnabled,
                        ChangeType = "Deleted",
                        ChangedProperties = Array.Empty<string>(),
                        Description = existingFilter.Description ?? string.Empty,
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"delete IP filter {existingFilter.Id}",
                    new { IpAddressOrCidr = existingFilter.IpAddressOrCidr, FilterType = existingFilter.FilterType });

                return (true, null);
            }
            else
            {
                _logger.LogWarning("Failed to delete IP filter {FilterId} from database", id);
                return (false, "Failed to delete the IP filter");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting IP filter with ID {FilterId}", id);
            return (false, "An unexpected error occurred");
        }
    }

    /// <inheritdoc/>
    public async Task<IpFilterSettingsDto> GetIpFilterSettingsAsync()
    {
        try
        {
            _logger.LogDebug("Getting IP filter settings");

            // Try to get settings from database first
            var enabledSetting = await _globalSettingRepository.GetByKeyAsync(SettingKeyEnabled);
            var defaultAllowSetting = await _globalSettingRepository.GetByKeyAsync(SettingKeyDefaultAllow);
            var bypassAdminUiSetting = await _globalSettingRepository.GetByKeyAsync(SettingKeyBypassForAdminUi);
            var excludedEndpointsSetting = await _globalSettingRepository.GetByKeyAsync(SettingKeyExcludedEndpoints);

            // Fall back to config file options if database settings don't exist
            var options = _ipFilterOptions.CurrentValue;

            var settings = new IpFilterSettingsDto
            {
                IsEnabled = enabledSetting != null
                    ? bool.TryParse(enabledSetting.Value, out var enabled) && enabled
                    : options.Enabled,

                DefaultAllow = defaultAllowSetting != null
                    ? bool.TryParse(defaultAllowSetting.Value, out var defaultAllow) && defaultAllow
                    : options.DefaultAllow,

                BypassForAdminUi = bypassAdminUiSetting != null
                    ? bool.TryParse(bypassAdminUiSetting.Value, out var bypass) && bypass
                    : options.BypassForAdminUi,

                ExcludedEndpoints = excludedEndpointsSetting != null
                    ? DeserializeExcludedEndpoints(excludedEndpointsSetting.Value)
                    : options.ExcludedEndpoints.ToList()
            };

            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting IP filter settings");

            // Return default settings on error
            return new IpFilterSettingsDto
            {
                IsEnabled = false,
                DefaultAllow = true,
                BypassForAdminUi = true,
                ExcludedEndpoints = new List<string> { "/api/v1/health" }
            };
        }
    }

    /// <summary>
    /// Deserializes the excluded endpoints JSON string to a list
    /// </summary>
    private List<string> DeserializeExcludedEndpoints(string json)
    {
        try
        {
            var endpoints = AdminJson.Deserialize<List<string>>(json);
            return endpoints ?? new List<string> { "/api/v1/health" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize excluded endpoints JSON, using defaults");
            return new List<string> { "/api/v1/health" };
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string? ErrorMessage)> UpdateIpFilterSettingsAsync(IpFilterSettingsDto settings)
    {
        try
        {
            _logger.LogDebug("Updating IP filter settings: Enabled={Enabled}, DefaultAllow={DefaultAllow}",
                settings.IsEnabled, settings.DefaultAllow);

            // Validate settings
            if (settings.ExcludedEndpoints == null)
            {
                settings.ExcludedEndpoints = new List<string>();
            }

            // Persist settings to database using GlobalSettingRepository
            await _globalSettingRepository.UpsertAsync(
                SettingKeyEnabled,
                settings.IsEnabled.ToString(),
                "Whether IP filtering is enabled");

            await _globalSettingRepository.UpsertAsync(
                SettingKeyDefaultAllow,
                settings.DefaultAllow.ToString(),
                "Default filter mode when no specific rules match (true = allow, false = deny)");

            await _globalSettingRepository.UpsertAsync(
                SettingKeyBypassForAdminUi,
                settings.BypassForAdminUi.ToString(),
                "Whether to bypass filtering for admin UI access");

            await _globalSettingRepository.UpsertAsync(
                SettingKeyExcludedEndpoints,
                AdminJson.Serialize(settings.ExcludedEndpoints),
                "List of endpoints to exclude from IP filtering");

            _logger.LogInformation("IP filter settings updated successfully in database");

            // Publish event for cache invalidation across services
            await PublishEventAsync(
                new IpFilterChanged
                {
                    FilterId = 0, // Settings change, not a specific filter
                    IpAddressOrCidr = "*",
                    FilterType = "settings",
                    IsEnabled = settings.IsEnabled,
                    ChangeType = "SettingsUpdated",
                    ChangedProperties = new[] { "IsEnabled", "DefaultAllow", "BypassForAdminUi", "ExcludedEndpoints" },
                    Description = "IP filter settings updated",
                    CorrelationId = Guid.NewGuid().ToString()
                },
                "update IP filter settings",
                new { IsEnabled = settings.IsEnabled, DefaultAllow = settings.DefaultAllow });

            // Also publish GlobalSettingChanged so GlobalSettingsCacheService (used by the Gateway
            // enforcement gate and the DefaultAllow policy) invalidates these keys live. Without this
            // the WebAdmin toggle would not take effect until the process restarts.
            foreach (var settingKey in new[] { SettingKeyEnabled, SettingKeyDefaultAllow })
            {
                await PublishEventAsync(
                    new GlobalSettingChanged
                    {
                        SettingId = 0,
                        SettingKey = settingKey,
                        ChangeType = "Updated",
                        ChangedProperties = Array.Empty<string>(),
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"invalidate global setting {settingKey}",
                    new { SettingKey = settingKey });
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating IP filter settings");
            return (false, "An unexpected error occurred while saving settings");
        }
    }

    /// <inheritdoc/>
    public async Task<IpCheckResult> CheckIpAddressAsync(string ipAddress)
    {
        // GetIpFilterSettingsAsync has its own error handling and returns safe defaults on failure,
        // so it will not throw; keep it outside the try below so DefaultAllow is available to the
        // fail posture in the catch.
        var settings = await GetIpFilterSettingsAsync();

        // If IP filtering is disabled, allow all
        if (!settings.IsEnabled)
        {
            return new IpCheckResult { IsAllowed = true };
        }

        // Validate the IP format
        if (!System.Net.IPAddress.TryParse(ipAddress, out _))
        {
            return new IpCheckResult
            {
                IsAllowed = false,
                DeniedReason = "Invalid IP address format"
            };
        }

        try
        {
            _logger.LogDebug("Checking if IP address is allowed: {IpAddress}", LoggingSanitizer.S(ipAddress));

            // Load enabled rules as entities and evaluate with the shared precedence model, so the
            // Admin control plane and the Gateway data plane decide identically.
            var filters = (await _ipFilterRepository.GetEnabledAsync()).ToList();
            var whitelist = filters.Where(f => f.FilterType == IpFilterConstants.WHITELIST).ToList();
            var blacklist = filters.Where(f => f.FilterType == IpFilterConstants.BLACKLIST).ToList();

            var decision = IpFilterEvaluator.Evaluate(ipAddress, whitelist, blacklist, settings.DefaultAllow);

            if (!decision.IsAllowed)
            {
                _logger.LogWarning("IP {IpAddress} denied: {Reason}",
                    LoggingSanitizer.S(ipAddress), decision.Reason);
            }

            return new IpCheckResult
            {
                IsAllowed = decision.IsAllowed,
                DeniedReason = decision.IsAllowed ? null : decision.Reason
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if IP address is allowed: {IpAddress}", LoggingSanitizer.S(ipAddress));

            // Nuanced fail posture: apply the default-allow policy. Fails OPEN when permissive
            // (DefaultAllow=true) and CLOSED when restrictive (DefaultAllow=false), so a DB blip
            // cannot silently disable an allowlist.
            return new IpCheckResult
            {
                IsAllowed = settings.DefaultAllow,
                DeniedReason = settings.DefaultAllow ? null : "Error during IP check; denied by default-deny policy"
            };
        }
    }

    /// <summary>
    /// Validates if a string is a valid IP address or CIDR notation.
    /// Delegates to IpAddressHelper for consistent validation.
    /// </summary>
    /// <param name="ipAddressOrCidr">The string to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    private bool IsValidIpAddressOrCidr(string ipAddressOrCidr)
    {
        return IpAddressHelper.IsValidIpAddressOrCidr(ipAddressOrCidr);
    }

    /// <inheritdoc/>
    public async Task<bool> IsIpAllowedAsync(string ipAddress)
    {
        var result = await CheckIpAddressAsync(ipAddress);
        return result.IsAllowed;
    }
}
