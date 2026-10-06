using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Models;
using System.Text.Json;
using ConduitLLM.Configuration.Serialization;

namespace ConduitLLM.Configuration.Extensions
{
    /// <summary>
    /// Extension methods for converting between Provider-related entities and DTOs
    /// </summary>
    public static class ProviderMappingExtensions
    {
        /// <summary>
        /// Converts a Provider entity to a ProviderReferenceDto
        /// </summary>
        public static ProviderReferenceDto ToReferenceDto(this Provider provider)
        {
            return new ProviderReferenceDto
            {
                Id = provider.Id,
                ProviderType = provider.ProviderType,
                DisplayName = provider.ProviderName,
                IsEnabled = provider.IsEnabled
            };
        }

        /// <summary>
        /// Converts a ModelProviderMapping entity to a ModelProviderMappingDto
        /// </summary>
        public static ModelProviderMappingDto ToDto(this ModelProviderMapping mapping)
        {
            var association = mapping.ModelProviderTypeAssociation;
            var capabilities = association?.Model is null
                ? null
                : ModelCapabilityResolver.Resolve(association.Model, association);

            return new ModelProviderMappingDto
            {
                Id = mapping.Id,
                ModelAlias = mapping.ModelAlias,
                ProviderModelId = mapping.ProviderModelId,
                ProviderId = mapping.ProviderId,
                Provider = mapping.Provider?.ToReferenceDto(),
                ModelProviderTypeAssociationId = mapping.ModelProviderTypeAssociationId,
                Priority = mapping.RoutingPriority,
                Weight = mapping.RoutingWeight,
                IsEnabled = mapping.IsEnabled,
                CreatedAt = mapping.CreatedAt,
                UpdatedAt = mapping.UpdatedAt,
                ProviderOptions = ParseOptions(mapping.ProviderOptions),
                Capabilities = capabilities
            };
        }

        /// <summary>
        /// Updates a ModelProviderMapping entity from a ModelProviderMappingDto
        /// </summary>
        public static void UpdateFromDto(this ModelProviderMapping mapping, ModelProviderMappingDto dto)
        {
            mapping.ModelAlias = dto.ModelAlias;
            mapping.ProviderModelId = dto.ProviderModelId;
            mapping.ProviderId = dto.ProviderId;
            mapping.ModelProviderTypeAssociationId = dto.ModelProviderTypeAssociationId;
            mapping.IsEnabled = dto.IsEnabled;
            mapping.ProviderOptions = SerializeOptions(dto.ProviderOptions);
            mapping.RoutingPriority = dto.Priority;
            mapping.RoutingWeight = dto.Weight;
            mapping.UpdatedAt = System.DateTime.UtcNow;
        }

        public static void UpdateFromDto(this ModelProviderMapping mapping, UpdateModelProviderMappingDto dto)
        {
            if (dto.ModelAlias is not null) mapping.ModelAlias = dto.ModelAlias;
            if (dto.ProviderModelId is not null) mapping.ProviderModelId = dto.ProviderModelId;
            if (dto.ProviderId.HasValue) mapping.ProviderId = dto.ProviderId.Value;
            if (dto.ModelProviderTypeAssociationId.HasValue)
                mapping.ModelProviderTypeAssociationId = dto.ModelProviderTypeAssociationId.Value;
            if (dto.IsEnabled.HasValue) mapping.IsEnabled = dto.IsEnabled.Value;
            if (dto.ProviderOptions is not null) mapping.ProviderOptions = SerializeOptions(dto.ProviderOptions);
            if (dto.Priority.HasValue) mapping.RoutingPriority = dto.Priority.Value;
            if (dto.Weight.HasValue) mapping.RoutingWeight = dto.Weight.Value;
            mapping.UpdatedAt = System.DateTime.UtcNow;
        }

        /// <summary>
        /// Creates a new ModelProviderMapping entity from a ModelProviderMappingDto
        /// </summary>
        public static ModelProviderMapping ToEntity(this ModelProviderMappingDto dto)
        {
            var mapping = new ModelProviderMapping();
            mapping.UpdateFromDto(dto);
            mapping.Id = 0; // Reset ID for new entities
            mapping.CreatedAt = System.DateTime.UtcNow;
            return mapping;
        }

        public static ModelProviderMapping ToEntity(this CreateModelProviderMappingDto dto)
        {
            return new ModelProviderMapping
            {
                ModelAlias = dto.ModelAlias,
                ProviderModelId = dto.ProviderModelId,
                ProviderId = dto.ProviderId,
                ModelProviderTypeAssociationId = dto.ModelProviderTypeAssociationId,
                IsEnabled = dto.IsEnabled,
                ProviderOptions = SerializeOptions(dto.ProviderOptions),
                RoutingPriority = dto.Priority,
                RoutingWeight = dto.Weight,
                CreatedAt = System.DateTime.UtcNow,
                UpdatedAt = System.DateTime.UtcNow
            };
        }

        private static string? SerializeOptions(Dictionary<string, JsonElement>? options) =>
            options is null
                ? null
                : JsonSerializer.Serialize(
                    options,
                    ConfigurationJsonContext.Default.DictionaryStringJsonElement);

        private static Dictionary<string, JsonElement>? ParseOptions(string? options)
        {
            if (string.IsNullOrWhiteSpace(options))
                return null;
            try
            {
                return JsonSerializer.Deserialize(
                    options,
                    ConfigurationJsonContext.Default.DictionaryStringJsonElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
