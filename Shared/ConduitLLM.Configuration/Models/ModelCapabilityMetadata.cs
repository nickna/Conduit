using System.Text.Json;

using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Serialization;

namespace ConduitLLM.Configuration.Models;

/// <summary>
/// Describes where model capability metadata originated.
/// </summary>
public enum ModelCapabilitySource
{
    Unknown = 0,
    LegacyInferred = 1,
    Curated = 2,
    ProviderApi = 3,
    Manual = 4
}

/// <summary>
/// Canonical modality names used in storage and API contracts.
/// </summary>
public static class ModelModalities
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Audio = "audio";
    public const string Video = "video";
    public const string File = "file";

    private static readonly HashSet<string> Known =
        new(StringComparer.OrdinalIgnoreCase) { Text, Image, Audio, Video, File };

    public static IReadOnlyList<string>? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize(
                json,
                ConfigurationModelJsonContext.Default.StringArray) ?? []);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Serialize(IEnumerable<string>? modalities) =>
        modalities is null
            ? null
            : JsonSerializer.Serialize(
                Normalize(modalities).ToArray(),
                ConfigurationModelJsonContext.Default.StringArray);

    public static IReadOnlyList<string> Normalize(IEnumerable<string> modalities) =>
        modalities
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    public static bool IsKnown(string modality) => Known.Contains(modality);
}

/// <summary>
/// Nullable provider-specific overrides for operational model capabilities.
/// A null member inherits the canonical model value.
/// </summary>
public sealed class ProviderOperationalCapabilities
{
    public bool? SupportsChat { get; set; }
    public bool? SupportsStreaming { get; set; }
    public bool? SupportsVision { get; set; }
    public bool? SupportsImageGeneration { get; set; }
    public bool? SupportsVideoGeneration { get; set; }
    public bool? SupportsEmbeddings { get; set; }
    public bool? SupportsFunctionCalling { get; set; }
    public bool? SupportsSpeechToText { get; set; }
    public bool? SupportsTextToSpeech { get; set; }
    public bool? SupportsRerank { get; set; }
}

/// <summary>
/// Resolves canonical and provider-specific model capability metadata.
/// </summary>
public static class ModelCapabilityResolver
{
    public static ModelCapabilitiesDto Resolve(
        Model model,
        ModelProviderTypeAssociation? association = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var canonicalInputs = ModelModalities.Parse(model.InputModalitiesJson);
        var canonicalOutputs = ModelModalities.Parse(model.OutputModalitiesJson);
        if (model.CapabilitySource != ModelCapabilitySource.Unknown)
        {
            canonicalInputs ??= InferLegacyInputs(model);
            canonicalOutputs ??= InferLegacyOutputs(model);
        }

        var inputs = association?.InputModalitiesJson is null
            ? canonicalInputs
            : ModelModalities.Parse(association.InputModalitiesJson);
        var outputs = association?.OutputModalitiesJson is null
            ? canonicalOutputs
            : ModelModalities.Parse(association.OutputModalitiesJson);

        var operationOverrides = DeserializeOverrides(association?.OperationalCapabilitiesJson);

        var supportsImageInput =
            inputs?.Contains(ModelModalities.Image, StringComparer.Ordinal) == true;
        var supportsVideoInput =
            inputs?.Contains(ModelModalities.Video, StringComparer.Ordinal) == true;

        return new ModelCapabilitiesDto
        {
            InputModalities = inputs,
            OutputModalities = outputs,
            CapabilitySource = association?.CapabilitySource ?? model.CapabilitySource,
            CapabilitiesLastVerifiedAt =
                association?.CapabilitiesLastVerifiedAt ?? model.CapabilitiesLastVerifiedAt,
            SupportsImageInput = supportsImageInput,
            SupportsVideoInput = supportsVideoInput,
            SupportsAudioInput =
                inputs?.Contains(ModelModalities.Audio, StringComparer.Ordinal) == true,
            SupportsFileInput =
                inputs?.Contains(ModelModalities.File, StringComparer.Ordinal) == true,
            SupportsVideoUnderstanding =
                supportsVideoInput &&
                outputs?.Contains(ModelModalities.Text, StringComparer.Ordinal) == true,
            SupportsChat = operationOverrides?.SupportsChat ?? model.SupportsChat,
            SupportsStreaming = operationOverrides?.SupportsStreaming ?? model.SupportsStreaming,
            SupportsVision = supportsImageInput,
            SupportsImageGeneration =
                operationOverrides?.SupportsImageGeneration ?? model.SupportsImageGeneration,
            SupportsVideoGeneration =
                operationOverrides?.SupportsVideoGeneration ?? model.SupportsVideoGeneration,
            SupportsEmbeddings =
                operationOverrides?.SupportsEmbeddings ?? model.SupportsEmbeddings,
            SupportsFunctionCalling =
                operationOverrides?.SupportsFunctionCalling ?? model.SupportsFunctionCalling,
            SupportsSpeechToText =
                operationOverrides?.SupportsSpeechToText ?? model.SupportsSpeechToText,
            SupportsTextToSpeech =
                operationOverrides?.SupportsTextToSpeech ?? model.SupportsTextToSpeech,
            SupportsRerank = operationOverrides?.SupportsRerank ?? model.SupportsRerank,
            MaxInputTokens = association?.MaxInputTokens ?? model.MaxInputTokens,
            MaxOutputTokens = association?.MaxOutputTokens ?? model.MaxOutputTokens
        };
    }

    public static string? SerializeOverrides(ProviderOperationalCapabilities? capabilities) =>
        capabilities is null
            ? null
            : JsonSerializer.Serialize(
                capabilities,
                ConfigurationModelJsonContext.Default.ProviderOperationalCapabilities);

    public static ProviderOperationalCapabilities? DeserializeOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                json,
                ConfigurationModelJsonContext.Default.ProviderOperationalCapabilities);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> InferLegacyInputs(Model model)
    {
        var modalities = new List<string>();
        if (model.SupportsChat ||
            model.SupportsEmbeddings ||
            model.SupportsImageGeneration ||
            model.SupportsVideoGeneration ||
            model.SupportsTextToSpeech ||
            model.SupportsRerank)
        {
            modalities.Add(ModelModalities.Text);
        }
        if (model.SupportsVision)
        {
            modalities.Add(ModelModalities.Image);
        }
        if (model.SupportsSpeechToText)
        {
            modalities.Add(ModelModalities.Audio);
        }
        return ModelModalities.Normalize(modalities);
    }

    private static IReadOnlyList<string> InferLegacyOutputs(Model model)
    {
        var modalities = new List<string>();
        if (model.SupportsChat || model.SupportsSpeechToText || model.SupportsRerank)
        {
            modalities.Add(ModelModalities.Text);
        }
        if (model.SupportsImageGeneration)
        {
            modalities.Add(ModelModalities.Image);
        }
        if (model.SupportsVideoGeneration)
        {
            modalities.Add(ModelModalities.Video);
        }
        if (model.SupportsTextToSpeech)
        {
            modalities.Add(ModelModalities.Audio);
        }
        return ModelModalities.Normalize(modalities);
    }
}
