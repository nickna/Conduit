using System.Globalization;
using System.Text.Json;

using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Models.ProviderSync;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Providers.OpenRouter;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Admin.Services
{
    /// <inheritdoc />
    public class OpenRouterDriftDetectionService : IOpenRouterDriftDetectionService
    {
        private readonly IDbContextFactory<ConduitDbContext> _dbFactory;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly OpenRouterSyncOptions _options;
        private readonly ILogger<OpenRouterDriftDetectionService> _logger;

        private static readonly JsonSerializerOptions JsonOptions = ConduitLLM.Core.Serialization.ConduitJsonOptions.Compact;

        public OpenRouterDriftDetectionService(
            IDbContextFactory<ConduitDbContext> dbFactory,
            IHttpClientFactory httpClientFactory,
            IOptions<OpenRouterSyncOptions> options,
            ILogger<OpenRouterDriftDetectionService> logger)
        {
            _dbFactory = dbFactory;
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<ProviderSyncRunDto> RunSyncAsync(string triggeredBy, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var run = new ProviderMetadataSyncRun
            {
                ProviderType = ProviderType.OpenRouter,
                TriggeredBy = triggeredBy,
                Status = "Running",
                StartedAt = DateTime.UtcNow
            };
            db.ProviderMetadataSyncRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var catalog = await FetchCatalogAsync(cancellationToken);
                run.ModelsFetched = catalog.Count;

                var byId = new Dictionary<string, OpenRouterCatalogModel>(StringComparer.OrdinalIgnoreCase);
                var bySlug = new Dictionary<string, OpenRouterCatalogModel>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in catalog)
                {
                    if (!string.IsNullOrEmpty(m.Id)) byId[m.Id] = m;
                    if (!string.IsNullOrEmpty(m.CanonicalSlug)) bySlug[m.CanonicalSlug!] = m;
                }

                var mappings = await db.ModelProviderMappings
                    .Include(x => x.Provider)
                    .Include(x => x.ModelProviderTypeAssociation).ThenInclude(a => a.Model)
                    .Include(x => x.ModelProviderTypeAssociation).ThenInclude(a => a.ModelCost)
                    .Where(x => x.IsEnabled && x.Provider.ProviderType == ProviderType.OpenRouter)
                    .ToListAsync(cancellationToken);
                run.MappingsChecked = mappings.Count;

                foreach (var mapping in mappings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessMappingAsync(db, run, mapping, byId, bySlug, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                run.Status = "Completed";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                run.Status = "Cancelled";
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenRouter metadata sync run {RunId} failed", run.Id);
                run.Status = "Failed";
                run.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            }
            finally
            {
                run.CompletedAt = DateTime.UtcNow;
                if (!cancellationToken.IsCancellationRequested)
                {
                    try { await db.SaveChangesAsync(cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    // A fresh context persists only the run status, never pending drift mutations after loss.
                    using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await using var completionDb = await _dbFactory.CreateDbContextAsync(completion.Token);
                        var persistedRun = await completionDb.ProviderMetadataSyncRuns.FindAsync([run.Id], completion.Token);
                        if (persistedRun != null)
                        {
                            persistedRun.Status = "Cancelled";
                            persistedRun.CompletedAt = run.CompletedAt;
                            await completionDb.SaveChangesAsync(completion.Token);
                        }
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Could not persist cancelled sync status for run {RunId}", run.Id); }
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return MapRun(run);
        }

        private async Task<List<OpenRouterCatalogModel>> FetchCatalogAsync(CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient("OpenRouterSync");
            client.Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds);

            using var response = await client.GetAsync(_options.ModelsEndpoint, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            var parsed = AdminJson.Deserialize<OpenRouterCatalogResponse>(json, JsonOptions);
            var models = parsed?.Data ?? new List<OpenRouterCatalogModel>();

            // The full list is returned when offset/limit are omitted; a suspiciously round count may
            // indicate a paginated truncation. Log it rather than silently under-comparing.
            if (models.Count is 500 or 1000)
            {
                _logger.LogWarning(
                    "OpenRouter catalog returned exactly {Count} models — verify this is the full list, not a paginated page.",
                    models.Count);
            }

            return models;
        }

        private async Task ProcessMappingAsync(
            ConduitDbContext db,
            ProviderMetadataSyncRun run,
            ModelProviderMapping mapping,
            IReadOnlyDictionary<string, OpenRouterCatalogModel> byId,
            IReadOnlyDictionary<string, OpenRouterCatalogModel> bySlug,
            CancellationToken cancellationToken)
        {
            var modelId = mapping.ProviderModelId;
            OpenRouterCatalogModel? catalog = byId.TryGetValue(modelId, out var c1) ? c1
                : bySlug.TryGetValue(modelId, out var c2) ? c2 : null;

            var mpta = mapping.ModelProviderTypeAssociation;
            var model = mpta?.Model;
            var cost = mpta?.ModelCost;

            var detected = new Dictionary<DriftType, (string current, string proposed)>();

            if (catalog == null)
            {
                detected[DriftType.ModelRemoved] = (
                    "{}",
                    Json(new Dictionary<string, object?> { ["removed"] = true }));
            }
            else
            {
                ComputePricingDrift(catalog, cost, detected);
                ComputeContextWindowDrift(catalog, mpta, model, detected);
                ComputeCapabilitiesDrift(catalog, mpta, model, detected);

                if (!string.IsNullOrEmpty(catalog.ExpirationDate))
                    detected[DriftType.ModelDeprecated] = (
                        "{}",
                        Json(new Dictionary<string, object?> { ["expirationDate"] = catalog.ExpirationDate }));
            }

            foreach (var driftType in Enum.GetValues<DriftType>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existingPending = await db.ProviderMetadataDriftItems.FirstOrDefaultAsync(
                    x => x.ModelProviderMappingId == mapping.Id && x.DriftType == driftType && x.Status == DriftStatus.Pending,
                    cancellationToken);

                if (detected.TryGetValue(driftType, out var d))
                {
                    if (existingPending != null)
                    {
                        existingPending.CurrentValuesJson = d.current;
                        existingPending.ProposedValuesJson = d.proposed;
                        existingPending.LastDetectedAt = DateTime.UtcNow;
                        existingPending.LastSyncRunId = run.Id;
                        existingPending.OpenRouterModelId = modelId;
                        run.ItemsUpdated++;
                    }
                    else
                    {
                        // Don't re-open an item an admin dismissed with the same proposal.
                        var lastDismissed = await db.ProviderMetadataDriftItems
                            .Where(x => x.ModelProviderMappingId == mapping.Id && x.DriftType == driftType && x.Status == DriftStatus.Dismissed)
                            .OrderByDescending(x => x.Id)
                            .FirstOrDefaultAsync(cancellationToken);
                        if (lastDismissed != null && lastDismissed.ProposedValuesJson == d.proposed)
                            continue;

                        db.ProviderMetadataDriftItems.Add(new ProviderMetadataDriftItem
                        {
                            ModelProviderMappingId = mapping.Id,
                            ProviderId = mapping.ProviderId,
                            OpenRouterModelId = modelId,
                            DriftType = driftType,
                            Status = DriftStatus.Pending,
                            CurrentValuesJson = d.current,
                            ProposedValuesJson = d.proposed,
                            FirstDetectedAt = DateTime.UtcNow,
                            LastDetectedAt = DateTime.UtcNow,
                            LastSyncRunId = run.Id
                        });
                        run.ItemsCreated++;
                    }
                }
                else if (existingPending != null)
                {
                    existingPending.Status = DriftStatus.AutoResolved;
                    existingPending.ResolvedAt = DateTime.UtcNow;
                    existingPending.ResolvedBy = "system:auto-resolve";
                    existingPending.LastSyncRunId = run.Id;
                    run.ItemsAutoResolved++;
                }
            }
        }

        private static void ComputePricingDrift(
            OpenRouterCatalogModel catalog, ModelCost? cost,
            Dictionary<DriftType, (string current, string proposed)> detected)
        {
            var proposed = new PricingDriftPayload
            {
                InputPerMillion = ToPerMillion(catalog.Pricing?.Prompt),
                OutputPerMillion = ToPerMillion(catalog.Pricing?.Completion),
                CachedInputPerMillion = ToPerMillion(catalog.Pricing?.InputCacheRead),
                CachedWritePerMillion = ToPerMillion(catalog.Pricing?.InputCacheWrite)
            };

            if (cost == null)
            {
                if (proposed.InputPerMillion.HasValue || proposed.OutputPerMillion.HasValue)
                    detected[DriftType.MissingCost] = (Json(new PricingDriftPayload()), Json(proposed));
                return;
            }

            var current = new PricingDriftPayload
            {
                InputPerMillion = Round4(cost.InputCostPerMillionTokens),
                OutputPerMillion = Round4(cost.OutputCostPerMillionTokens),
                CachedInputPerMillion = Round4(cost.CachedInputCostPerMillionTokens),
                CachedWritePerMillion = Round4(cost.CachedInputWriteCostPerMillionTokens)
            };

            if (current.InputPerMillion != proposed.InputPerMillion
                || current.OutputPerMillion != proposed.OutputPerMillion
                || current.CachedInputPerMillion != proposed.CachedInputPerMillion
                || current.CachedWritePerMillion != proposed.CachedWritePerMillion)
            {
                detected[DriftType.Pricing] = (Json(current), Json(proposed));
            }
        }

        private static void ComputeContextWindowDrift(
            OpenRouterCatalogModel catalog, ModelProviderTypeAssociation? mpta, Model? model,
            Dictionary<DriftType, (string current, string proposed)> detected)
        {
            var proposed = new ContextWindowDriftPayload
            {
                MaxInputTokens = catalog.ContextLength ?? catalog.TopProvider?.ContextLength,
                MaxOutputTokens = catalog.TopProvider?.MaxCompletionTokens
            };
            var current = new ContextWindowDriftPayload
            {
                MaxInputTokens = mpta?.MaxInputTokens ?? model?.MaxInputTokens,
                MaxOutputTokens = mpta?.MaxOutputTokens ?? model?.MaxOutputTokens
            };

            // Only flag when the provider reports a value that differs (never propose a heuristic).
            var inputDrift = proposed.MaxInputTokens.HasValue && proposed.MaxInputTokens != current.MaxInputTokens;
            var outputDrift = proposed.MaxOutputTokens.HasValue && proposed.MaxOutputTokens != current.MaxOutputTokens;
            if (inputDrift || outputDrift)
                detected[DriftType.ContextWindow] = (Json(current), Json(proposed));
        }

        private static void ComputeCapabilitiesDrift(
            OpenRouterCatalogModel catalog, ModelProviderTypeAssociation? association, Model? model,
            Dictionary<DriftType, (string current, string proposed)> detected)
        {
            if (model == null)
                return;

            var input = catalog.Architecture?.InputModalities ?? new List<string>();
            var output = catalog.Architecture?.OutputModalities ?? new List<string>();
            var supported = catalog.SupportedParameters ?? new List<string>();

            var proposed = new CapabilitiesDriftPayload
            {
                InputModalities = ModelModalities.Normalize(input),
                OutputModalities = ModelModalities.Normalize(output),
                SupportsVision = input.Contains("image", StringComparer.OrdinalIgnoreCase),
                SupportsFunctionCalling = supported.Contains("tools", StringComparer.OrdinalIgnoreCase),
                SupportsImageGeneration = output.Contains("image", StringComparer.OrdinalIgnoreCase),
                SupportsVideoGeneration = output.Contains("video", StringComparer.OrdinalIgnoreCase)
            };
            var effective = ModelCapabilityResolver.Resolve(model, association);
            var current = new CapabilitiesDriftPayload
            {
                InputModalities = effective.InputModalities ?? [],
                OutputModalities = effective.OutputModalities ?? [],
                SupportsVision = effective.SupportsVision,
                SupportsFunctionCalling = effective.SupportsFunctionCalling,
                SupportsImageGeneration = effective.SupportsImageGeneration,
                SupportsVideoGeneration = effective.SupportsVideoGeneration
            };

            if (!proposed.InputModalities.SequenceEqual(current.InputModalities, StringComparer.Ordinal)
                || !proposed.OutputModalities.SequenceEqual(current.OutputModalities, StringComparer.Ordinal)
                || proposed.SupportsVision != current.SupportsVision
                || proposed.SupportsFunctionCalling != current.SupportsFunctionCalling
                || proposed.SupportsImageGeneration != current.SupportsImageGeneration
                || proposed.SupportsVideoGeneration != current.SupportsVideoGeneration)
            {
                detected[DriftType.Capabilities] = (Json(current), Json(proposed));
            }
        }

        private static decimal? ToPerMillion(string? price)
        {
            if (string.IsNullOrEmpty(price))
                return null;
            if (!decimal.TryParse(price, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return null;
            return Math.Round(parsed * 1_000_000m, 4);
        }

        private static decimal? Round4(decimal value) => Math.Round(value, 4);
        private static decimal? Round4(decimal? value) => value.HasValue ? Math.Round(value.Value, 4) : null;

        private static string Json(object value) => AdminJson.Serialize(value, JsonOptions);

        private static ProviderSyncRunDto MapRun(ProviderMetadataSyncRun run) => new()
        {
            Id = run.Id,
            ProviderType = run.ProviderType.ToString(),
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            Status = run.Status,
            TriggeredBy = run.TriggeredBy,
            ModelsFetched = run.ModelsFetched,
            MappingsChecked = run.MappingsChecked,
            ItemsCreated = run.ItemsCreated,
            ItemsUpdated = run.ItemsUpdated,
            ItemsAutoResolved = run.ItemsAutoResolved,
            ErrorMessage = run.ErrorMessage
        };
    }
}
