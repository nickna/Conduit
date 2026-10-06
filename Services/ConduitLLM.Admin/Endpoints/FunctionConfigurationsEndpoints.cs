using System.Text.Json;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.DTOs;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

/// <summary>
/// Controller for managing function configurations.
/// </summary>
public class FunctionConfigurationsEndpoints
{
    private readonly IFunctionConfigurationRepository _configurationRepository;
    private readonly IEventPublisher _eventPublisher;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<FunctionConfigurationsEndpoints> _logger;

    /// <summary>
    /// Initializes the Function Configurations endpoint handler.
    /// </summary>
    public FunctionConfigurationsEndpoints(
        IFunctionConfigurationRepository configurationRepository,
        IEventPublisher eventPublisher,
        IHttpContextAccessor httpContextAccessor,
        ILogger<FunctionConfigurationsEndpoints> logger)
    {
        _configurationRepository = configurationRepository ?? throw new ArgumentNullException(nameof(configurationRepository));
        _eventPublisher = eventPublisher;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public static IEndpointRouteBuilder MapFunctionConfigurationsEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/function-configurations")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Function Configurations");
        group.MapGet("/", ([FromServices] FunctionConfigurationsEndpoints e) => e.GetAllConfigurations())
            .WithName("FunctionConfigurations_GetAll").Produces<List<FunctionConfigurationDto>>();
        group.MapGet("/{id}", ([FromServices] FunctionConfigurationsEndpoints e, int id) => e.GetConfigurationById(id))
            .WithName("FunctionConfigurations_GetById").Produces<FunctionConfigurationDto>().Produces(StatusCodes.Status404NotFound);
        group.MapGet("/provider/{providerType}", ([FromServices] FunctionConfigurationsEndpoints e, string providerType) => e.GetConfigurationsByProvider(providerType))
            .WithName("FunctionConfigurations_GetByProvider").Produces<List<FunctionConfigurationDto>>();
        group.MapGet("/purpose/{purpose}", ([FromServices] FunctionConfigurationsEndpoints e, string purpose) => e.GetConfigurationsByPurpose(purpose))
            .WithName("FunctionConfigurations_GetByPurpose").Produces<List<FunctionConfigurationDto>>();
        group.MapPost("/", ([FromServices] FunctionConfigurationsEndpoints e, CreateFunctionConfigurationRequest request) => e.CreateConfiguration(request))
            .WithName("FunctionConfigurations_Create").Produces<FunctionConfigurationDto>(StatusCodes.Status201Created).Produces(StatusCodes.Status400BadRequest);
        group.MapPatch("/{id}", ([FromServices] FunctionConfigurationsEndpoints e, int id, JsonMergePatch<UpdateFunctionConfigurationRequest> patch) => e.UpdateConfiguration(id, patch.Value))
            .AcceptsJsonMergePatch<UpdateFunctionConfigurationRequest>()
            .WithName("FunctionConfigurations_Update").Produces<FunctionConfigurationDto>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound);
        group.MapDelete("/{id}", ([FromServices] FunctionConfigurationsEndpoints e, int id) => e.DeleteConfiguration(id))
            .WithName("FunctionConfigurations_Delete").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
        return app;
    }

    /// <summary>
    /// Gets all function configurations.
    /// </summary>
    /// <returns>List of all function configurations</returns>
    public async Task<IResult> GetAllConfigurations()
    {
        var configurations = await _configurationRepository.GetAllUnboundedAsync();
        return Results.Ok(configurations.Select(ToDto).ToList());
    }

    /// <summary>
    /// Gets a function configuration by ID.
    /// </summary>
    /// <param name="id">The ID of the function configuration</param>
    /// <returns>The function configuration</returns>
    public async Task<IResult> GetConfigurationById(int id)
    {
        var configuration = await _configurationRepository.GetByIdAsync(id);
        if (configuration == null)
        {
            return AdminResults.NotFoundEntity("FunctionConfiguration", id);
        }
        return Results.Ok(ToDto(configuration));
    }

    /// <summary>
    /// Gets function configurations by provider type.
    /// </summary>
    /// <param name="providerType">The provider type (e.g., "Exa")</param>
    /// <returns>List of function configurations for the specified provider</returns>
    public async Task<IResult> GetConfigurationsByProvider(string providerType)
    {
        if (!Enum.TryParse<ConduitLLM.Functions.Enums.FunctionProviderType>(providerType, true, out var providerEnum))
        {
            return AdminResults.BadRequest($"Invalid provider type: {providerType}");
        }

        var configurations = await _configurationRepository.GetByProviderTypeAsync(providerEnum);
        return Results.Ok(configurations.Select(ToDto).ToList());
    }

    /// <summary>
    /// Gets function configurations by purpose.
    /// </summary>
    /// <param name="purpose">The purpose (e.g., "Search", "Answer", "Enrich")</param>
    /// <returns>List of function configurations for the specified purpose</returns>
    public async Task<IResult> GetConfigurationsByPurpose(string purpose)
    {
        if (!Enum.TryParse<ConduitLLM.Functions.Enums.FunctionPurpose>(purpose, true, out var purposeEnum))
        {
            return AdminResults.BadRequest($"Invalid purpose: {purpose}");
        }

        var configurations = await _configurationRepository.GetByPurposeAsync(purposeEnum);
        return Results.Ok(configurations.Select(ToDto).ToList());
    }

    /// <summary>
    /// Creates a new function configuration.
    /// </summary>
    /// <param name="request">The function configuration to create</param>
    /// <returns>The created function configuration</returns>
    public async Task<IResult> CreateConfiguration(CreateFunctionConfigurationRequest request)
    {
        if (request == null)
        {
            return AdminResults.BadRequest("Function configuration data is required");
        }

        var configuration = new FunctionConfiguration
        {
            ProviderType = request.ProviderType,
            ConfigurationName = request.ConfigurationName,
            Purpose = request.Purpose,
            DefaultExecutionMode = request.DefaultExecutionMode,
            BaseUrl = request.BaseUrl,
            IsEnabled = request.IsEnabled,
            CacheTtlMinutes = request.CacheTtlMinutes,
            TimeoutSeconds = request.TimeoutSeconds,
            MaxRetries = request.MaxRetries,
            ProviderSettings = StructuredJson.SerializeObject(request.ProviderSettings),
            ParameterSchema = StructuredJson.SerializeObject(request.ParameterSchema),
            Description = request.Description
        };
        int id = await _configurationRepository.CreateAsync(configuration);

        // Fetch the created entity to return
        var created = await _configurationRepository.GetByIdAsync(id);

        // Audit log and publish event for cache invalidation
        if (created != null)
        {
            LogAdminAudit("Created", "FunctionConfiguration", created.Id, $"Name: {LoggingSanitizer.S(created.ConfigurationName)}");
            AdminOperationsMetricsService.RecordConfigurationChange("functionconfiguration", "create");
            _eventPublisher.PublishFireAndForget(new FunctionConfigurationChanged
            {
                FunctionConfigurationId = created.Id,
                ConfigurationName = created.ConfigurationName,
                ProviderType = created.ProviderType.ToString(),
                Purpose = created.Purpose.ToString(),
                ChangeType = "Created",
                ChangedProperties = new[] { "Created" },
                IsEnabledChanged = false,
                CacheTtlChanged = false,
                CorrelationId = Guid.NewGuid().ToString()
            }, "create function configuration",
            new { ConfigName = created.ConfigurationName, ConfigId = created.Id });
        }

        return created is null
            ? AdminResults.Problem(
                StatusCodes.Status500InternalServerError,
                "Function configuration was created but could not be reloaded.",
                "function_configuration_reload_failed")
            : Results.Created($"/v1/admin/function-configurations/{id}", ToDto(created));
    }

    /// <summary>
    /// Updates an existing function configuration.
    /// </summary>
    /// <param name="id">The ID of the function configuration to update</param>
    /// <param name="request">The updated function configuration data</param>
    /// <returns>The updated function configuration</returns>
    public async Task<IResult> UpdateConfiguration(
        int id,
        UpdateFunctionConfigurationRequest request)
    {
        if (request == null)
        {
            return AdminResults.BadRequest("Function configuration data is required");
        }

        var existing = await _configurationRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return AdminResults.NotFoundEntity("FunctionConfiguration", id);
        }

        var changedProperties = new List<string>();
        var isEnabledChanged = false;
        var cacheTtlChanged = false;

        if (request.TryGetPatchedProperty(nameof(request.ConfigurationName), existing.ConfigurationName, out string? configurationName))
        {
            configurationName = configurationName ?? throw new InvalidOperationException("configurationName cannot be null.");
            SetIfChanged(configurationName, existing.ConfigurationName, value => existing.ConfigurationName = value, nameof(existing.ConfigurationName), changedProperties);
        }
        if (request.TryGetPatchedProperty(nameof(request.Purpose), existing.Purpose, out FunctionPurpose purpose))
            SetIfChanged(purpose, existing.Purpose, value => existing.Purpose = value, nameof(existing.Purpose), changedProperties);
        if (request.TryGetPatchedProperty(nameof(request.DefaultExecutionMode), existing.DefaultExecutionMode, out ExecutionMode executionMode))
            SetIfChanged(executionMode, existing.DefaultExecutionMode, value => existing.DefaultExecutionMode = value, nameof(existing.DefaultExecutionMode), changedProperties);
        if (request.TryGetPatchedProperty(nameof(request.BaseUrl), existing.BaseUrl, out string? baseUrl))
            SetIfChanged(baseUrl, existing.BaseUrl, value => existing.BaseUrl = value, nameof(existing.BaseUrl), changedProperties);
        if (request.TryGetPatchedProperty(nameof(request.IsEnabled), existing.IsEnabled, out bool isEnabled))
        {
            isEnabledChanged = existing.IsEnabled != isEnabled;
            SetIfChanged(isEnabled, existing.IsEnabled, value => existing.IsEnabled = value, nameof(existing.IsEnabled), changedProperties);
        }
        if (request.TryGetPatchedProperty(nameof(request.CacheTtlMinutes), existing.CacheTtlMinutes, out int? cacheTtl))
        {
            cacheTtlChanged = existing.CacheTtlMinutes != cacheTtl;
            SetIfChanged(cacheTtl, existing.CacheTtlMinutes, value => existing.CacheTtlMinutes = value, nameof(existing.CacheTtlMinutes), changedProperties);
        }
        if (request.TryGetPatchedProperty(nameof(request.TimeoutSeconds), existing.TimeoutSeconds, out int? timeout))
            SetIfChanged(timeout, existing.TimeoutSeconds, value => existing.TimeoutSeconds = value, nameof(existing.TimeoutSeconds), changedProperties);
        if (request.TryGetPatchedProperty(nameof(request.MaxRetries), existing.MaxRetries, out int? maxRetries))
            SetIfChanged(maxRetries, existing.MaxRetries, value => existing.MaxRetries = value, nameof(existing.MaxRetries), changedProperties);
        if (request.TryGetPatchedProperty(
                nameof(request.ProviderSettings),
                StructuredJson.ParseObject(existing.ProviderSettings),
                out Dictionary<string, JsonElement>? providerSettings))
        {
            SetIfChanged(
                StructuredJson.SerializeObject(providerSettings),
                existing.ProviderSettings,
                value => existing.ProviderSettings = value,
                nameof(existing.ProviderSettings),
                changedProperties);
        }
        if (request.TryGetPatchedProperty(
                nameof(request.ParameterSchema),
                StructuredJson.ParseObject(existing.ParameterSchema),
                out Dictionary<string, JsonElement>? parameterSchema))
        {
            SetIfChanged(
                StructuredJson.SerializeObject(parameterSchema),
                existing.ParameterSchema,
                value => existing.ParameterSchema = value,
                nameof(existing.ParameterSchema),
                changedProperties);
        }
        if (request.TryGetPatchedProperty(nameof(request.Description), existing.Description, out string? description))
            SetIfChanged(description, existing.Description, value => existing.Description = value, nameof(existing.Description), changedProperties);
        existing.UpdatedAt = DateTime.UtcNow;

        await _configurationRepository.UpdateAsync(existing);

        // Fetch the updated entity to return
        var updated = await _configurationRepository.GetByIdAsync(id);

        if (updated == null)
        {
            return AdminResults.NotFound("Function configuration not found after update");
        }

        LogAdminAudit("Updated", "FunctionConfiguration", id,
            changedProperties.Count > 0 ? $"Changed: {string.Join(", ", changedProperties)}" : null);
        AdminOperationsMetricsService.RecordConfigurationChange("functionconfiguration", "update");

        // Publish FunctionConfigurationChanged event for cache invalidation
        if (changedProperties.Count > 0)
        {
            _eventPublisher.PublishFireAndForget(new FunctionConfigurationChanged
            {
                FunctionConfigurationId = updated.Id,
                ConfigurationName = updated.ConfigurationName,
                ProviderType = updated.ProviderType.ToString(),
                Purpose = updated.Purpose.ToString(),
                ChangeType = "Updated",
                ChangedProperties = changedProperties.ToArray(),
                IsEnabledChanged = isEnabledChanged,
                CacheTtlChanged = cacheTtlChanged,
                CorrelationId = Guid.NewGuid().ToString()
            }, "update function configuration",
            new { ConfigName = updated.ConfigurationName, ConfigId = updated.Id, ChangedProps = string.Join(", ", changedProperties) });
        }

        return Results.Ok(ToDto(updated));
    }

    /// <summary>
    /// Deletes a function configuration.
    /// </summary>
    /// <param name="id">The ID of the function configuration to delete</param>
    /// <returns>No content on success</returns>
    public async Task<IResult> DeleteConfiguration(int id)
    {
        var toDelete = await _configurationRepository.GetByIdAsync(id);
        if (toDelete == null)
        {
            return AdminResults.NotFoundEntity("FunctionConfiguration", id);
        }

        await _configurationRepository.DeleteAsync(id);
        LogAdminAudit("Deleted", "FunctionConfiguration", id, $"Name: {LoggingSanitizer.S(toDelete.ConfigurationName)}");
        AdminOperationsMetricsService.RecordConfigurationChange("functionconfiguration", "delete");

        // Publish FunctionConfigurationChanged event for cache invalidation
        _eventPublisher.PublishFireAndForget(new FunctionConfigurationChanged
        {
            FunctionConfigurationId = toDelete.Id,
            ConfigurationName = toDelete.ConfigurationName,
            ProviderType = toDelete.ProviderType.ToString(),
            Purpose = toDelete.Purpose.ToString(),
            ChangeType = "Deleted",
            ChangedProperties = new[] { "Deleted" },
            IsEnabledChanged = false,
            CacheTtlChanged = false,
            CorrelationId = Guid.NewGuid().ToString()
        }, "delete function configuration",
        new { ConfigName = toDelete.ConfigurationName, ConfigId = toDelete.Id });

        return Results.NoContent();
    }

    private void LogAdminAudit(string operation, string entityType, object? entityId = null, string? detail = null) =>
        AdminAudit.Log(_httpContextAccessor.HttpContext!, _logger, operation, entityType, entityId, detail);

    private static FunctionConfigurationDto ToDto(FunctionConfiguration configuration) => new()
    {
        Id = configuration.Id,
        ProviderType = configuration.ProviderType,
        ConfigurationName = configuration.ConfigurationName,
        Purpose = configuration.Purpose,
        DefaultExecutionMode = configuration.DefaultExecutionMode,
        BaseUrl = configuration.BaseUrl,
        IsEnabled = configuration.IsEnabled,
        CacheTtlMinutes = configuration.CacheTtlMinutes,
        TimeoutSeconds = configuration.TimeoutSeconds,
        MaxRetries = configuration.MaxRetries,
        ProviderSettings = StructuredJson.ParseObject(configuration.ProviderSettings),
        ParameterSchema = StructuredJson.ParseObject(configuration.ParameterSchema),
        Description = configuration.Description,
        CreatedAt = configuration.CreatedAt,
        UpdatedAt = configuration.UpdatedAt
    };

    private static void SetIfChanged<T>(
        T requested,
        T current,
        Action<T> setter,
        string property,
        ICollection<string> changed)
    {
        if (!EqualityComparer<T>.Default.Equals(requested, current))
        {
            setter(requested);
            changed.Add(property);
        }
    }
}
