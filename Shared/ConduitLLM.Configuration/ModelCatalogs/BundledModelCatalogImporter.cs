using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.ModelCatalogs;

/// <summary>
/// Merges the bundled model snapshot. Provider-owned capability metadata is refreshed
/// while manually curated canonical metadata remains authoritative.
/// </summary>
public interface IBundledModelCatalogImporter
{
    Task<BundledModelCatalogImportResult?> ImportAsync(
        bool onlyWhenIdentifierCatalogIsEmpty,
        CancellationToken cancellationToken = default);
}

public sealed class BundledModelCatalogImporter : IBundledModelCatalogImporter
{
    private const long CATALOG_IMPORT_LOCK_ID = 7891012;
    private readonly IDbContextFactory<ConduitDbContext> _contextFactory;
    private readonly BundledModelCatalog _catalog;
    private readonly ILogger<BundledModelCatalogImporter> _logger;

    public BundledModelCatalogImporter(
        IDbContextFactory<ConduitDbContext> contextFactory,
        BundledModelCatalog catalog,
        ILogger<BundledModelCatalogImporter> logger)
    {
        _contextFactory = contextFactory;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>Returns without importing when <paramref name="onlyWhenIdentifierCatalogIsEmpty"/> is true and identifiers exist.</summary>
    public async Task<BundledModelCatalogImportResult?> ImportAsync(
        bool onlyWhenIdentifierCatalogIsEmpty,
        CancellationToken cancellationToken = default)
    {
        await using var strategyContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            // A retry must start with a fresh change tracker as well as a fresh
            // transaction; a failed save can leave generated keys on tracked rows.
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            if (onlyWhenIdentifierCatalogIsEmpty &&
                await context.ModelProviderTypeAssociations.AnyAsync(cancellationToken))
            {
                return null;
            }

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({CATALOG_IMPORT_LOCK_ID})", cancellationToken);

            // Re-check after taking the lock; another host may have seeded while this host waited.
            if (onlyWhenIdentifierCatalogIsEmpty &&
                await context.ModelProviderTypeAssociations.AnyAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var imported = await MergeAsync(context, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return imported;
        });
        if (result is null)
        {
            _logger.LogDebug("Bundled model catalog startup seed skipped because model identifiers already exist");
            return null;
        }

        foreach (var conflict in result.Conflicts)
        {
            _logger.LogWarning("Bundled model catalog entry skipped: {Conflict}", conflict);
        }

        _logger.LogInformation(
            "Bundled model catalog import completed: {Providers} providers, {Discovered} entries, " +
            "{Created} identifiers created, {Skipped} existing identifiers skipped, {Conflicts} conflicts",
            result.ProvidersProcessed,
            result.ModelsDiscovered,
            result.Created.Identifiers,
            result.SkippedExistingIdentifiers,
            result.Conflicts.Count);
        return result;
    }

    private async Task<BundledModelCatalogImportResult> MergeAsync(
        ConduitDbContext context,
        CancellationToken cancellationToken)
    {
        var catalogs = await _catalog.LoadAsync(cancellationToken);
        var result = new BundledModelCatalogImportResult
        {
            ProvidersProcessed = catalogs.Count,
            ModelsDiscovered = catalogs.Sum(x => x.Models.Count)
        };

        var authors = (await context.ModelAuthors.ToListAsync(cancellationToken))
            .ToDictionary(x => x.Name, StringComparer.Ordinal);
        var series = (await context.ModelSeries.Include(x => x.Author).ToListAsync(cancellationToken))
            .ToDictionary(x => SeriesKey(x.Author.Name, x.Name), StringComparer.Ordinal);
        var models = (await context.Models.Include(x => x.Series).ThenInclude(x => x.Author)
                .ToListAsync(cancellationToken))
            .GroupBy(x => ModelKey(x.Series.Author.Name, x.Series.Name, x.Name), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var costs = (await context.ModelCosts.ToListAsync(cancellationToken))
            .GroupBy(x => x.CostName, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var identifiers = (await context.ModelProviderTypeAssociations
                .Where(x => x.Provider != null)
                .ToListAsync(cancellationToken))
            .ToDictionary(x => IdentifierKey((int?)x.Provider, x.Identifier), StringComparer.Ordinal);

        // Warn once per distinct unrecognized tokenizer string per import (#1232).
        var unrecognizedTokenizers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var providerCatalog in catalogs)
        {
            var providerResult = new ProviderCatalogImportResult
            {
                Provider = providerCatalog.Name,
                ModelsDiscovered = providerCatalog.Models.Count
            };
            result.Providers.Add(providerResult);

            foreach (var (identifier, catalogModel) in providerCatalog.Models)
            {
                var identifierKey = IdentifierKey(providerCatalog.Configuration.ProviderType, identifier);
                var tokenizerType = ResolveTokenizer(
                    providerCatalog.Name, identifier, catalogModel.TokenizerType, unrecognizedTokenizers);
                if (identifiers.TryGetValue(identifierKey, out var existingAssociation))
                {
                    if (existingAssociation.CapabilitySource != ModelCapabilitySource.Manual)
                    {
                        ApplyCatalogCapabilities(existingAssociation, catalogModel);
                    }
                    if (CanRefreshCanonical(existingAssociation.Model))
                    {
                        ApplyCatalogCapabilities(existingAssociation.Model, catalogModel, tokenizerType);
                    }
                    providerResult.SkippedExistingIdentifiers++;
                    result.SkippedExistingIdentifiers++;
                    continue;
                }

                var modelName = string.IsNullOrWhiteSpace(catalogModel.Name) ? identifier : catalogModel.Name;
                var ownerName = string.IsNullOrWhiteSpace(catalogModel.Owner)
                    ? providerCatalog.Name
                    : catalogModel.Owner;
                var seriesName = string.IsNullOrWhiteSpace(catalogModel.Series)
                    ? (string.IsNullOrWhiteSpace(catalogModel.Family) ? "Unknown Series" : catalogModel.Family)
                    : catalogModel.Series;
                var modelKey = ModelKey(ownerName, seriesName, modelName);

                if (models.TryGetValue(modelKey, out var matchingModels) && matchingModels.Count > 1)
                {
                    AddConflict(result, providerResult,
                        $"{providerCatalog.Name}/{identifier}: multiple canonical models match '{ownerName} / {seriesName} / {modelName}'.");
                    continue;
                }

                if (costs.TryGetValue(identifier, out var matchingCosts) && matchingCosts.Count > 1)
                {
                    AddConflict(result, providerResult,
                        $"{providerCatalog.Name}/{identifier}: multiple model costs have CostName '{identifier}'.");
                    continue;
                }

                if (!authors.TryGetValue(ownerName, out var author))
                {
                    author = new ModelAuthor
                    {
                        Name = ownerName,
                        WebsiteUrl = providerCatalog.Configuration.WebsiteUrl
                    };
                    context.ModelAuthors.Add(author);
                    authors.Add(ownerName, author);
                    providerResult.Created.Authors++;
                }

                var seriesKey = SeriesKey(ownerName, seriesName);
                if (!series.TryGetValue(seriesKey, out var modelSeries))
                {
                    modelSeries = new ModelSeries
                    {
                        Name = seriesName,
                        Author = author,
                        TokenizerType = tokenizerType,
                        Parameters = "{}"
                    };
                    context.ModelSeries.Add(modelSeries);
                    series.Add(seriesKey, modelSeries);
                    providerResult.Created.Series++;
                }

                Model model;
                if (matchingModels?.Count == 1)
                {
                    model = matchingModels[0];
                }
                else
                {
                    model = new Model
                    {
                        Name = modelName,
                        Description = catalogModel.Notes,
                        ModelCardUrl = providerCatalog.Configuration.ModelCardUrl,
                        Series = modelSeries,
                        SupportsVision = catalogModel.SupportsVision,
                        SupportsImageGeneration = catalogModel.SupportsImageGeneration,
                        SupportsVideoGeneration = catalogModel.SupportsVideoGeneration,
                        SupportsSpeechToText = catalogModel.SupportsSpeechToText,
                        SupportsTextToSpeech = catalogModel.SupportsTextToSpeech,
                        SupportsRerank = catalogModel.SupportsRerank,
                        SupportsEmbeddings = catalogModel.SupportsEmbeddings,
                        SupportsChat = catalogModel.SupportsChat,
                        SupportsFunctionCalling = catalogModel.SupportsFunctionCalling,
                        SupportsStreaming = catalogModel.SupportsStreaming,
                        TokenizerType = tokenizerType,
                        MaxInputTokens = catalogModel.MaxInputTokens,
                        MaxOutputTokens = catalogModel.MaxOutputTokens,
                        InputModalitiesJson = SerializeInputModalities(catalogModel),
                        OutputModalitiesJson = SerializeOutputModalities(catalogModel),
                        CapabilitySource = catalogModel.CapabilitySource,
                        CapabilitiesLastVerifiedAt = catalogModel.CapabilitiesLastVerifiedAt,
                        IsActive = true,
                        ModelParameters = "{}"
                    };
                    context.Models.Add(model);
                    models[modelKey] = [model];
                    providerResult.Created.Models++;
                }

                ModelCost cost;
                if (matchingCosts?.Count == 1)
                {
                    cost = matchingCosts[0];
                }
                else
                {
                    var now = DateTime.UtcNow;
                    cost = new ModelCost
                    {
                        CostName = identifier,
                        Description = $"Standard pricing for {identifier}",
                        InputCostPerMillionTokens = catalogModel.InputPricePerMillion,
                        OutputCostPerMillionTokens = catalogModel.OutputPricePerMillion,
                        PricingModel = PricingModel.Standard,
                        ModelType = "Text",
                        IsActive = true,
                        EffectiveDate = now,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    context.ModelCosts.Add(cost);
                    costs[identifier] = [cost];
                    providerResult.Created.Costs++;
                }

                var association = new ModelProviderTypeAssociation
                {
                    Model = model,
                    Identifier = identifier,
                    Provider = (ProviderType)providerCatalog.Configuration.ProviderType,
                    IsEnabled = true,
                    IsPrimary = true,
                    MaxInputTokens = catalogModel.MaxInputTokens,
                    MaxOutputTokens = catalogModel.MaxOutputTokens,
                    InputModalitiesJson = SerializeInputModalities(catalogModel),
                    OutputModalitiesJson = SerializeOutputModalities(catalogModel),
                    OperationalCapabilitiesJson = ModelCapabilityResolver.SerializeOverrides(
                        ToProviderOverrides(catalogModel)),
                    CapabilitySource = catalogModel.CapabilitySource,
                    CapabilitiesLastVerifiedAt = catalogModel.CapabilitiesLastVerifiedAt,
                    ModelCost = cost
                };
                context.ModelProviderTypeAssociations.Add(association);
                identifiers.Add(identifierKey, association);
                providerResult.Created.Identifiers++;
            }

            result.Created.Add(providerResult.Created);
        }

        return result;
    }

    private static bool CanRefreshCanonical(Model model) =>
        model.CapabilitySource is ModelCapabilitySource.Unknown
            or ModelCapabilitySource.LegacyInferred
            or ModelCapabilitySource.ProviderApi;

    private static void ApplyCatalogCapabilities(
        Model model,
        ProviderCatalogModel catalog,
        TokenizerType tokenizerType)
    {
        model.TokenizerType = tokenizerType;
        model.SupportsChat = catalog.SupportsChat;
        model.SupportsStreaming = catalog.SupportsStreaming;
        model.SupportsVision = catalog.SupportsVision;
        model.SupportsImageGeneration = catalog.SupportsImageGeneration;
        model.SupportsVideoGeneration = catalog.SupportsVideoGeneration;
        model.SupportsEmbeddings = catalog.SupportsEmbeddings;
        model.SupportsFunctionCalling = catalog.SupportsFunctionCalling;
        model.SupportsSpeechToText = catalog.SupportsSpeechToText;
        model.SupportsTextToSpeech = catalog.SupportsTextToSpeech;
        model.SupportsRerank = catalog.SupportsRerank;
        model.InputModalitiesJson = SerializeInputModalities(catalog);
        model.OutputModalitiesJson = SerializeOutputModalities(catalog);
        model.CapabilitySource = catalog.CapabilitySource;
        model.CapabilitiesLastVerifiedAt = catalog.CapabilitiesLastVerifiedAt;
        model.MaxInputTokens = catalog.MaxInputTokens;
        model.MaxOutputTokens = catalog.MaxOutputTokens;
        model.UpdatedAt = DateTime.UtcNow;
    }

    private static void ApplyCatalogCapabilities(
        ModelProviderTypeAssociation association,
        ProviderCatalogModel catalog)
    {
        association.InputModalitiesJson = SerializeInputModalities(catalog);
        association.OutputModalitiesJson = SerializeOutputModalities(catalog);
        association.OperationalCapabilitiesJson =
            ModelCapabilityResolver.SerializeOverrides(ToProviderOverrides(catalog));
        association.CapabilitySource = catalog.CapabilitySource;
        association.CapabilitiesLastVerifiedAt = catalog.CapabilitiesLastVerifiedAt;
        association.MaxInputTokens = catalog.MaxInputTokens;
        association.MaxOutputTokens = catalog.MaxOutputTokens;
    }

    private static string SerializeInputModalities(ProviderCatalogModel catalog) =>
        ModelModalities.Serialize(catalog.InputModalities ?? InferInputModalities(catalog))!;

    private static string SerializeOutputModalities(ProviderCatalogModel catalog) =>
        ModelModalities.Serialize(catalog.OutputModalities ?? InferOutputModalities(catalog))!;

    private static IReadOnlyList<string> InferInputModalities(ProviderCatalogModel model)
    {
        var values = new List<string>();
        if (model.SupportsChat || model.SupportsEmbeddings || model.SupportsImageGeneration ||
            model.SupportsVideoGeneration || model.SupportsTextToSpeech || model.SupportsRerank)
            values.Add(ModelModalities.Text);
        if (model.SupportsVision) values.Add(ModelModalities.Image);
        if (model.SupportsSpeechToText || model.SupportsAudio) values.Add(ModelModalities.Audio);
        return values;
    }

    private static IReadOnlyList<string> InferOutputModalities(ProviderCatalogModel model)
    {
        var values = new List<string>();
        if (model.SupportsChat || model.SupportsSpeechToText || model.SupportsRerank)
            values.Add(ModelModalities.Text);
        if (model.SupportsImageGeneration) values.Add(ModelModalities.Image);
        if (model.SupportsVideoGeneration) values.Add(ModelModalities.Video);
        if (model.SupportsTextToSpeech) values.Add(ModelModalities.Audio);
        return values;
    }

    private static ProviderOperationalCapabilities ToProviderOverrides(ProviderCatalogModel model) => new()
    {
        SupportsChat = model.SupportsChat,
        SupportsStreaming = model.SupportsStreaming,
        SupportsVision = model.SupportsVision,
        SupportsImageGeneration = model.SupportsImageGeneration,
        SupportsVideoGeneration = model.SupportsVideoGeneration,
        SupportsEmbeddings = model.SupportsEmbeddings,
        SupportsFunctionCalling = model.SupportsFunctionCalling,
        SupportsSpeechToText = model.SupportsSpeechToText,
        SupportsTextToSpeech = model.SupportsTextToSpeech,
        SupportsRerank = model.SupportsRerank
    };

    private static void AddConflict(
        BundledModelCatalogImportResult result,
        ProviderCatalogImportResult provider,
        string message)
    {
        result.Conflicts.Add(message);
        provider.Conflicts++;
    }

    private static string IdentifierKey(int? provider, string identifier) => $"{provider}:{identifier}";
    private static string SeriesKey(string author, string series) => $"{author}\u001f{series}";
    private static string ModelKey(string author, string series, string model) => $"{author}\u001f{series}\u001f{model}";

    /// <summary>
    /// Maps the catalog's tokenizer string, warning once per distinct unrecognized value.
    /// A typo or a new vocabulary name must not silently degrade to "no tokenizer" (#1232):
    /// the model still imports as <see cref="TokenizerType.None"/> (counts fall back to the
    /// approximate default encoding), but the import log says so.
    /// </summary>
    private TokenizerType ResolveTokenizer(
        string provider,
        string identifier,
        string value,
        HashSet<string> reportedUnrecognized)
    {
        if (MapTokenizer(value) is { } mapped)
        {
            return mapped;
        }

        if (reportedUnrecognized.Add(value))
        {
            _logger.LogWarning(
                "Bundled model catalog {Provider}/{Identifier}: unrecognized tokenizer '{Tokenizer}' " +
                "imported as None; token counts will use the approximate default encoding. " +
                "Add a mapping in BundledModelCatalogImporter.MapTokenizer if this is a real vocabulary.",
                provider, identifier, value);
        }

        return TokenizerType.None;
    }

    // Internal for tests: the bundled-catalog hygiene test asserts every shipped catalog
    // value is recognized here, so catalog and importer cannot drift apart silently.
    internal static TokenizerType? MapTokenizer(string? value) => value switch
    {
        null or "" or "None" => TokenizerType.None,
        "Cl100KBase" or "cl100k_base" => TokenizerType.Cl100KBase,
        "P50KBase" or "p50k_base" => TokenizerType.P50KBase,
        "P50KEdit" => TokenizerType.P50KEdit,
        "R50KBase" => TokenizerType.R50KBase,
        "O200KBase" or "o200k_base" => TokenizerType.O200KBase,
        "O200KHarmony" => TokenizerType.O200KHarmony,
        "Claude" => TokenizerType.Claude,
        "Claude3" => TokenizerType.Claude3,
        "Gemini" => TokenizerType.Gemini,
        "PaLM" => TokenizerType.PaLM,
        "LLaMA" or "Llama" => TokenizerType.LLaMA,
        "LLaMA2" => TokenizerType.LLaMA2,
        "LLaMA3" => TokenizerType.LLaMA3,
        "Mistral" => TokenizerType.Mistral,
        "Cohere" => TokenizerType.Cohere,
        "Kimi" => TokenizerType.Kimi,
        "Groq" => TokenizerType.Groq,
        "Cerebras" => TokenizerType.Cerebras,
        "MiniMax" => TokenizerType.MiniMax,
        "SentencePiece" or "T5" => TokenizerType.SentencePiece,
        "BPE" or "ByteLevelBPE" or "GPTNeoX" => TokenizerType.BPE,
        "WordPiece" => TokenizerType.WordPiece,
        "Tiktoken" or "tiktoken" => TokenizerType.Tiktoken,
        _ => null
    };
}
