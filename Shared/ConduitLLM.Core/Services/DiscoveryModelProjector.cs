using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Owns the database query, capability filtering, and service-neutral projection shared by live
/// discovery, cache warming, and administrative previews. Each HTTP service owns its wire dialect.
/// </summary>
public static class DiscoveryModelProjector
{
    public static async Task<IReadOnlyList<DiscoveredModelDto>> ProjectAsync(
        ConduitDbContext context,
        string? capability,
        bool includePricing,
        ILogger logger,
        CancellationToken cancellationToken = default, TimeProvider? clock = null)
    {
        var modelMappings = await context.ModelProviderMappings
            .Include(mapping => mapping.Provider)
            .Include(mapping => mapping.ModelProviderTypeAssociation)
                .ThenInclude(association => association.Model)
                    .ThenInclude(model => model.Series)
            .Include(mapping => mapping.ModelProviderTypeAssociation)
                .ThenInclude(association => association.ModelCost)
            .AsNoTracking()
            .Where(mapping =>
                mapping.IsEnabled &&
                mapping.Provider != null &&
                mapping.Provider.IsEnabled)
            .ToListAsync(cancellationToken);

        logger.LogDebug(
            "Found {Count} enabled model mappings for discovery (capability filter: {Capability})",
            modelMappings.Count,
            capability ?? "all");

        var models = new List<DiscoveredModelDto>();
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        foreach (var mapping in modelMappings)
        {
            var association = mapping.ModelProviderTypeAssociation;
            var model = association?.Model;
            if (model is null)
            {
                logger.LogWarning("Model mapping {ModelAlias} has no model data", mapping.ModelAlias);
                continue;
            }

            var capabilities = ModelCapabilityResolver.Resolve(model, association);
            if (!SupportsCapability(capability, mapping.Provider?.ProviderType, capabilities))
            {
                continue;
            }

            var maxInputTokens = association!.MaxInputTokens ?? model.MaxInputTokens ?? 0;
            var maxOutputTokens = association.MaxOutputTokens ?? model.MaxOutputTokens ?? 0;
            var supportsPdf = capabilities.SupportsFileInput ||
                              mapping.Provider?.ProviderType == ProviderType.OpenRouter;

            models.Add(new DiscoveredModelDto(
                mapping.ModelAlias,
                mapping.Provider?.ProviderType.ToString().ToLowerInvariant(),
                mapping.ModelAlias,
                model.Description ?? string.Empty,
                model.ModelCardUrl ?? string.Empty,
                maxInputTokens + maxOutputTokens,
                maxInputTokens,
                maxOutputTokens,
                model.TokenizerType.ToString().ToLowerInvariant(),
                capabilities.InputModalities ?? [],
                capabilities.OutputModalities ?? [],
                capabilities.CapabilitySource.ToString().ToLowerInvariant(),
                capabilities.CapabilitiesLastVerifiedAt,
                model.ModelParameters ?? model.Series?.Parameters ?? "{}",
                new DiscoveryModelCapabilitiesDto(
                    capabilities.SupportsChat,
                    capabilities.SupportsStreaming,
                    capabilities.SupportsImageInput,
                    capabilities.SupportsVideoInput,
                    capabilities.SupportsAudioInput,
                    capabilities.SupportsFileInput,
                    capabilities.SupportsVision,
                    capabilities.SupportsVideoUnderstanding,
                    capabilities.SupportsImageGeneration,
                    capabilities.SupportsVideoGeneration,
                    capabilities.SupportsEmbeddings,
                    capabilities.SupportsFunctionCalling,
                    capabilities.SupportsSpeechToText,
                    capabilities.SupportsTextToSpeech,
                    capabilities.SupportsRerank,
                    capabilities.SupportsFunctionCalling,
                    null,
                    maxInputTokens + maxOutputTokens,
                    maxOutputTokens,
                    supportsPdf),
                includePricing ? BuildPricing(association.ModelCost, now) : null)
                { PricingRefreshAt = includePricing ? PricingRefresh(association.ModelCost, now) : null });
        }

        return models;
    }

    private static bool SupportsCapability(
        string? capability,
        ProviderType? providerType,
        ModelCapabilitiesDto capabilities)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return true;
        }

        return capability.Replace("-", "_").ToLowerInvariant() switch
        {
            "chat" => capabilities.SupportsChat,
            "streaming" or "chat_stream" => capabilities.SupportsStreaming,
            "vision" => capabilities.SupportsVision,
            "image_input" => capabilities.SupportsImageInput,
            "video_input" => capabilities.SupportsVideoInput,
            "audio_input" => capabilities.SupportsAudioInput,
            "file_input" => capabilities.SupportsFileInput,
            "pdf_input" => capabilities.SupportsFileInput || providerType == ProviderType.OpenRouter,
            "video_understanding" => capabilities.SupportsVideoUnderstanding,
            "video_generation" => capabilities.SupportsVideoGeneration,
            "image_generation" => capabilities.SupportsImageGeneration,
            "embeddings" => capabilities.SupportsEmbeddings,
            "function_calling" => capabilities.SupportsFunctionCalling,
            "speech_to_text" or "audio_transcription" => capabilities.SupportsSpeechToText,
            "text_to_speech" => capabilities.SupportsTextToSpeech,
            "rerank" => capabilities.SupportsRerank,
            _ => false
        };
    }

    private static DateTime? PricingRefresh(ModelCost? cost, DateTime now)
    {
        if (cost is not { IsActive: true }) return null;
        if (cost.EffectiveDate > now) return cost.EffectiveDate;
        return cost.ExpiryDate > now ? cost.ExpiryDate : null;
    }

    private static DiscoveryModelPricingDto? BuildPricing(ModelCost? cost, DateTime now)
    {
        if (cost is not { IsActive: true }
            || cost.EffectiveDate > now
            || (cost.ExpiryDate.HasValue && cost.ExpiryDate.Value <= now))
        {
            return null;
        }

        return new DiscoveryModelPricingDto(
            cost.PricingModel.ToString().ToLowerInvariant(),
            cost.InputCostPerMillionTokens,
            cost.OutputCostPerMillionTokens,
            cost.CachedInputCostPerMillionTokens,
            cost.EmbeddingCostPerMillionTokens,
            "USD");
    }
}
