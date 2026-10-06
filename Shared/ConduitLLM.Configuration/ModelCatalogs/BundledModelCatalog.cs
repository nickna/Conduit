using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ConduitLLM.Configuration.Models;

namespace ConduitLLM.Configuration.ModelCatalogs;

/// <summary>Loads the provider model catalog snapshot embedded in the release assembly.</summary>
public sealed class BundledModelCatalog
{
    private const string ResourcePrefix = "ConduitLLM.Configuration.ModelCatalogs.";
    private readonly Assembly _assembly;

    public BundledModelCatalog() : this(typeof(BundledModelCatalog).Assembly) { }

    internal BundledModelCatalog(Assembly assembly)
    {
        _assembly = assembly;
    }

    public async Task<IReadOnlyList<BundledProviderCatalog>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var configuration = await DeserializeResourceAsync<Dictionary<string, ProviderCatalogConfiguration>>(
            "provider-config.json",
            (JsonTypeInfo<Dictionary<string, ProviderCatalogConfiguration>>)
                BundledModelCatalogJsonContext.Default.GetTypeInfo(
                    typeof(Dictionary<string, ProviderCatalogConfiguration>))!,
            cancellationToken);
        var resources = _assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        var catalogs = new List<BundledProviderCatalog>();

        foreach (var (providerName, provider) in configuration.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var fileName = $"{providerName}-models.json";
            if (!resources.Contains(ResourcePrefix + fileName))
            {
                throw new InvalidOperationException(
                    $"Bundled model catalog configuration references '{providerName}', but {fileName} is not embedded.");
            }

            var document = await DeserializeResourceAsync(
                fileName,
                BundledModelCatalogJsonContext.Default.ProviderModelsDocument,
                cancellationToken);
            catalogs.Add(new BundledProviderCatalog(providerName, provider, document.Models));
        }

        var configuredResources = configuration.Keys
            .Select(x => ResourcePrefix + x + "-models.json")
            .ToHashSet(StringComparer.Ordinal);
        var unconfigured = resources
            .Where(x => x.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                        x.EndsWith("-models.json", StringComparison.Ordinal) &&
                        !configuredResources.Contains(x))
            .ToList();
        if (unconfigured.Count > 0)
        {
            throw new InvalidOperationException(
                $"Bundled provider model catalog(s) are missing from provider-config.json: {string.Join(", ", unconfigured)}");
        }

        return catalogs;
    }

    private async Task<T> DeserializeResourceAsync<T>(
        string fileName,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = _assembly.GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new InvalidOperationException($"Bundled model catalog resource '{fileName}' was not found.");
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken)
            ?? throw new InvalidOperationException($"Bundled model catalog resource '{fileName}' is empty or invalid.");
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Dictionary<string, ProviderCatalogConfiguration>))]
[JsonSerializable(typeof(ProviderModelsDocument))]
internal partial class BundledModelCatalogJsonContext : JsonSerializerContext;

public sealed record BundledProviderCatalog(
    string Name,
    ProviderCatalogConfiguration Configuration,
    IReadOnlyDictionary<string, ProviderCatalogModel> Models);

public sealed class ProviderCatalogConfiguration
{
    public int ProviderType { get; set; }
    public string WebsiteUrl { get; set; } = string.Empty;
    public string ModelCardUrl { get; set; } = string.Empty;
    public bool SupportsAudio { get; set; }
}

public sealed class ProviderModelsDocument
{
    public Dictionary<string, ProviderCatalogModel> Models { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ProviderCatalogModel
{
    public string Name { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Series { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public int MaxInputTokens { get; set; }
    public int MaxOutputTokens { get; set; }
    public string TokenizerType { get; set; } = "None";
    public bool SupportsChat { get; set; }
    public bool SupportsStreaming { get; set; }
    public bool SupportsVision { get; set; }
    public bool SupportsFunctionCalling { get; set; }
    public bool SupportsEmbeddings { get; set; }
    public bool SupportsAudio { get; set; }
    public bool SupportsImageGeneration { get; set; }
    public bool SupportsVideoGeneration { get; set; }
    public bool SupportsSpeechToText { get; set; }
    public bool SupportsTextToSpeech { get; set; }
    public bool SupportsRerank { get; set; }
    public IReadOnlyList<string>? InputModalities { get; set; }
    public IReadOnlyList<string>? OutputModalities { get; set; }
    public ModelCapabilitySource CapabilitySource { get; set; } = ModelCapabilitySource.Curated;
    public DateTime? CapabilitiesLastVerifiedAt { get; set; }
    public decimal InputPricePerMillion { get; set; }
    public decimal OutputPricePerMillion { get; set; }
    public int? SpeedTokensPerSec { get; set; }
    public string? Notes { get; set; }
}
