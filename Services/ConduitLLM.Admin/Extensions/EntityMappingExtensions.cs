using ConduitLLM.Admin.Models.ModelAuthors;
using ConduitLLM.Admin.Models.Models;
using ConduitLLM.Admin.Models.ModelSeries;
using ConduitLLM.Configuration.DTOs.IpFilter;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Functions.DTOs;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Extensions;
using ConduitLLM.Functions.Utilities;

namespace ConduitLLM.Admin.Extensions
{
    /// <summary>
    /// Extension methods for converting entities to their DTO representations
    /// </summary>
    public static class EntityMappingExtensions
    {
        /// <summary>
        /// Maps an IpFilterEntity to an IpFilterDto
        /// </summary>
        public static IpFilterDto ToDto(this IpFilterEntity entity)
        {
            return new IpFilterDto
            {
                Id = entity.Id,
                FilterType = entity.FilterType,
                IpAddressOrCidr = entity.IpAddressOrCidr,
                Name = entity.Name ?? string.Empty,
                Description = entity.Description,
                IsEnabled = entity.IsEnabled,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                ResourceETag = new ConduitLLM.Configuration.Utilities.ResourceVersion(entity.RowVersion, entity.UpdatedAt).ETag,
                CreatedBy = entity.CreatedBy,
                UpdatedBy = entity.UpdatedBy,
                VirtualKeyId = entity.VirtualKeyId
            };
        }

        /// <summary>
        /// Maps a ModelSeries entity to a ModelSeriesDto
        /// </summary>
        public static ModelSeriesDto ToDto(this ModelSeries series)
        {
            return new ModelSeriesDto
            {
                Id = series.Id,
                AuthorId = series.AuthorId,
                AuthorName = series.Author?.Name,
                Name = series.Name,
                Description = series.Description,
                TokenizerType = series.TokenizerType,
                Parameters = StructuredJson.ParseObject(series.Parameters) ?? new()
            };
        }

        /// <summary>
        /// Maps a Model entity to a ModelDto
        /// </summary>
        public static ModelDto ToDto(this Model model)
        {
            var capabilities = ModelCapabilityResolver.Resolve(model);
            return new ModelDto
            {
                Id = model.Id,
                Name = model.Name,
                ModelSeriesId = model.ModelSeriesId,
                IsActive = model.IsActive,
                CreatedAt = model.CreatedAt,
                UpdatedAt = model.UpdatedAt,
                Series = model.Series?.ToDto(),
                ModelParameters = StructuredJson.ParseObject(model.ModelParameters),
                InputModalities = capabilities.InputModalities,
                OutputModalities = capabilities.OutputModalities,
                CapabilitySource = capabilities.CapabilitySource,
                CapabilitiesLastVerifiedAt = capabilities.CapabilitiesLastVerifiedAt,
                SupportsImageInput = capabilities.SupportsImageInput,
                SupportsVideoInput = capabilities.SupportsVideoInput,
                SupportsAudioInput = capabilities.SupportsAudioInput,
                SupportsFileInput = capabilities.SupportsFileInput,
                SupportsVideoUnderstanding = capabilities.SupportsVideoUnderstanding,
                SupportsChat = model.SupportsChat,
                SupportsVision = model.SupportsVision,
                SupportsImageGeneration = model.SupportsImageGeneration,
                SupportsVideoGeneration = model.SupportsVideoGeneration,
                SupportsSpeechToText = model.SupportsSpeechToText,
                SupportsTextToSpeech = model.SupportsTextToSpeech,
                SupportsRerank = model.SupportsRerank,
                SupportsEmbeddings = model.SupportsEmbeddings,
                SupportsFunctionCalling = model.SupportsFunctionCalling,
                SupportsStreaming = model.SupportsStreaming,
                MaxInputTokens = model.MaxInputTokens,
                MaxOutputTokens = model.MaxOutputTokens,
                TokenizerType = model.TokenizerType,
                Identifiers = model.Identifiers?.Select(i => new ModelIdentifierDto
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
                }).ToList()
            };
        }

        /// <summary>
        /// Maps a ModelAuthor entity to a ModelAuthorDto
        /// </summary>
        public static ModelAuthorDto ToDto(this ModelAuthor author)
        {
            return new ModelAuthorDto
            {
                Id = author.Id,
                Name = author.Name,
                Description = author.Description,
                WebsiteUrl = author.WebsiteUrl
            };
        }

        /// <summary>
        /// Maps a FunctionExecution entity to its Admin execution resource.
        /// </summary>
        public static AdminFunctionExecutionDto ToDto(this FunctionExecution entity) => entity.ToAdminDto();

        /// <summary>
        /// Maps a FunctionCost entity to a FunctionCostDto
        /// </summary>
        public static FunctionCostDto ToDto(this FunctionCost entity)
        {
            return new FunctionCostDto
            {
                Id = entity.Id,
                CostName = entity.CostName,
                ProviderType = entity.ProviderType,
                Purpose = entity.Purpose,
                Description = entity.Description,
                BaseCost = entity.BaseCost,
                PricingModel = entity.PricingModel,
                CostPerExecution = entity.CostPerExecution,
                CostPerResult = entity.CostPerResult,
                CostPerToken = entity.CostPerToken,
                CostPerMinute = entity.CostPerMinute,
                TieredPricing = StructuredJson.ParseObject(entity.TieredPricing),
                PricingConfiguration = StructuredJson.ParseObject(entity.PricingConfiguration),
                IsActive = entity.IsActive,
                EffectiveDate = entity.EffectiveDate,
                ExpiryDate = entity.ExpiryDate,
                Priority = entity.Priority,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt
            };
        }

    }
}
