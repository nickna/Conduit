using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Configuration.Messaging;

namespace ConduitLLM.Admin.Endpoints
{
    /// <summary>
    /// Controller for managing provider tools and their costs.
    /// </summary>
    public class ProviderToolsEndpoints
    {
        private readonly ConduitDbContext _context;
        private readonly IEventBus? _eventBus;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<ProviderToolsEndpoints> _logger;

        /// <summary>
        /// Initializes the Provider Tools endpoint handler.
        /// </summary>
        public ProviderToolsEndpoints(
            ConduitDbContext context,
            IHttpContextAccessor httpContextAccessor,
            ILogger<ProviderToolsEndpoints> logger,
            IEventBus? eventBus = null)
        {
            _context = context;
            _eventBus = eventBus;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public static IEndpointRouteBuilder MapProviderToolsEndpoints(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/v1/admin/provider-tools")
                .AddEndpointFilter<OperationLoggingEndpointFilter>()
                .WithTags("Provider Tools");
            group.MapGet("/", ([FromServices] ProviderToolsEndpoints e, ProviderType? provider = null, bool? isActive = null) => e.GetProviderTools(provider, isActive)).WithName("ProviderTools_GetAll").Produces<IEnumerable<ProviderToolDto>>();
            group.MapGet("/{id}", ([FromServices] ProviderToolsEndpoints e, int id) => e.GetProviderTool(id)).WithName("ProviderTools_GetById").Produces<ProviderToolDto>().Produces(StatusCodes.Status404NotFound);
            group.MapPost("/", ([FromServices] ProviderToolsEndpoints e, CreateProviderToolDto dto) => e.CreateProviderTool(dto)).WithName("ProviderTools_Create").Produces<ProviderToolDto>(StatusCodes.Status201Created).Produces(StatusCodes.Status400BadRequest);
            group.MapPatch("/{id}", ([FromServices] ProviderToolsEndpoints e, int id, JsonMergePatch<UpdateProviderToolDto> patch) => e.UpdateProviderTool(id, patch.Value)).AcceptsJsonMergePatch<UpdateProviderToolDto>().WithName("ProviderTools_Update").Produces<ProviderToolDto>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound);
            group.MapDelete("/{id}", ([FromServices] ProviderToolsEndpoints e, int id) => e.DeleteProviderTool(id)).WithName("ProviderTools_Delete").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
            group.MapGet("/providers", ([FromServices] ProviderToolsEndpoints e) => e.GetToolProviders()).WithName("ProviderTools_GetProviders").Produces<IEnumerable<ToolProviderDto>>();
            group.MapGet("/billing-units", ([FromServices] ProviderToolsEndpoints e) => e.GetBillingUnits()).WithName("ProviderTools_GetBillingUnits").Produces<IEnumerable<string>>();
            group.MapPost("/import", ([FromServices] ProviderToolsEndpoints e, List<CreateProviderToolDto> tools) => e.ImportProviderTools(tools)).WithName("ProviderTools_Import").Produces<ProviderToolImportResultDto>();
            group.MapGet("/export", ([FromServices] ProviderToolsEndpoints e) => e.ExportProviderTools()).WithName("ProviderTools_Export").Produces<IEnumerable<ProviderToolDto>>();
            return app;
        }

        /// <summary>
        /// Gets all provider tools.
        /// </summary>
        /// <param name="provider">Optional provider type filter</param>
        /// <param name="isActive">Optional active status filter</param>
        /// <returns>List of provider tools</returns>
        public async Task<IResult> GetProviderTools(
            [FromQuery] ProviderType? provider = null,
            [FromQuery] bool? isActive = null)
        {
            var query = _context.ProviderTools.AsQueryable();

            if (provider.HasValue)
            {
                query = query.Where(pt => pt.Provider == provider.Value);
            }

            if (isActive.HasValue)
            {
                query = query.Where(pt => pt.IsActive == isActive.Value);
            }

            var tools = await query
                .OrderBy(pt => pt.Provider)
                .ThenBy(pt => pt.ToolName)
                .ToListAsync();

            return Results.Ok(tools.Select(ProviderToolDto.FromEntity));
        }

        /// <summary>
        /// Gets a specific provider tool by ID.
        /// </summary>
        /// <param name="id">Tool ID</param>
        /// <returns>Provider tool details</returns>
        public async Task<IResult> GetProviderTool(int id)
        {
            var tool = await _context.ProviderTools.FindAsync(id);
            if (tool == null)
            {
                throw new KeyNotFoundException($"Provider tool with ID '{id}' not found");
            }

            return Results.Ok(ProviderToolDto.FromEntity(tool));
        }

        /// <summary>
        /// Creates a new provider tool.
        /// </summary>
        /// <param name="dto">Provider tool creation data</param>
        /// <returns>Created provider tool</returns>
        public async Task<IResult> CreateProviderTool(CreateProviderToolDto dto)
        {
            // Validate billing unit
            ValidateBillingUnit(dto.BillingUnit);

            // Check if tool already exists for this provider
            var existingTool = await _context.ProviderTools
                .FirstOrDefaultAsync(pt => pt.Provider == dto.Provider && pt.ToolName == dto.ToolName);

            if (existingTool != null)
            {
                throw new InvalidOperationException($"Tool '{dto.ToolName}' already exists for provider {dto.Provider}");
            }

            var tool = new ProviderTool
            {
                Provider = dto.Provider,
                ToolName = dto.ToolName,
                ToolParameters = dto.ToolParameters,
                CostPerUnit = dto.CostPerUnit,
                BillingUnit = dto.BillingUnit,
                CostDescription = dto.CostDescription,
                IsActive = dto.IsActive,
                UpdatedAt = DateTime.UtcNow
            };

            _context.ProviderTools.Add(tool);
            await _context.SaveChangesAsync();

            LogAdminAudit("Created", "ProviderTool", tool.Id,
                $"ToolName: {LoggingSanitizer.S(tool.ToolName)}, Provider: {tool.Provider}");

            await PublishToolChangedEventAsync(tool, "Created");

            var result = ProviderToolDto.FromEntity(tool);
            return Results.Created($"/v1/admin/provider-tools/{result.Id}", result);
        }

        /// <summary>
        /// Updates an existing provider tool.
        /// </summary>
        /// <param name="id">Tool ID</param>
        /// <param name="dto">Updated tool data</param>
        /// <returns>Updated provider tool</returns>
        public async Task<IResult> UpdateProviderTool(int id, UpdateProviderToolDto dto)
        {
            var tool = await _context.ProviderTools.FindAsync(id);
            if (tool == null)
            {
                throw new KeyNotFoundException($"Provider tool with ID '{id}' not found");
            }

            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.IsActive),
                    tool.IsActive,
                    out var isActive))
            {
                tool.IsActive = isActive;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.ToolParameters),
                    tool.ToolParameters,
                    out string? toolParameters))
            {
                tool.ToolParameters = toolParameters;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.CostPerUnit),
                    tool.CostPerUnit,
                    out decimal? costPerUnit))
            {
                tool.CostPerUnit = costPerUnit;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.BillingUnit),
                    tool.BillingUnit,
                    out string? billingUnit))
            {
                if (billingUnit is not null)
                {
                    ValidateBillingUnit(billingUnit);
                }
                tool.BillingUnit = billingUnit;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.CostDescription),
                    tool.CostDescription,
                    out string? costDescription))
            {
                tool.CostDescription = costDescription;
            }
            tool.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            LogAdminAudit("Updated", "ProviderTool", id,
                $"ToolName: {LoggingSanitizer.S(tool.ToolName)}, Provider: {tool.Provider}");

            await PublishToolChangedEventAsync(tool, "Updated");

            return Results.Ok(ProviderToolDto.FromEntity(tool));
        }

        /// <summary>
        /// Deletes a provider tool.
        /// </summary>
        /// <param name="id">Tool ID</param>
        /// <returns>Success status</returns>
        public async Task<IResult> DeleteProviderTool(int id)
        {
            var tool = await _context.ProviderTools.FindAsync(id);
            if (tool == null)
            {
                throw new KeyNotFoundException($"Provider tool with ID '{id}' not found");
            }

            _context.ProviderTools.Remove(tool);
            await _context.SaveChangesAsync();

            LogAdminAudit("Deleted", "ProviderTool", id,
                $"ToolName: {LoggingSanitizer.S(tool.ToolName)}, Provider: {tool.Provider}");

            await PublishToolChangedEventAsync(tool, "Deleted");

            return Results.NoContent();
        }

        /// <summary>
        /// Gets available provider types that support tools.
        /// </summary>
        /// <returns>List of provider types with tool support</returns>
        public IResult GetToolProviders()
        {
            // Define which providers support tools
            var toolProviders = new[]
            {
                new ToolProviderDto { Value = (int)ProviderType.Groq, Name = "Groq", Description = "Supports code_interpreter, browser tools" },
                new ToolProviderDto { Value = (int)ProviderType.OpenAI, Name = "OpenAI", Description = "Function calling (client-side tools)" },
                new ToolProviderDto { Value = (int)ProviderType.Fireworks, Name = "Fireworks", Description = "May support tools" },
                new ToolProviderDto { Value = (int)ProviderType.OpenAICompatible, Name = "OpenAI Compatible", Description = "Depends on implementation" }
            };

            return Results.Ok(toolProviders);
        }

        /// <summary>
        /// Gets available billing units.
        /// </summary>
        /// <returns>List of billing unit options</returns>
        public IResult GetBillingUnits()
        {
            return Results.Ok(ProviderToolBillingUnits.All);
        }

        /// <summary>
        /// Bulk import provider tools from a JSON array.
        /// </summary>
        /// <param name="tools">Array of provider tools to import</param>
        /// <returns>Import results</returns>
        public async Task<IResult> ImportProviderTools(List<CreateProviderToolDto> tools)
        {
            var imported = 0;
            var skipped = 0;
            var errors = new List<string>();
            var affectedProviders = new HashSet<ProviderType>();

            foreach (var dto in tools)
            {
                try
                {
                    // Validate billing unit
                    if (!ProviderToolBillingUnits.IsValid(dto.BillingUnit))
                    {
                        errors.Add($"Tool '{dto.ToolName}': Invalid billing unit '{dto.BillingUnit}'. " +
                            $"Must be one of: {string.Join(", ", ProviderToolBillingUnits.All)}");
                        skipped++;
                        continue;
                    }

                    // Check if tool already exists
                    var exists = await _context.ProviderTools
                        .AnyAsync(pt => pt.Provider == dto.Provider && pt.ToolName == dto.ToolName);

                    if (exists)
                    {
                        skipped++;
                        errors.Add($"Tool '{dto.ToolName}' already exists for {dto.Provider}");
                        continue;
                    }

                    var tool = new ProviderTool
                    {
                        Provider = dto.Provider,
                        ToolName = dto.ToolName,
                        ToolParameters = dto.ToolParameters,
                        CostPerUnit = dto.CostPerUnit,
                        BillingUnit = dto.BillingUnit,
                        CostDescription = dto.CostDescription,
                        IsActive = dto.IsActive,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _context.ProviderTools.Add(tool);
                    imported++;
                    affectedProviders.Add(dto.Provider);
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to import {dto.ToolName}: {ex.Message}");
                }
            }

            if (imported > 0)
            {
                await _context.SaveChangesAsync();

                // Publish events for each affected provider
                foreach (var provider in affectedProviders)
                {
                    await PublishToolChangedEventAsync(provider, "BulkImport");
                }
            }

            LogAdminAudit("Imported", "ProviderTool",
                detail: $"Imported: {imported}, Skipped: {skipped}, Total: {tools.Count}");

            return Results.Ok(new ProviderToolImportResultDto
            {
                Imported = imported,
                Skipped = skipped,
                Total = tools.Count,
                Errors = errors.Count > 0 ? errors : null
            });
        }

        /// <summary>
        /// Exports all provider tools as JSON.
        /// </summary>
        /// <returns>JSON array of all provider tools</returns>
        public async Task<IResult> ExportProviderTools()
        {
            var tools = await _context.ProviderTools
                .OrderBy(pt => pt.Provider)
                .ThenBy(pt => pt.ToolName)
                .ToListAsync();

            var dtos = tools.Select(ProviderToolDto.FromEntity);

            _httpContextAccessor.HttpContext!.Response.Headers.Append("Content-Disposition", "attachment; filename=provider-tools.json");
            return Results.Ok(dtos);
        }

        /// <summary>
        /// Validates that the billing unit is a recognized value.
        /// </summary>
        private static void ValidateBillingUnit(string? billingUnit)
        {
            if (!ProviderToolBillingUnits.IsValid(billingUnit))
            {
                throw new ArgumentException(
                    $"Invalid billing unit '{billingUnit}'. Must be one of: {string.Join(", ", ProviderToolBillingUnits.All)}");
            }
        }

        /// <summary>
        /// Publishes a ProviderToolChanged event for cache invalidation.
        /// </summary>
        private async Task PublishToolChangedEventAsync(ProviderTool tool, string changeType)
        {
            if (_eventBus == null) return;

            try
            {
                await _eventBus.PublishAsync(new ProviderToolChanged
                {
                    ProviderToolId = tool.Id,
                    ToolName = tool.ToolName,
                    ProviderType = tool.Provider?.ToString() ?? "Unknown",
                    ChangeType = changeType,
                    CorrelationId = Guid.NewGuid().ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish ProviderToolChanged event for {ToolName} — operation completed but cache may be stale",
                    tool.ToolName);
            }
        }

        /// <summary>
        /// Publishes a ProviderToolChanged event for a provider type (bulk operations).
        /// </summary>
        private async Task PublishToolChangedEventAsync(ProviderType providerType, string changeType)
        {
            if (_eventBus == null) return;

            try
            {
                await _eventBus.PublishAsync(new ProviderToolChanged
                {
                    ProviderToolId = 0,
                    ToolName = "*",
                    ProviderType = providerType.ToString(),
                    ChangeType = changeType,
                    CorrelationId = Guid.NewGuid().ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish ProviderToolChanged event for {ProviderType} — operation completed but cache may be stale",
                    providerType);
            }
        }

        private void LogAdminAudit(string operation, string entityType, object? entityId = null, string? detail = null) =>
            AdminAudit.Log(_httpContextAccessor.HttpContext!, _logger, operation, entityType, entityId, detail);
    }
}
