using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Models.Models;
using ConduitLLM.Admin.Models.ModelSeries;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Functions.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ConduitLLM.Admin.Endpoints
{
    /// <summary>
    /// Controller for managing canonical Model entities
    /// </summary>
    public partial class ModelEndpoints : AdminEndpointHandlerBase
    {
        private readonly IModelRepository _modelRepository;
        private readonly IModelSeriesRepository _modelSeriesRepository;
        private readonly IAdminModelProviderMappingService _mappingService;
        private readonly IProviderRepository _providerRepository;
        private readonly IEventBus _eventBus;

        /// <summary>
        /// Initializes the Model endpoint handler.
        /// </summary>
        public ModelEndpoints(
            IModelRepository modelRepository,
            IModelSeriesRepository modelSeriesRepository,
            IAdminModelProviderMappingService mappingService,
            IProviderRepository providerRepository,
            IEventBus eventBus,
            IHttpContextAccessor httpContextAccessor,
            ILogger<ModelEndpoints> logger)
            : base(null, httpContextAccessor, logger)
        {
            _modelRepository = modelRepository ?? throw new ArgumentNullException(nameof(modelRepository));
            _modelSeriesRepository = modelSeriesRepository ?? throw new ArgumentNullException(nameof(modelSeriesRepository));
            _mappingService = mappingService ?? throw new ArgumentNullException(nameof(mappingService));
            _providerRepository = providerRepository ?? throw new ArgumentNullException(nameof(providerRepository));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        public static IEndpointRouteBuilder MapModelEndpoints(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/v1/admin/models")
                .RequireAuthorization("MasterKeyPolicy")
                .AddEndpointFilter<OperationLoggingEndpointFilter>()
                .WithTags("Models");

            group.MapGet("/", ([FromServices] ModelEndpoints endpoints, string? search = null, string? capability = null, bool? hasProviders = null) =>
                    endpoints.GetAllModels(search, capability, hasProviders))
                .WithName("Model_GetAll").Produces<IEnumerable<ModelDto>>();
            group.MapGet("/paged", ([FromServices] ModelEndpoints endpoints, int page = 1, int pageSize = 50, string? search = null, string? capability = null, bool? hasProviders = null) =>
                    endpoints.GetPagedModels(page, pageSize, search, capability, hasProviders))
                .WithName("Model_GetPaged").Produces<PagedResult<ModelDto>>();
            group.MapGet("/{id:int}", ([FromServices] ModelEndpoints endpoints, int id) => endpoints.GetModelById(id))
                .WithName("Model_GetById").Produces<ModelDto>().Produces(StatusCodes.Status404NotFound);
            group.MapGet("/search", ([FromServices] ModelEndpoints endpoints, string? query = null) => endpoints.SearchModels(query))
                .WithName("Model_Search").Produces<IEnumerable<ModelDto>>();
            group.MapGet("/provider/models/{provider}", ([FromServices] ModelEndpoints endpoints, string provider) => endpoints.GetModelsByProvider(provider))
                .WithName("Model_GetByProvider").Produces<IEnumerable<ModelWithProviderIdDto>>().Produces(StatusCodes.Status400BadRequest);
            group.MapGet("/provider/{provider}", ([FromServices] ModelEndpoints endpoints, string provider) => endpoints.GetModelsByProvider(provider))
                .ExcludeFromDescription();
            group.MapPost("/", ([FromServices] ModelEndpoints endpoints, CreateModelDto dto) => endpoints.CreateModel(dto))
                .WithName("Model_Create").Produces<ModelDto>(StatusCodes.Status201Created).Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status409Conflict);
            group.MapPatch("/{id:int}", ([FromServices] ModelEndpoints endpoints, int id, JsonMergePatch<UpdateModelDto> patch) => endpoints.UpdateModel(id, patch.Value))
                .AcceptsJsonMergePatch<UpdateModelDto>()
                .WithName("Model_Update").Produces<ModelDto>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
            group.MapDelete("/{id:int}", ([FromServices] ModelEndpoints endpoints, int id) => endpoints.DeleteModel(id))
                .WithName("Model_Delete").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);

            group.MapGet("/{id:int}/identifiers", ([FromServices] ModelEndpoints endpoints, int id) => endpoints.GetModelIdentifiers(id))
                .WithName("Model_GetIdentifiers").Produces<IEnumerable<ModelIdentifierDto>>().Produces(StatusCodes.Status404NotFound);
            group.MapGet("/{id:int}/available-providers", ([FromServices] ModelEndpoints endpoints, int id) => endpoints.GetAvailableProviders(id))
                .WithName("Model_GetAvailableProviders").Produces<IEnumerable<ModelProviderAvailabilityDto>>().Produces(StatusCodes.Status404NotFound);
            group.MapPost("/{id:int}/identifiers", ([FromServices] ModelEndpoints endpoints, int id, ModelIdentifierRequestDto dto) => endpoints.CreateModelIdentifier(id, dto))
                .WithName("Model_CreateIdentifier").Produces<ModelIdentifierDto>(StatusCodes.Status201Created).Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
            group.MapPatch("/{id:int}/identifiers/{identifierId:int}", ([FromServices] ModelEndpoints endpoints, int id, int identifierId, JsonMergePatch<ModelIdentifierRequestDto> patch) => endpoints.UpdateModelIdentifier(id, identifierId, patch.Value))
                .AcceptsJsonMergePatch<ModelIdentifierRequestDto>()
                .WithName("Model_UpdateIdentifier").Produces<ModelIdentifierDto>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
            group.MapDelete("/{id:int}/identifiers/{identifierId:int}", ([FromServices] ModelEndpoints endpoints, int id, int identifierId) => endpoints.DeleteModelIdentifier(id, identifierId))
                .WithName("Model_DeleteIdentifier").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

            group.MapGet("/{id:int}/provider-mappings", ([FromServices] ModelEndpoints endpoints, int id) => endpoints.GetModelProviderMappings(id))
                .WithName("Model_GetProviderMappings").Produces<IEnumerable<ModelProviderMappingDto>>().Produces(StatusCodes.Status404NotFound);
            group.MapPost("/{id:int}/provider-mappings", ([FromServices] ModelEndpoints endpoints, int id, ModelProviderMappingDto dto) => endpoints.CreateModelProviderMapping(id, dto))
                .WithName("Model_CreateProviderMapping").Produces<ModelProviderMappingDto>(StatusCodes.Status201Created).Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
            group.MapPatch("/{id:int}/provider-mappings/{mappingId:int}", ([FromServices] ModelEndpoints endpoints, int id, int mappingId, JsonMergePatch<UpdateModelProviderMappingDto> patch) => endpoints.UpdateModelProviderMapping(id, mappingId, patch.Value))
                .AcceptsJsonMergePatch<UpdateModelProviderMappingDto>()
                .WithName("Model_UpdateProviderMapping").Produces<ModelProviderMappingDto>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound);
            group.MapDelete("/{id:int}/provider-mappings/{mappingId:int}", ([FromServices] ModelEndpoints endpoints, int id, int mappingId) => endpoints.DeleteModelProviderMapping(id, mappingId))
                .WithName("Model_DeleteProviderMapping").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
            return app;
        }

        /// <summary>
        /// Gets all models with their capabilities.
        /// Supports optional search and filtering.
        /// </summary>
        /// <param name="search">Optional search term for model name (case-insensitive partial match)</param>
        /// <param name="capability">Optional operation or directional modality filter.</param>
        /// <param name="hasProviders">Optional filter: true = only models with identifiers, false = without</param>
        /// <returns>List of all matching models</returns>
        public async Task<IResult> GetAllModels(
            [FromQuery] string? search = null,
            [FromQuery] string? capability = null,
            [FromQuery] bool? hasProviders = null)
        {
            var (models, _) = await _modelRepository.GetPaginatedWithFilterAsync(
                null, null, search, capability, hasProviders);

            var dtos = models.Select(m => m.ToDto()).ToList();
            return Ok(dtos);
        }

        /// <summary>
        /// Gets a paginated page of models with optional search and filtering.
        /// </summary>
        /// <param name="page">Page number (1-based).</param>
        /// <param name="pageSize">Items per page (max 100).</param>
        /// <param name="search">Optional search term for model name (case-insensitive partial match)</param>
        /// <param name="capability">Optional operation or directional modality filter.</param>
        /// <param name="hasProviders">Optional filter: true = only models with identifiers, false = without</param>
        /// <returns>A paginated result of matching models</returns>
        public async Task<IResult> GetPagedModels(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? search = null,
            [FromQuery] string? capability = null,
            [FromQuery] bool? hasProviders = null)
        {
            (page, pageSize) = Pagination.Normalize(page, pageSize);

            var (models, totalCount) = await _modelRepository.GetPaginatedWithFilterAsync(
                page, pageSize, search, capability, hasProviders);

            return Ok(new PagedResult<ModelDto>
            {
                Data = models.Select(m => m.ToDto()).ToList(),
                Pagination = PaginationMetadata.Create(page, pageSize, totalCount)
            });
        }

        /// <summary>
        /// Gets a specific model by ID
        /// </summary>
        /// <param name="id">The model ID</param>
        /// <returns>The model with its capabilities</returns>
        public async Task<IResult> GetModelById(int id)
        {
            var model = await _modelRepository.GetByIdWithDetailsAsync(id);
            if (model == null)
            {
                return AdminResults.NotFoundEntity("Model", id);
            }

            return Ok(model.ToDto());
        }


        /// <summary>
        /// Searches for models by name
        /// </summary>
        /// <param name="query">The search query</param>
        /// <returns>List of matching models</returns>
        public async Task<IResult> SearchModels(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Ok(new List<ModelDto>());
            }

            var models = await _modelRepository.SearchByNameAsync(query);
            return Ok(models.Select(m => m.ToDto()));
        }

        /// <summary>
        /// Gets models available from a specific provider
        /// </summary>
        /// <param name="provider">The provider name (e.g., "groq", "openai", "anthropic")</param>
        /// <returns>List of models available from the provider</returns>
        public async Task<IResult> GetModelsByProvider(string provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                return BadRequest("Provider name is required");
            }

            // Parse provider string to enum
            if (!Enum.TryParse<ProviderType>(provider, ignoreCase: true, out var providerType) ||
                !ProviderTypeCatalog.IsConfigurable(providerType))
            {
                var validProviders = ProviderTypeCatalog.ConfigurableTypes
                    .Select(providerType => providerType.ToString().ToLowerInvariant());
                return BadRequest($"Invalid provider '{provider}'. Valid providers: {string.Join(", ", validProviders)}");
            }

            var models = await _modelRepository.GetByProviderAsync(providerType);
            return Ok(models.Select(m =>
            {
                // Repository already handles the provider string to enum conversion
                // Just get the first identifier for this model (they're already filtered by provider)
                var providerIdentifier = m.Identifiers?.FirstOrDefault()?.Identifier
                    ?? m.Name; // Fallback to model name if no specific identifier

                // Use MapToDto to get base DTO, then create extended DTO
                var baseDto = m.ToDto();
                return new ModelWithProviderIdDto
                {
                    Id = baseDto.Id,
                    Name = baseDto.Name,
                    ProviderModelId = providerIdentifier,
                    ModelSeriesId = baseDto.ModelSeriesId,
                    IsActive = baseDto.IsActive,
                    CreatedAt = baseDto.CreatedAt,
                    UpdatedAt = baseDto.UpdatedAt,
                    Series = baseDto.Series,
                    ModelParameters = baseDto.ModelParameters,
                    // Copy capability fields
                    InputModalities = baseDto.InputModalities,
                    OutputModalities = baseDto.OutputModalities,
                    CapabilitySource = baseDto.CapabilitySource,
                    CapabilitiesLastVerifiedAt = baseDto.CapabilitiesLastVerifiedAt,
                    SupportsImageInput = baseDto.SupportsImageInput,
                    SupportsVideoInput = baseDto.SupportsVideoInput,
                    SupportsAudioInput = baseDto.SupportsAudioInput,
                    SupportsFileInput = baseDto.SupportsFileInput,
                    SupportsVideoUnderstanding = baseDto.SupportsVideoUnderstanding,
                    SupportsChat = baseDto.SupportsChat,
                    SupportsVision = baseDto.SupportsVision,
                    SupportsFunctionCalling = baseDto.SupportsFunctionCalling,
                    SupportsStreaming = baseDto.SupportsStreaming,
                    SupportsImageGeneration = baseDto.SupportsImageGeneration,
                    SupportsVideoGeneration = baseDto.SupportsVideoGeneration,
                    SupportsSpeechToText = baseDto.SupportsSpeechToText,
                    SupportsTextToSpeech = baseDto.SupportsTextToSpeech,
                    SupportsRerank = baseDto.SupportsRerank,
                    SupportsEmbeddings = baseDto.SupportsEmbeddings,
                    MaxInputTokens = baseDto.MaxInputTokens,
                    MaxOutputTokens = baseDto.MaxOutputTokens,
                    TokenizerType = baseDto.TokenizerType
                };
            }));
        }

        /// <summary>
        /// Creates a new model
        /// </summary>
        /// <param name="dto">The model to create</param>
        /// <returns>The created model</returns>
        public async Task<IResult> CreateModel(CreateModelDto dto)
        {
            if (dto == null)
            {
                return BadRequest("Model data is required");
            }

            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                return BadRequest("Model name is required");
            }

            var invalidModalities = GetInvalidModalities(dto.InputModalities, dto.OutputModalities);
            if (invalidModalities.Length > 0)
            {
                return BadRequest($"Unknown model modalities: {string.Join(", ", invalidModalities)}");
            }

            // Check if a model with the same name already exists
            var existing = await _modelRepository.GetByNameAsync(dto.Name);
            if (existing != null)
            {
                return Conflict($"A model with name '{dto.Name}' already exists");
            }

            if (!await _modelSeriesRepository.ExistsAsync(dto.ModelSeriesId))
            {
                return BadRequest($"Model series with ID {dto.ModelSeriesId} does not exist");
            }

            var model = new Model
            {
                Name = dto.Name,
                ModelSeriesId = dto.ModelSeriesId,
                ModelParameters = dto.ModelParameters is null ? null : AdminJson.Serialize(dto.ModelParameters),
                InputModalitiesJson = ModelModalities.Serialize(dto.InputModalities),
                OutputModalitiesJson = ModelModalities.Serialize(dto.OutputModalities),
                CapabilitySource = dto.CapabilitySource ??
                    (dto.InputModalities is null && dto.OutputModalities is null
                        ? ModelCapabilitySource.LegacyInferred
                        : ModelCapabilitySource.Manual),
                CapabilitiesLastVerifiedAt = dto.CapabilitiesLastVerifiedAt,
                IsActive = dto.IsActive ?? true,
                // Set capability fields directly
                SupportsChat = dto.SupportsChat,
                SupportsVision = dto.SupportsVision,
                SupportsFunctionCalling = dto.SupportsFunctionCalling,
                SupportsStreaming = dto.SupportsStreaming,
                SupportsImageGeneration = dto.SupportsImageGeneration,
                SupportsVideoGeneration = dto.SupportsVideoGeneration,
                SupportsSpeechToText = dto.SupportsSpeechToText,
                SupportsTextToSpeech = dto.SupportsTextToSpeech,
                SupportsRerank = dto.SupportsRerank,
                SupportsEmbeddings = dto.SupportsEmbeddings,
                MaxInputTokens = dto.MaxInputTokens,
                MaxOutputTokens = dto.MaxOutputTokens,
                TokenizerType = dto.TokenizerType,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _modelRepository.CreateModelAsync(model);

            // Reload with capabilities
            model = await _modelRepository.GetByIdWithDetailsAsync(model.Id);
            if (model == null)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Failed to reload created model");
            }

            LogAdminAudit("Created", "Model", model.Id, $"Name: {LoggingSanitizer.S(model.Name)}");
            AdminOperationsMetricsService.RecordConfigurationChange("model", "create");

            return Results.Created($"/v1/admin/models/{model.Id}", model.ToDto());
        }

        /// <summary>
        /// Updates an existing model
        /// </summary>
        /// <param name="id">The model ID</param>
        /// <param name="dto">The updated model data</param>
        /// <returns>No content on success</returns>
        public async Task<IResult> UpdateModel(int id, UpdateModelDto dto)
        {
            if (dto == null)
            {
                return BadRequest("Update data is required");
            }

            var invalidModalities = GetInvalidModalities(dto.InputModalities, dto.OutputModalities);
            if (invalidModalities.Length > 0)
            {
                return BadRequest($"Unknown model modalities: {string.Join(", ", invalidModalities)}");
            }

            var model = await _modelRepository.GetByIdWithDetailsAsync(id);
            if (model == null)
            {
                return NotFound($"Model with ID {id} not found");
            }

            // Capture pre-state for change tracking
            var changes = new List<(string Property, string? OldValue, string? NewValue)>();

            // Check for name conflicts if name is being changed.
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.Name),
                    model.Name,
                    out var name)
                && name != model.Name)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("name cannot be null or empty.");
                }
                var existing = await _modelRepository.GetByNameAsync(name);
                if (existing != null && existing.Id != id)
                {
                    return Conflict($"A model with name '{name}' already exists");
                }
                changes.Add(("Name", model.Name, name));
                model.Name = name;
            }

            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.ModelSeriesId),
                    model.ModelSeriesId,
                    out var modelSeriesId))
            {
                if (!await _modelSeriesRepository.ExistsAsync(modelSeriesId))
                {
                    return BadRequest($"Model series with ID {modelSeriesId} does not exist");
                }
                if (model.ModelSeriesId != modelSeriesId)
                {
                    changes.Add(("ModelSeriesId", model.ModelSeriesId.ToString(), modelSeriesId.ToString()));
                    // Clear the loaded navigation so EF repoints by FK instead of the old graph.
                    model.Series = null!;
                }
                model.ModelSeriesId = modelSeriesId;
            }

            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.IsActive),
                    model.IsActive,
                    out var isActive))
            {
                SetWithChangeTracking(nameof(model.IsActive), model.IsActive, isActive, value => model.IsActive = value, changes);
            }

            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.ModelParameters),
                    StructuredJson.ParseObject(model.ModelParameters),
                    out Dictionary<string, JsonElement>? modelParameters))
            {
                var newParams = modelParameters is null or { Count: 0 }
                    ? null
                    : AdminJson.Serialize(modelParameters);
                if (model.ModelParameters != newParams)
                    changes.Add(("ModelParameters", model.ModelParameters ?? "null", newParams ?? "null"));
                model.ModelParameters = newParams;
            }

            var clearDirectionalCapabilities = JsonMergePatchState.TryGetPatchedProperty(
                dto,
                nameof(dto.ClearDirectionalCapabilities),
                false,
                out var clearDirectional) && clearDirectional;
            var inputModalitiesPatched = JsonMergePatchState.IsDefined(dto, nameof(dto.InputModalities));
            var outputModalitiesPatched = JsonMergePatchState.IsDefined(dto, nameof(dto.OutputModalities));

            if (clearDirectionalCapabilities)
            {
                changes.Add(("DirectionalCapabilities", "configured", "unknown"));
                model.InputModalitiesJson = null;
                model.OutputModalitiesJson = null;
                model.CapabilitySource = ModelCapabilitySource.Unknown;
                model.CapabilitiesLastVerifiedAt = null;
            }
            else if (JsonMergePatchState.TryGetPatchedProperty(
                         dto,
                         nameof(dto.InputModalities),
                         ModelModalities.Parse(model.InputModalitiesJson),
                         out IReadOnlyList<string>? inputModalities))
            {
                var serialized = ModelModalities.Serialize(inputModalities);
                if (model.InputModalitiesJson != serialized)
                    changes.Add(("InputModalities", model.InputModalitiesJson ?? "unknown", serialized ?? "unknown"));
                model.InputModalitiesJson = serialized;
            }
            if (!clearDirectionalCapabilities
                && JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.OutputModalities),
                    ModelModalities.Parse(model.OutputModalitiesJson),
                    out IReadOnlyList<string>? outputModalities))
            {
                var serialized = ModelModalities.Serialize(outputModalities);
                if (model.OutputModalitiesJson != serialized)
                    changes.Add(("OutputModalities", model.OutputModalitiesJson ?? "unknown", serialized ?? "unknown"));
                model.OutputModalitiesJson = serialized;
            }
            if (!clearDirectionalCapabilities
                && JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.CapabilitySource),
                    model.CapabilitySource,
                    out var capabilitySource))
            {
                SetWithChangeTracking(
                    nameof(model.CapabilitySource),
                    model.CapabilitySource,
                    capabilitySource,
                    value => model.CapabilitySource = value,
                    changes);
            }
            else if (!clearDirectionalCapabilities && (inputModalitiesPatched || outputModalitiesPatched))
            {
                model.CapabilitySource = ModelCapabilitySource.Manual;
            }
            if (!clearDirectionalCapabilities
                && JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.CapabilitiesLastVerifiedAt),
                    model.CapabilitiesLastVerifiedAt,
                    out DateTime? capabilitiesLastVerifiedAt))
            {
                model.CapabilitiesLastVerifiedAt = capabilitiesLastVerifiedAt;
            }

            // Update capability fields with change tracking
            ApplyBooleanPatch(dto, nameof(dto.SupportsChat), model.SupportsChat, value => model.SupportsChat = value, changes);
            ApplyBooleanPatch(dto, nameof(dto.SupportsVision), model.SupportsVision, value => model.SupportsVision = value, changes);
            ApplyBooleanPatch(dto, nameof(dto.SupportsFunctionCalling), model.SupportsFunctionCalling, value => model.SupportsFunctionCalling = value, changes);
            ApplyBooleanPatch(dto, nameof(dto.SupportsStreaming), model.SupportsStreaming, value => model.SupportsStreaming = value, changes);
            ApplyBooleanPatch(dto, nameof(dto.SupportsImageGeneration), model.SupportsImageGeneration, value => model.SupportsImageGeneration = value, changes);
            ApplyBooleanPatch(dto, nameof(dto.SupportsVideoGeneration), model.SupportsVideoGeneration, value => model.SupportsVideoGeneration = value, changes);
            ApplyBooleanPatch(dto, nameof(dto.SupportsSpeechToText), model.SupportsSpeechToText, value => model.SupportsSpeechToText = value, changes);
            ApplyBooleanPatch(dto, nameof(dto.SupportsTextToSpeech), model.SupportsTextToSpeech, value => model.SupportsTextToSpeech = value, changes);
            ApplyBooleanPatch(dto, nameof(dto.SupportsRerank), model.SupportsRerank, value => model.SupportsRerank = value, changes);
            ApplyBooleanPatch(dto, nameof(dto.SupportsEmbeddings), model.SupportsEmbeddings, value => model.SupportsEmbeddings = value, changes);

            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.MaxInputTokens),
                    model.MaxInputTokens,
                    out int? maxInputTokens))
            {
                SetWithChangeTracking(nameof(model.MaxInputTokens), model.MaxInputTokens, maxInputTokens, value => model.MaxInputTokens = value, changes);
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.MaxOutputTokens),
                    model.MaxOutputTokens,
                    out int? maxOutputTokens))
            {
                SetWithChangeTracking(nameof(model.MaxOutputTokens), model.MaxOutputTokens, maxOutputTokens, value => model.MaxOutputTokens = value, changes);
            }

            model.UpdatedAt = DateTime.UtcNow;

            // Track if parameters were changed
            bool parametersChanged = JsonMergePatchState.IsDefined(dto, nameof(dto.ModelParameters));

            var updatedModel = await _modelRepository.UpdateModelAsync(model);

            // Publish ModelUpdated event for cache invalidation
            var changedPropertyNames = changes.Count > 0
                ? changes.Select(c => c.Property).ToArray()
                : GetChangedProperties(dto);

            await _eventBus.PublishAsync(new ModelUpdated
            {
                ModelId = updatedModel.Id,
                ModelName = updatedModel.Name,
                ModelSeriesId = updatedModel.ModelSeriesId,
                ChangeType = "Updated",
                ParametersChanged = parametersChanged,
                ChangedProperties = changedPropertyNames
            });

            if (changes.Count > 0)
            {
                LogAdminAuditWithChanges("Model", updatedModel.Id, changes,
                    $"Name: {LoggingSanitizer.S(updatedModel.Name)}");
            }
            else
            {
                LogAdminAudit("Updated", "Model", updatedModel.Id,
                    $"Name: {LoggingSanitizer.S(updatedModel.Name)}, no value changes detected");
            }
            AdminOperationsMetricsService.RecordConfigurationChange("model", "update");

            return Ok(updatedModel.ToDto());
        }

        /// <summary>
        /// Deletes a model
        /// </summary>
        /// <param name="id">The model ID</param>
        /// <returns>No content on success</returns>
        public async Task<IResult> DeleteModel(int id)
        {
            var model = await _modelRepository.GetByIdAsync(id);
            if (model == null)
            {
                return NotFound($"Model with ID {id} not found");
            }

            // Check if model is referenced by any mappings
            var hasReferences = await _modelRepository.HasMappingReferencesAsync(id);
            if (hasReferences)
            {
                return Conflict("Cannot delete model that is referenced by model provider mappings");
            }

            await _modelRepository.DeleteAsync(id);

            LogAdminAudit("Deleted", "Model", id);
            AdminOperationsMetricsService.RecordConfigurationChange("model", "delete");

            return NoContent();
        }

        /// <summary>
        /// Helper method to get list of changed properties from DTO
        /// </summary>
        private static string[] GetChangedProperties(UpdateModelDto dto)
        {
            string[] properties =
            [
                nameof(dto.Name), nameof(dto.ModelSeriesId), nameof(dto.InputModalities),
                nameof(dto.OutputModalities), nameof(dto.CapabilitySource),
                nameof(dto.CapabilitiesLastVerifiedAt), nameof(dto.ClearDirectionalCapabilities),
                nameof(dto.SupportsChat), nameof(dto.SupportsVision),
                nameof(dto.SupportsFunctionCalling), nameof(dto.SupportsStreaming),
                nameof(dto.SupportsImageGeneration), nameof(dto.SupportsVideoGeneration),
                nameof(dto.SupportsSpeechToText), nameof(dto.SupportsTextToSpeech),
                nameof(dto.SupportsRerank), nameof(dto.SupportsEmbeddings),
                nameof(dto.MaxInputTokens), nameof(dto.MaxOutputTokens),
                nameof(dto.IsActive), nameof(dto.ModelParameters)
            ];
            return properties.Where(property => JsonMergePatchState.IsDefined(dto, property)).ToArray();
        }

        private static void ApplyBooleanPatch(
            UpdateModelDto dto,
            string propertyName,
            bool currentValue,
            Action<bool> setter,
            List<(string Property, string? OldValue, string? NewValue)> changes)
        {
            if (JsonMergePatchState.TryGetPatchedProperty(dto, propertyName, currentValue, out var patchedValue))
            {
                SetWithChangeTracking(propertyName, currentValue, patchedValue, setter, changes);
            }
        }

        private static void SetWithChangeTracking<T>(
            string propertyName,
            T currentValue,
            T patchedValue,
            Action<T> setter,
            List<(string Property, string? OldValue, string? NewValue)> changes)
        {
            if (!EqualityComparer<T>.Default.Equals(currentValue, patchedValue))
            {
                changes.Add((
                    propertyName,
                    currentValue?.ToString() ?? "null",
                    patchedValue?.ToString() ?? "null"));
            }
            setter(patchedValue);
        }

        private static string[] GetInvalidModalities(
            IEnumerable<string>? inputModalities,
            IEnumerable<string>? outputModalities) =>
            (inputModalities ?? [])
                .Concat(outputModalities ?? [])
                .Where(value => string.IsNullOrWhiteSpace(value) || !ModelModalities.IsKnown(value.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }
}
