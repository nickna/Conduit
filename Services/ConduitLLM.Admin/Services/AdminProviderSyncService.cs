using System.Text.Json;

using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Models.ProviderSync;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Utilities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Admin.Services
{
    /// <inheritdoc />
    public class AdminProviderSyncService : EventPublishingServiceBase, IAdminProviderSyncService
    {
        private readonly IDbContextFactory<ConduitDbContext> _dbFactory;
        private readonly IAdminModelCostService _modelCostService;

        private static readonly JsonSerializerOptions JsonOptions = ConduitLLM.Core.Serialization.ConduitJsonOptions.Compact;

        public AdminProviderSyncService(
            IDbContextFactory<ConduitDbContext> dbFactory,
            IAdminModelCostService modelCostService,
            ILogger<AdminProviderSyncService> logger,
            IEventBus? eventBus = null)
            : base(eventBus, logger)
        {
            _dbFactory = dbFactory;
            _modelCostService = modelCostService;
        }

        /// <inheritdoc />
        public async Task<List<DriftItemDto>> GetDriftItemsAsync(string? status, string? driftType, int? providerId, int page, int pageSize)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var query = db.ProviderMetadataDriftItems
                .Include(x => x.Mapping)
                .AsNoTracking();

            if (!string.IsNullOrEmpty(status) && Enum.TryParse<DriftStatus>(status, true, out var s))
                query = query.Where(x => x.Status == s);
            else if (string.IsNullOrEmpty(status))
                query = query.Where(x => x.Status == DriftStatus.Pending);

            if (!string.IsNullOrEmpty(driftType) && Enum.TryParse<DriftType>(driftType, true, out var dt))
                query = query.Where(x => x.DriftType == dt);
            if (providerId.HasValue)
                query = query.Where(x => x.ProviderId == providerId.Value);

            var items = await query
                .OrderByDescending(x => x.LastDetectedAt)
                .Skip(Math.Max(0, page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var providerNames = await LoadProviderNamesAsync(db, items.Select(i => i.ProviderId));
            return items.Select(i => MapItem(i, providerNames)).ToList();
        }

        /// <inheritdoc />
        public async Task<DriftItemDto?> GetDriftItemAsync(int id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var item = await db.ProviderMetadataDriftItems.Include(x => x.Mapping).AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
                return null;
            var providerNames = await LoadProviderNamesAsync(db, new[] { item.ProviderId });
            return MapItem(item, providerNames);
        }

        /// <inheritdoc />
        public async Task<DriftActionResultDto> ApplyAsync(int id, string actor)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var item = await db.ProviderMetadataDriftItems.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
                return new DriftActionResultDto { Id = id, Success = false, Error = "Drift item not found." };
            if (item.Status != DriftStatus.Pending)
                return new DriftActionResultDto { Id = id, Success = false, Error = $"Drift item is already {item.Status}." };

            var mapping = await db.ModelProviderMappings
                .Include(m => m.ModelProviderTypeAssociation).ThenInclude(a => a.Model)
                .Include(m => m.ModelProviderTypeAssociation).ThenInclude(a => a.ModelCost)
                .FirstOrDefaultAsync(m => m.Id == item.ModelProviderMappingId);
            if (mapping == null)
                return new DriftActionResultDto { Id = id, Success = false, Error = "Mapping no longer exists." };

            // Staleness guard: if Conduit's current values changed since detection, refuse to apply.
            var liveCurrent = RecomputeCurrentJson(item.DriftType, mapping);
            if (liveCurrent != null && !JsonEquivalent(liveCurrent, item.CurrentValuesJson))
            {
                return new DriftActionResultDto
                {
                    Id = id,
                    Success = false,
                    Stale = true,
                    Error = "Current values changed since detection — re-run sync and review again."
                };
            }

            try
            {
                await ApplyDriftAsync(db, item, mapping);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to apply drift item {DriftItemId}", id);
                return new DriftActionResultDto { Id = id, Success = false, Error = ex.Message };
            }

            item.Status = DriftStatus.Applied;
            item.ResolvedAt = DateTime.UtcNow;
            item.ResolvedBy = actor;
            await db.SaveChangesAsync();

            return new DriftActionResultDto { Id = id, Success = true };
        }

        /// <inheritdoc />
        public async Task<DriftActionResultDto> DismissAsync(int id, string actor)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var item = await db.ProviderMetadataDriftItems.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
                return new DriftActionResultDto { Id = id, Success = false, Error = "Drift item not found." };
            if (item.Status != DriftStatus.Pending)
                return new DriftActionResultDto { Id = id, Success = false, Error = $"Drift item is already {item.Status}." };

            item.Status = DriftStatus.Dismissed;
            item.ResolvedAt = DateTime.UtcNow;
            item.ResolvedBy = actor;
            await db.SaveChangesAsync();
            return new DriftActionResultDto { Id = id, Success = true };
        }

        /// <inheritdoc />
        public async Task<BulkDriftActionResponse> ApplyBulkAsync(IReadOnlyList<int> ids, string actor)
        {
            var results = new List<DriftActionResultDto>();
            foreach (var id in ids)
                results.Add(await ApplyAsync(id, actor));
            return AggregateBulk(results);
        }

        /// <inheritdoc />
        public async Task<BulkDriftActionResponse> DismissBulkAsync(IReadOnlyList<int> ids, string actor)
        {
            var results = new List<DriftActionResultDto>();
            foreach (var id in ids)
                results.Add(await DismissAsync(id, actor));
            return AggregateBulk(results);
        }

        /// <inheritdoc />
        public async Task<List<ProviderSyncRunDto>> GetSyncRunsAsync(int page, int pageSize)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var runs = await db.ProviderMetadataSyncRuns.AsNoTracking()
                .OrderByDescending(r => r.Id)
                .Skip(Math.Max(0, page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return runs.Select(r => new ProviderSyncRunDto
            {
                Id = r.Id,
                ProviderType = r.ProviderType.ToString(),
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                Status = r.Status,
                TriggeredBy = r.TriggeredBy,
                ModelsFetched = r.ModelsFetched,
                MappingsChecked = r.MappingsChecked,
                ItemsCreated = r.ItemsCreated,
                ItemsUpdated = r.ItemsUpdated,
                ItemsAutoResolved = r.ItemsAutoResolved,
                ErrorMessage = r.ErrorMessage
            }).ToList();
        }

        private async Task ApplyDriftAsync(ConduitDbContext db, ProviderMetadataDriftItem item, ModelProviderMapping mapping)
        {
            var mpta = mapping.ModelProviderTypeAssociation;

            switch (item.DriftType)
            {
                case DriftType.Pricing:
                {
                    var proposed = Deserialize<PricingDriftPayload>(item.ProposedValuesJson);
                    var cost = mpta?.ModelCost
                        ?? throw new InvalidOperationException("Mapping has no ModelCost to update.");
                    var dto = BuildUpdateDto(cost, proposed);
                    await _modelCostService.UpdateModelCostAsync(cost.Id, dto); // fires ModelCostChanged -> cache invalidation
                    break;
                }
                case DriftType.MissingCost:
                {
                    var proposed = Deserialize<PricingDriftPayload>(item.ProposedValuesJson);
                    if (mpta == null)
                        throw new InvalidOperationException("Mapping has no provider-type association to associate a cost with.");
                    var dto = BuildCreateDto(item.OpenRouterModelId, mpta.Id, proposed);
                    await _modelCostService.CreateModelCostAsync(dto); // fires ModelCostChanged (Created)
                    break;
                }
                case DriftType.ContextWindow:
                {
                    var proposed = Deserialize<ContextWindowDriftPayload>(item.ProposedValuesJson);
                    if (mpta == null)
                        throw new InvalidOperationException("Mapping has no provider-type association to update.");
                    // Provider-scoped override (safe for other providers of the same canonical model).
                    var tracked = await db.ModelProviderTypeAssociations.FirstAsync(a => a.Id == mpta.Id);
                    if (proposed.MaxInputTokens.HasValue) tracked.MaxInputTokens = proposed.MaxInputTokens;
                    if (proposed.MaxOutputTokens.HasValue) tracked.MaxOutputTokens = proposed.MaxOutputTokens;
                    await db.SaveChangesAsync();
                    break;
                }
                case DriftType.Capabilities:
                {
                    var proposed = Deserialize<CapabilitiesDriftPayload>(item.ProposedValuesJson);
                    if (mpta?.Model == null)
                        throw new InvalidOperationException("Mapping has no model to update.");
                    // OpenRouter reports provider-scoped capabilities; do not mutate other providers.
                    var tracked = await db.ModelProviderTypeAssociations.FirstAsync(a => a.Id == mpta.Id);
                    tracked.InputModalitiesJson = ModelModalities.Serialize(proposed.InputModalities);
                    tracked.OutputModalitiesJson = ModelModalities.Serialize(proposed.OutputModalities);
                    tracked.OperationalCapabilitiesJson = ModelCapabilityResolver.SerializeOverrides(new()
                    {
                        SupportsVision = proposed.SupportsVision,
                        SupportsFunctionCalling = proposed.SupportsFunctionCalling,
                        SupportsImageGeneration = proposed.SupportsImageGeneration,
                        SupportsVideoGeneration = proposed.SupportsVideoGeneration
                    });
                    tracked.CapabilitySource = ModelCapabilitySource.ProviderApi;
                    tracked.CapabilitiesLastVerifiedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    break;
                }
                case DriftType.ModelRemoved:
                case DriftType.ModelDeprecated:
                {
                    // Disable the mapping so it stops routing to the removed/deprecated model.
                    var tracked = await db.ModelProviderMappings.FirstAsync(m => m.Id == mapping.Id);
                    tracked.IsEnabled = false;
                    await db.SaveChangesAsync();
                    break;
                }
            }
        }

        private static string? RecomputeCurrentJson(DriftType driftType, ModelProviderMapping mapping)
        {
            var mpta = mapping.ModelProviderTypeAssociation;
            var model = mpta?.Model;
            var cost = mpta?.ModelCost;

            switch (driftType)
            {
                case DriftType.Pricing:
                    if (cost == null) return null;
                    return Serialize(new PricingDriftPayload
                    {
                        InputPerMillion = Math.Round(cost.InputCostPerMillionTokens, 4),
                        OutputPerMillion = Math.Round(cost.OutputCostPerMillionTokens, 4),
                        CachedInputPerMillion = cost.CachedInputCostPerMillionTokens.HasValue ? Math.Round(cost.CachedInputCostPerMillionTokens.Value, 4) : null,
                        CachedWritePerMillion = cost.CachedInputWriteCostPerMillionTokens.HasValue ? Math.Round(cost.CachedInputWriteCostPerMillionTokens.Value, 4) : null
                    });
                case DriftType.ContextWindow:
                    return Serialize(new ContextWindowDriftPayload
                    {
                        MaxInputTokens = mpta?.MaxInputTokens ?? model?.MaxInputTokens,
                        MaxOutputTokens = mpta?.MaxOutputTokens ?? model?.MaxOutputTokens
                    });
                case DriftType.Capabilities:
                    if (model == null) return null;
                    var effective = ModelCapabilityResolver.Resolve(model, mpta);
                    return Serialize(new CapabilitiesDriftPayload
                    {
                        InputModalities = effective.InputModalities ?? [],
                        OutputModalities = effective.OutputModalities ?? [],
                        SupportsVision = effective.SupportsVision,
                        SupportsFunctionCalling = effective.SupportsFunctionCalling,
                        SupportsImageGeneration = effective.SupportsImageGeneration,
                        SupportsVideoGeneration = effective.SupportsVideoGeneration
                    });
                default:
                    return null; // MissingCost / ModelRemoved / ModelDeprecated: no meaningful current snapshot to guard.
            }
        }

        private static UpdateModelCostDto BuildUpdateDto(ModelCost cost, PricingDriftPayload proposed) => new()
        {
            CostName = cost.CostName,
            PricingModel = cost.PricingModel,
            PricingConfiguration = StructuredJson.ParseObject(cost.PricingConfiguration),
            ModelType = cost.ModelType,
            IsActive = cost.IsActive,
            Priority = cost.Priority,
            Description = cost.Description,
            InputCostPerMillionTokens = proposed.InputPerMillion ?? cost.InputCostPerMillionTokens,
            OutputCostPerMillionTokens = proposed.OutputPerMillion ?? cost.OutputCostPerMillionTokens,
            EmbeddingCostPerMillionTokens = cost.EmbeddingCostPerMillionTokens,
            BatchProcessingMultiplier = cost.BatchProcessingMultiplier,
            SupportsBatchProcessing = cost.SupportsBatchProcessing,
            CachedInputCostPerMillionTokens = proposed.CachedInputPerMillion ?? cost.CachedInputCostPerMillionTokens,
            CachedInputWriteCostPerMillionTokens = proposed.CachedWritePerMillion ?? cost.CachedInputWriteCostPerMillionTokens,
            CostPerSearchUnit = cost.CostPerSearchUnit,
            AudioCostPerMinute = cost.AudioCostPerMinute,
            AudioCostPerThousandCharacters = cost.AudioCostPerThousandCharacters,
            ModelProviderTypeAssociationIds = null // leave associations unchanged
        };

        private static CreateModelCostDto BuildCreateDto(string modelId, int mptaId, PricingDriftPayload proposed) => new()
        {
            CostName = $"openrouter/{modelId} (synced)",
            PricingModel = PricingModel.Standard,
            ModelType = "chat",
            InputCostPerMillionTokens = proposed.InputPerMillion ?? 0m,
            OutputCostPerMillionTokens = proposed.OutputPerMillion ?? 0m,
            CachedInputCostPerMillionTokens = proposed.CachedInputPerMillion,
            CachedInputWriteCostPerMillionTokens = proposed.CachedWritePerMillion,
            ModelProviderTypeAssociationIds = new List<int> { mptaId }
        };

        private static async Task<Dictionary<int, string>> LoadProviderNamesAsync(ConduitDbContext db, IEnumerable<int> providerIds)
        {
            var ids = providerIds.Distinct().ToList();
            return await db.Providers.AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.ProviderName);
        }

        private static DriftItemDto MapItem(ProviderMetadataDriftItem item, IReadOnlyDictionary<int, string> providerNames) => new()
        {
            Id = item.Id,
            ModelProviderMappingId = item.ModelProviderMappingId,
            ModelAlias = item.Mapping?.ModelAlias ?? string.Empty,
            ProviderId = item.ProviderId,
            ProviderName = providerNames.TryGetValue(item.ProviderId, out var name) ? name : string.Empty,
            OpenRouterModelId = item.OpenRouterModelId,
            DriftType = item.DriftType.ToString(),
            Status = item.Status.ToString(),
            CurrentValues = StructuredJson.ParseObject(item.CurrentValuesJson) ?? new(),
            ProposedValues = StructuredJson.ParseObject(item.ProposedValuesJson) ?? new(),
            FirstDetectedAt = item.FirstDetectedAt,
            LastDetectedAt = item.LastDetectedAt,
            ResolvedAt = item.ResolvedAt,
            ResolvedBy = item.ResolvedBy
        };

        private static BulkDriftActionResponse AggregateBulk(List<DriftActionResultDto> results) => new()
        {
            Results = results,
            SucceededCount = results.Count(r => r.Success),
            FailedCount = results.Count(r => !r.Success)
        };

        private static T Deserialize<T>(string json) where T : new()
            => AdminJson.Deserialize<T>(json, JsonOptions) ?? new T();

        private static string Serialize(object value) => AdminJson.Serialize(value, JsonOptions);

        private static bool JsonEquivalent(string a, string b)
            => string.Equals(a, b, StringComparison.Ordinal);
    }
}
