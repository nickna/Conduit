using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Models.Models;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Functions.Utilities;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ConduitLLM.Admin.Endpoints
{
    public partial class ModelEndpoints
    {
        /// <summary>
        /// Gets model identifiers for a specific model
        /// </summary>
        /// <param name="id">The model ID</param>
        /// <returns>List of model identifiers showing which providers offer this model</returns>
        public async Task<IResult> GetModelIdentifiers(int id)
        {
            var model = await _modelRepository.GetByIdWithDetailsAsync(id);
            if (model == null)
            {
                return AdminResults.NotFoundEntity("Model", id);
            }

            var identifiers = model.Identifiers.Select(i => new ModelIdentifierDto
            {
                Id = i.Id,
                Identifier = i.Identifier,
                Provider = (int?)i.Provider,
                IsPrimary = i.IsPrimary,
                Metadata = StructuredJson.ParseObject(i.Metadata),
                MaxInputTokens = i.MaxInputTokens,
                MaxOutputTokens = i.MaxOutputTokens,
                SpeedScore = i.SpeedScore,
                QualityScore = i.QualityScore,
                ProviderVariation = i.ProviderVariation,
                ModelCostId = i.ModelCostId,
                InputModalities = ModelModalities.Parse(i.InputModalitiesJson),
                OutputModalities = ModelModalities.Parse(i.OutputModalitiesJson),
                OperationalCapabilities = ModelCapabilityResolver.DeserializeOverrides(i.OperationalCapabilitiesJson),
                CapabilitySource = i.CapabilitySource,
                CapabilitiesLastVerifiedAt = i.CapabilitiesLastVerifiedAt
            }).ToList();

            return Ok(identifiers);
        }

        /// <summary>
        /// Gets model associations with available providers
        /// Returns only associations where matching providers are configured
        /// </summary>
        /// <param name="id">The model ID</param>
        /// <returns>List of associations with their available providers</returns>
        public async Task<IResult> GetAvailableProviders(int id)
        {
            var model = await _modelRepository.GetByIdWithDetailsAsync(id);
            if (model == null)
            {
                return AdminResults.NotFoundEntity("Model", id);
            }

            var providers = await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
                _providerRepository.GetPaginatedAsync);
            var enabledProviders = providers.Where(p => p.IsEnabled).ToList();

            var result = new List<ModelProviderAvailabilityDto>();

            foreach (var association in model.Identifiers)
            {
                // Skip associations without a provider type - they're not properly configured
                if (association.Provider == null)
                {
                    Logger.LogWarning(
                        "ModelIdentifier {AssociationId} for model {ModelId} has null Provider field - skipping",
                        association.Id, id);
                    continue;
                }

                // Find matching providers for this association
                var matchingProviders = enabledProviders.Where(p =>
                    p.ProviderType == association.Provider
                ).ToList();

                if (matchingProviders.Any())
                {
                    result.Add(new ModelProviderAvailabilityDto
                    {
                        AssociationId = association.Id,
                        Identifier = association.Identifier,
                        Provider = (int?)association.Provider,
                        ProviderVariation = association.ProviderVariation,
                        MaxInputTokens = association.MaxInputTokens,
                        MaxOutputTokens = association.MaxOutputTokens,
                        SpeedScore = association.SpeedScore,
                        QualityScore = association.QualityScore,
                        IsPrimary = association.IsPrimary,
                        InputModalities = ModelModalities.Parse(association.InputModalitiesJson),
                        OutputModalities = ModelModalities.Parse(association.OutputModalitiesJson),
                        OperationalCapabilities =
                            ModelCapabilityResolver.DeserializeOverrides(association.OperationalCapabilitiesJson),
                        CapabilitySource = association.CapabilitySource,
                        CapabilitiesLastVerifiedAt = association.CapabilitiesLastVerifiedAt,
                        AvailableProviders = matchingProviders.Select(p => new AvailableProviderDto
                        {
                            ProviderId = p.Id,
                            ProviderName = p.ProviderName,
                            ProviderType = p.ProviderType.ToString()
                        }).ToList()
                    });
                }
            }

            return Ok(result);
        }

        /// <summary>
        /// Creates a new model identifier for a specific model
        /// </summary>
        /// <param name="id">The model ID</param>
        /// <param name="dto">The identifier data</param>
        /// <returns>The created identifier</returns>
        public async Task<IResult> CreateModelIdentifier(int id, ModelIdentifierRequestDto dto)
        {
            var model = await _modelRepository.GetByIdWithDetailsAsync(id);
            if (model == null)
            {
                return NotFound($"Model with ID {id} not found");
            }

            var invalidModalities = GetInvalidModalities(dto.InputModalities, dto.OutputModalities);
            if (invalidModalities.Length > 0)
                return BadRequest($"Unknown model modalities: {string.Join(", ", invalidModalities)}");

            // Parse provider if provided as integer
            ProviderType? providerType = dto.Provider.HasValue ? (ProviderType)dto.Provider.Value : null;

            // Check if identifier already exists for this provider
            var existing = model.Identifiers.FirstOrDefault(i =>
                i.Identifier == dto.Identifier &&
                i.Provider == providerType);

            if (existing != null)
            {
                return Conflict($"Identifier '{dto.Identifier}' already exists for provider '{dto.Provider}'");
            }

            var identifier = new ModelProviderTypeAssociation
            {
                ModelId = id,
                Identifier = dto.Identifier,
                Provider = providerType,
                IsPrimary = dto.IsPrimary ?? false,
                Metadata = dto.Metadata is null ? null : AdminJson.Serialize(dto.Metadata),
                MaxInputTokens = dto.MaxInputTokens,
                MaxOutputTokens = dto.MaxOutputTokens,
                SpeedScore = dto.SpeedScore,
                QualityScore = dto.QualityScore,
                ProviderVariation = dto.ProviderVariation,
                InputModalitiesJson = ModelModalities.Serialize(dto.InputModalities),
                OutputModalitiesJson = ModelModalities.Serialize(dto.OutputModalities),
                OperationalCapabilitiesJson = ModelCapabilityResolver.SerializeOverrides(dto.OperationalCapabilities),
                CapabilitySource = dto.CapabilitySource,
                CapabilitiesLastVerifiedAt = dto.CapabilitiesLastVerifiedAt
            };

            model.Identifiers.Add(identifier);
            await _modelRepository.UpdateModelAsync(model);
            await PublishIdentifierCapabilityChangeAsync(model, "Created");

            LogAdminAudit("Created", "ModelIdentifier", identifier.Id,
                $"ModelId: {id}, Identifier: {LoggingSanitizer.S(dto.Identifier)}");

            return Results.Created($"/v1/admin/models/{id}/identifiers", new ModelIdentifierDto
            {
                Id = identifier.Id,
                Identifier = identifier.Identifier,
                Provider = (int?)identifier.Provider,
                IsPrimary = identifier.IsPrimary,
                Metadata = StructuredJson.ParseObject(identifier.Metadata),
                MaxInputTokens = identifier.MaxInputTokens,
                MaxOutputTokens = identifier.MaxOutputTokens,
                SpeedScore = identifier.SpeedScore,
                QualityScore = identifier.QualityScore,
                ProviderVariation = identifier.ProviderVariation,
                ModelCostId = identifier.ModelCostId,
                InputModalities = ModelModalities.Parse(identifier.InputModalitiesJson),
                OutputModalities = ModelModalities.Parse(identifier.OutputModalitiesJson),
                OperationalCapabilities = ModelCapabilityResolver.DeserializeOverrides(identifier.OperationalCapabilitiesJson),
                CapabilitySource = identifier.CapabilitySource,
                CapabilitiesLastVerifiedAt = identifier.CapabilitiesLastVerifiedAt
            });
        }

        /// <summary>
        /// Updates a model identifier
        /// </summary>
        /// <param name="id">The model ID</param>
        /// <param name="identifierId">The identifier ID</param>
        /// <param name="dto">The updated identifier data</param>
        /// <returns>No content on success</returns>
        public async Task<IResult> UpdateModelIdentifier(int id, int identifierId, ModelIdentifierRequestDto dto)
        {
            var model = await _modelRepository.GetByIdWithDetailsAsync(id);
            if (model == null)
            {
                return NotFound($"Model with ID {id} not found");
            }

            var invalidModalities = GetInvalidModalities(dto.InputModalities, dto.OutputModalities);
            if (invalidModalities.Length > 0)
                return BadRequest($"Unknown model modalities: {string.Join(", ", invalidModalities)}");

            var identifier = model.Identifiers.FirstOrDefault(i => i.Id == identifierId);
            if (identifier == null)
            {
                return NotFound($"Identifier with ID {identifierId} not found for model {id}");
            }

            JsonMergePatchState.TryGetPatchedProperty(
                dto,
                nameof(dto.Identifier),
                identifier.Identifier,
                out var effectiveIdentifier);
            if (string.IsNullOrWhiteSpace(effectiveIdentifier))
            {
                throw new InvalidOperationException("identifier cannot be null or empty.");
            }
            JsonMergePatchState.TryGetPatchedProperty(
                dto,
                nameof(dto.Provider),
                (int?)identifier.Provider,
                out int? effectiveProvider);
            ProviderType? providerType = effectiveProvider.HasValue
                ? (ProviderType)effectiveProvider.Value
                : null;

            // Check if the new identifier/provider combo already exists (if changed)
            if (identifier.Identifier != effectiveIdentifier || identifier.Provider != providerType)
            {
                var existing = model.Identifiers.FirstOrDefault(i =>
                    i.Id != identifierId &&
                    i.Identifier == effectiveIdentifier &&
                    i.Provider == providerType);

                if (existing != null)
                {
                    return Conflict($"Identifier '{effectiveIdentifier}' already exists for provider '{effectiveProvider}'");
                }
            }

            identifier.Identifier = effectiveIdentifier;
            identifier.Provider = providerType;
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.IsPrimary),
                    identifier.IsPrimary,
                    out var isPrimary))
            {
                identifier.IsPrimary = isPrimary;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.Metadata),
                    StructuredJson.ParseObject(identifier.Metadata),
                    out Dictionary<string, JsonElement>? metadata))
            {
                identifier.Metadata = StructuredJson.SerializeObject(metadata);
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.MaxInputTokens),
                    identifier.MaxInputTokens,
                    out int? maxInputTokens))
            {
                identifier.MaxInputTokens = maxInputTokens;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.MaxOutputTokens),
                    identifier.MaxOutputTokens,
                    out int? maxOutputTokens))
            {
                identifier.MaxOutputTokens = maxOutputTokens;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.SpeedScore),
                    identifier.SpeedScore,
                    out decimal? speedScore))
            {
                identifier.SpeedScore = speedScore;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.QualityScore),
                    identifier.QualityScore,
                    out decimal? qualityScore))
            {
                identifier.QualityScore = qualityScore;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.ProviderVariation),
                    identifier.ProviderVariation,
                    out string? providerVariation))
            {
                identifier.ProviderVariation = providerVariation;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.InputModalities),
                    ModelModalities.Parse(identifier.InputModalitiesJson),
                    out IReadOnlyList<string>? inputModalities))
            {
                identifier.InputModalitiesJson = ModelModalities.Serialize(inputModalities);
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.OutputModalities),
                    ModelModalities.Parse(identifier.OutputModalitiesJson),
                    out IReadOnlyList<string>? outputModalities))
            {
                identifier.OutputModalitiesJson = ModelModalities.Serialize(outputModalities);
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.OperationalCapabilities),
                    ModelCapabilityResolver.DeserializeOverrides(identifier.OperationalCapabilitiesJson),
                    out ProviderOperationalCapabilities? operationalCapabilities))
            {
                identifier.OperationalCapabilitiesJson =
                    ModelCapabilityResolver.SerializeOverrides(operationalCapabilities);
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.CapabilitySource),
                    identifier.CapabilitySource,
                    out ModelCapabilitySource? capabilitySource))
            {
                identifier.CapabilitySource = capabilitySource;
            }
            if (JsonMergePatchState.TryGetPatchedProperty(
                    dto,
                    nameof(dto.CapabilitiesLastVerifiedAt),
                    identifier.CapabilitiesLastVerifiedAt,
                    out DateTime? capabilitiesLastVerifiedAt))
            {
                identifier.CapabilitiesLastVerifiedAt = capabilitiesLastVerifiedAt;
            }

            await _modelRepository.UpdateModelAsync(model);
            await PublishIdentifierCapabilityChangeAsync(model, "Updated");

            LogAdminAudit("Updated", "ModelIdentifier", identifierId,
                $"ModelId: {id}, Identifier: {LoggingSanitizer.S(identifier.Identifier)}");

            return Ok(new ModelIdentifierDto
            {
                Id = identifier.Id,
                Identifier = identifier.Identifier,
                Provider = (int?)identifier.Provider,
                IsPrimary = identifier.IsPrimary,
                Metadata = StructuredJson.ParseObject(identifier.Metadata),
                MaxInputTokens = identifier.MaxInputTokens,
                MaxOutputTokens = identifier.MaxOutputTokens,
                SpeedScore = identifier.SpeedScore,
                QualityScore = identifier.QualityScore,
                ProviderVariation = identifier.ProviderVariation,
                ModelCostId = identifier.ModelCostId,
                InputModalities = ModelModalities.Parse(identifier.InputModalitiesJson),
                OutputModalities = ModelModalities.Parse(identifier.OutputModalitiesJson),
                OperationalCapabilities = ModelCapabilityResolver.DeserializeOverrides(identifier.OperationalCapabilitiesJson),
                CapabilitySource = identifier.CapabilitySource,
                CapabilitiesLastVerifiedAt = identifier.CapabilitiesLastVerifiedAt
            });
        }

        /// <summary>
        /// Deletes a model identifier
        /// </summary>
        /// <param name="id">The model ID</param>
        /// <param name="identifierId">The identifier ID to delete</param>
        /// <returns>No content on success</returns>
        public async Task<IResult> DeleteModelIdentifier(int id, int identifierId)
        {
            var model = await _modelRepository.GetByIdAsync(id);
            if (model == null)
                return NotFound($"Model with ID {id} not found");

            // Directly delete the identifier from the repository
            var deleted = await _modelRepository.DeleteIdentifierAsync(id, identifierId);

            if (!deleted)
            {
                throw new KeyNotFoundException($"Identifier with ID {identifierId} not found for model {id}");
            }

            LogAdminAudit("Deleted", "ModelIdentifier", identifierId, $"ModelId: {id}");
            await PublishIdentifierCapabilityChangeAsync(model, "Deleted");

            return NoContent();
        }

        private async Task PublishIdentifierCapabilityChangeAsync(Model model, string changeType)
        {
            await _eventBus.PublishAsync(new ModelUpdated
            {
                ModelId = model.Id,
                ModelName = model.Name,
                ModelSeriesId = model.ModelSeriesId,
                ChangeType = $"Identifier{changeType}",
                ChangedProperties =
                [
                    "InputModalities",
                    "OutputModalities",
                    "OperationalCapabilities"
                ]
            });
        }
    }
}
