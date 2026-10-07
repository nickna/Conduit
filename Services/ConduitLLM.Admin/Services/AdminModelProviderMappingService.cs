using ConduitLLM.Core.Extensions;
using System.Text.Json;

using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Services;

using ConduitLLM.Configuration.Messaging;
namespace ConduitLLM.Admin.Services;

/// <summary>
/// Service for managing model provider mappings through the Admin API
/// </summary>
public class AdminModelProviderMappingService : EventPublishingServiceBase, IAdminModelProviderMappingService
{
    private readonly IModelProviderMappingRepository _mappingRepository;
    private readonly IProviderRepository _providerRepository;
    private readonly IModelRepository _modelRepository;
    private readonly ILogger<AdminModelProviderMappingService> _logger;

    /// <summary>
    /// Initializes a new instance of the AdminModelProviderMappingService class
    /// </summary>
    /// <param name="mappingRepository">The model provider mapping repository</param>
    /// <param name="providerRepository">The provider repository</param>
    /// <param name="modelRepository">The model repository</param>
    /// <param name="eventBus">Optional event bus (null if not configured)</param>
    /// <param name="logger">The logger</param>
    public AdminModelProviderMappingService(
        IModelProviderMappingRepository mappingRepository,
        IProviderRepository providerRepository,
        IModelRepository modelRepository,
        ILogger<AdminModelProviderMappingService> logger,
        IEventBus? eventBus = null)
        : base(eventBus, logger)
    {
        _mappingRepository = mappingRepository ?? throw new ArgumentNullException(nameof(mappingRepository));
        _providerRepository = providerRepository ?? throw new ArgumentNullException(nameof(providerRepository));
        _modelRepository = modelRepository ?? throw new ArgumentNullException(nameof(modelRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        LogEventPublishingConfiguration(nameof(AdminModelProviderMappingService));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ModelProviderMapping>> GetAllMappingsAsync()
    {
        _logger.LogDebug("Getting all model provider mappings");
        return await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
            _mappingRepository.GetPaginatedAsync);
    }

    /// <inheritdoc />
    public async Task<ModelProviderMapping?> GetMappingByIdAsync(int id)
    {
        _logger.LogDebug("Getting model provider mapping with ID: {Id}", id);
        return await _mappingRepository.GetByIdAsync(id);
    }

    /// <inheritdoc />
    public async Task<ModelProviderMapping?> GetMappingByModelIdAsync(int modelId)
    {
        _logger.LogDebug("Getting model provider mapping for model ID: {ModelId}", modelId);
        var mappings = await _mappingRepository.GetByModelIdAsync(modelId);
        return mappings.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ModelProviderMapping>> GetMappingsByModelIdAsync(int modelId)
    {
        _logger.LogDebug("Getting all model provider mappings for model ID: {ModelId}", modelId);
        return await _mappingRepository.GetByModelIdAsync(modelId);
    }

    /// <inheritdoc />
    public async Task<bool> AddMappingAsync(ModelProviderMapping mapping)
    {
        try
        {
            _logger.LogDebug("Adding new model provider mapping for model ID: {ModelId}", LoggingSanitizer.S(mapping.ModelAlias));

            // Validate provider exists by ID
            var provider = await _providerRepository.GetByIdAsync(mapping.ProviderId);
            if (provider == null)
            {
                _logger.LogWarning("Provider not found with ID: {ProviderId}", mapping.ProviderId);
                return false;
            }

            var association = await _modelRepository.GetProviderTypeAssociationByIdAsync(
                mapping.ModelProviderTypeAssociationId);
            if (!IsCompatibleAssociation(mapping, provider, association))
            {
                _logger.LogWarning(
                    "Association {AssociationId} is not compatible with provider {ProviderId} and model {ProviderModelId}",
                    mapping.ModelProviderTypeAssociationId,
                    mapping.ProviderId,
                    LoggingSanitizer.S(mapping.ProviderModelId));
                return false;
            }

            var existingMappings = await _mappingRepository.GetAllByModelNameAsync(mapping.ModelAlias);
            if (existingMappings.Any(existing => existing.ProviderId == mapping.ProviderId))
            {
                _logger.LogWarning("An alias/provider mapping already exists: {ModelId}/{ProviderId}",
                    LoggingSanitizer.S(mapping.ModelAlias), mapping.ProviderId);
                return false;
            }
            if (existingMappings.Any(existing =>
                existing.ModelProviderTypeAssociation?.ModelId != association!.ModelId)) return false;
            if (mapping.RoutingWeight is < 0.1m or > 2.0m) return false;

            // Set timestamps
            mapping.CreatedAt = DateTime.UtcNow;
            mapping.UpdatedAt = DateTime.UtcNow;

            // Add the mapping
            await _mappingRepository.CreateAsync(mapping);

            // Publish ModelMappingChanged event for creation
            await PublishEventAsync(
                new ModelMappingChanged
                {
                    MappingId = mapping.Id,
                    ModelAlias = mapping.ModelAlias,
                    ProviderId = mapping.ProviderId,
                    IsEnabled = mapping.IsEnabled,
                    ChangeType = "Created",
                    CorrelationId = Guid.NewGuid().ToString()
                },
                $"create model mapping for {mapping.ModelAlias}",
                new { ModelAlias = mapping.ModelAlias, ProviderId = mapping.ProviderId });

            _logger.LogInformation("Created model provider mapping {ModelAlias} -> provider {ProviderId}",
                LoggingSanitizer.S(mapping.ModelAlias), mapping.ProviderId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding model provider mapping for model ID: {ModelId}", LoggingSanitizer.S(mapping.ModelAlias));
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateMappingAsync(ModelProviderMapping mapping)
    {
        try
        {
            _logger.LogDebug("Updating model provider mapping with ID: {Id}", mapping.Id);

            // Check if the mapping exists
            var existingMapping = await _mappingRepository.GetByIdAsync(mapping.Id);
            if (existingMapping == null)
            {
                _logger.LogWarning("Model provider mapping not found with ID: {Id}", mapping.Id);
                return false;
            }

            // Validate provider exists by ID
            var provider = await _providerRepository.GetByIdAsync(mapping.ProviderId);
            if (provider == null)
            {
                _logger.LogWarning("Provider not found with ID: {ProviderId}", mapping.ProviderId);
                return false;
            }


            var association = await _modelRepository.GetProviderTypeAssociationByIdAsync(
                mapping.ModelProviderTypeAssociationId);
            if (!IsCompatibleAssociation(mapping, provider, association))
            {
                _logger.LogWarning(
                    "Association {AssociationId} is not compatible with provider {ProviderId} and model {ProviderModelId}",
                    mapping.ModelProviderTypeAssociationId,
                    mapping.ProviderId,
                    LoggingSanitizer.S(mapping.ProviderModelId));
                return false;
            }

            var aliasMappings = await _mappingRepository.GetAllByModelNameAsync(mapping.ModelAlias);
            if (aliasMappings.Any(other => other.Id != mapping.Id && other.ProviderId == mapping.ProviderId) ||
                aliasMappings.Any(other => other.Id != mapping.Id &&
                    other.ModelProviderTypeAssociation?.ModelId != association!.ModelId))
            {
                return false;
            }
            if (mapping.RoutingWeight is < 0.1m or > 2.0m) return false;

            // Update properties that can be modified
            existingMapping.ModelAlias = mapping.ModelAlias;
            existingMapping.ProviderModelId = mapping.ProviderModelId;
            existingMapping.ProviderId = mapping.ProviderId;
            existingMapping.ModelProviderTypeAssociationId = mapping.ModelProviderTypeAssociationId;
            existingMapping.IsEnabled = mapping.IsEnabled;
            existingMapping.ProviderOptions = mapping.ProviderOptions;
            existingMapping.RoutingPriority = mapping.RoutingPriority;
            existingMapping.RoutingWeight = mapping.RoutingWeight;

            existingMapping.UpdatedAt = DateTime.UtcNow;

            // Update the mapping
            var result = await _mappingRepository.UpdateAsync(existingMapping);
            
            if (result)
            {
                // Publish ModelMappingChanged event for update
                await PublishEventAsync(
                    new ModelMappingChanged
                    {
                        MappingId = existingMapping.Id,
                        ModelAlias = existingMapping.ModelAlias,
                        ProviderId = existingMapping.ProviderId,
                        IsEnabled = existingMapping.IsEnabled,
                        ChangeType = "Updated",
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"update model mapping {existingMapping.Id}",
                    new { ModelAlias = existingMapping.ModelAlias, ProviderId = existingMapping.ProviderId });

                _logger.LogInformation("Updated model provider mapping {MappingId} ({ModelAlias} -> provider {ProviderId})",
                    existingMapping.Id, LoggingSanitizer.S(existingMapping.ModelAlias), existingMapping.ProviderId);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating model provider mapping with ID: {Id}", mapping.Id);
            return false;
        }
    }

    private static bool IsCompatibleAssociation(
        ModelProviderMapping mapping,
        Provider provider,
        ModelProviderTypeAssociation? association) =>
        association is
        {
            IsEnabled: true
        } &&
        association.Provider == provider.ProviderType &&
        association.Identifier.Equals(mapping.ProviderModelId, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<bool> DeleteMappingAsync(int id)
    {
        try
        {
            _logger.LogDebug("Deleting model provider mapping with ID: {Id}", id);

            // Check if the mapping exists
            var existingMapping = await _mappingRepository.GetByIdAsync(id);
            if (existingMapping == null)
            {
                _logger.LogWarning("Model provider mapping not found with ID: {Id}", id);
                return false;
            }

            // Delete the mapping
            var result = await _mappingRepository.DeleteAsync(id);
            
            if (result)
            {
                // Publish ModelMappingChanged event for deletion
                await PublishEventAsync(
                    new ModelMappingChanged
                    {
                        MappingId = existingMapping.Id,
                        ModelAlias = existingMapping.ModelAlias,
                        ProviderId = existingMapping.ProviderId,
                        IsEnabled = existingMapping.IsEnabled,
                        ChangeType = "Deleted",
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"delete model mapping {existingMapping.Id}",
                    new { ModelAlias = existingMapping.ModelAlias, ProviderId = existingMapping.ProviderId });

                _logger.LogInformation("Deleted model provider mapping {MappingId} ({ModelAlias})",
                    existingMapping.Id, LoggingSanitizer.S(existingMapping.ModelAlias));
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting model provider mapping with ID: {Id}", id);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Provider>> GetProvidersAsync()
    {
        try
        {
            _logger.LogDebug("Getting all providers");
            return await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
                _providerRepository.GetPaginatedAsync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting providers");
            return Enumerable.Empty<Provider>();
        }
    }

    /// <inheritdoc />
    public async Task<BulkModelMappingPreviewResponse> PreviewBulkMappingsAsync(
        BulkModelMappingPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await BuildBulkResolutionContextAsync(request.Mappings, cancellationToken);
        var seen = new HashSet<MappingIdentity>();
        var items = request.Mappings
            .Select((item, index) => ResolveBulkItem(item, index, context, seen).Result)
            .ToList();

        return new BulkModelMappingPreviewResponse
        {
            Items = items,
            TotalProcessed = items.Count,
            ConflictCount = items.Count(item => item.HasConflict)
        };
    }

    /// <inheritdoc />
    public async Task<BulkModelMappingCreateResponse> CreateBulkMappingsAsync(
        BulkModelMappingCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogDebug("Creating {Count} resolved model provider mappings", request.Mappings.Count);
        var response = new BulkModelMappingCreateResponse { TotalProcessed = request.Mappings.Count };
        var context = await BuildBulkResolutionContextAsync(request.Mappings, cancellationToken);

        for (var index = 0; index < request.Mappings.Count; index++)
        {
            var item = request.Mappings[index];
            var resolved = ResolveBulkItem(item, index, context);

            if (resolved.ExistingMapping != null)
            {
                if (resolved.Association != null &&
                    IsEquivalentMapping(resolved.ExistingMapping, item, resolved.Association))
                {
                    response.Existing.Add(resolved.ExistingMapping.ToDto());
                }
                else
                {
                    response.Failed.Add(Fail(
                        item,
                        index,
                        BulkModelMappingErrorType.ExistingMappingMismatch,
                        $"Alias '{item.ModelAlias}' already maps provider {item.ProviderId} to a different model.",
                        resolved.Association?.Id,
                        resolved.ExistingMapping));
                }

                continue;
            }

            if (resolved.Result.ErrorType != null || resolved.Association == null || resolved.Provider == null)
            {
                response.Failed.Add(resolved.Result);
                continue;
            }

            var now = DateTime.UtcNow;
            var mapping = new ModelProviderMapping
            {
                ModelAlias = item.ModelAlias.Trim(),
                ProviderId = item.ProviderId,
                ProviderModelId = item.ProviderModelId.Trim(),
                ModelProviderTypeAssociationId = resolved.Association.Id,
                RoutingPriority = request.Priority,
                RoutingWeight = request.Weight,
                IsEnabled = request.IsEnabled,
                CreatedAt = now,
                UpdatedAt = now
            };

            try
            {
                mapping.Id = await _mappingRepository.CreateAsync(mapping, cancellationToken);

                // Resolution queries are intentionally no-tracking and use repository-owned
                // DbContexts. Passing those detached navigation objects to CreateAsync causes
                // EF Core's Add graph traversal to mark the existing provider and association
                // as Added, resulting in duplicate primary-key inserts. Persist foreign keys
                // only, then restore the resolved objects for response projection and the
                // in-memory bulk conflict context.
                mapping.ModelProviderTypeAssociation = resolved.Association;
                mapping.Provider = resolved.Provider;

                response.Created.Add(mapping.ToDto());
                AddMappingToContext(context, mapping);
                await PublishBulkMappingCreatedAsync(mapping);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A concurrent retry may have won the unique alias/provider insert. Treat an
                // equivalent row as idempotent; otherwise preserve partial-success semantics.
                ModelProviderMapping? racedMapping = null;
                try
                {
                    racedMapping = (await _mappingRepository.GetAllByModelNameAsync(
                        item.ModelAlias.Trim(), cancellationToken))
                        .FirstOrDefault(existing => existing.ProviderId == item.ProviderId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception lookupException)
                {
                    _logger.LogWarning(
                        lookupException,
                        "Could not check for a concurrent bulk mapping retry at index {Index}",
                        index);
                }

                if (racedMapping != null && IsEquivalentMapping(racedMapping, item, resolved.Association))
                {
                    response.Existing.Add(racedMapping.ToDto());
                    AddMappingToContext(context, racedMapping);
                    continue;
                }

                _logger.LogError(
                    ex,
                    "Error processing bulk mapping at index {Index} for alias {ModelAlias} and provider {ProviderId}",
                    index,
                    LoggingSanitizer.S(item.ModelAlias),
                    item.ProviderId);
                response.Failed.Add(Fail(
                    item,
                    index,
                    BulkModelMappingErrorType.SystemError,
                    "The mapping could not be created.",
                    resolved.Association.Id));
            }
        }

        response.CreatedCount = response.Created.Count;
        response.ExistingCount = response.Existing.Count;
        response.SuccessCount = response.CreatedCount + response.ExistingCount;
        response.FailureCount = response.Failed.Count;
        response.IsSuccess = response.FailureCount == 0;
        response.IsPartialSuccess = response.SuccessCount > 0 && response.FailureCount > 0;

        _logger.LogInformation(
            "Bulk mapping operation completed. Created: {Created}, Existing: {Existing}, Failed: {Failed}",
            response.CreatedCount,
            response.ExistingCount,
            response.FailureCount);

        return response;
    }

    private async Task<BulkResolutionContext> BuildBulkResolutionContextAsync(
        IReadOnlyCollection<BulkModelMappingItemDto> items,
        CancellationToken cancellationToken)
    {
        var providers = (await _providerRepository.ListAsync(cancellationToken))
            .ToDictionary(provider => provider.Id);
        var existingMappings = await _mappingRepository.GetAllUnboundedAsync(cancellationToken);
        var identifiers = items
            .Select(item => item.ProviderModelId?.Trim() ?? string.Empty)
            .Where(identifier => identifier.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var associations = await _modelRepository.GetProviderTypeAssociationsByIdentifiersAsync(
            identifiers,
            cancellationToken);

        return new BulkResolutionContext(providers, associations, existingMappings);
    }

    private static ResolvedBulkItem ResolveBulkItem(
        BulkModelMappingItemDto item,
        int index,
        BulkResolutionContext context,
        HashSet<MappingIdentity>? seen = null)
    {
        if (string.IsNullOrWhiteSpace(item.ModelAlias) || string.IsNullOrWhiteSpace(item.ProviderModelId) ||
            item.ProviderId <= 0)
        {
            return new ResolvedBulkItem(Fail(
                item,
                index,
                BulkModelMappingErrorType.Validation,
                "Model alias, provider ID, and provider model ID are required."));
        }

        if (!context.Providers.TryGetValue(item.ProviderId, out var provider))
        {
            return new ResolvedBulkItem(Fail(
                item,
                index,
                BulkModelMappingErrorType.ProviderNotFound,
                $"Provider {item.ProviderId} was not found."));
        }

        var identifierMatches = context.Associations
            .Where(association => association.Identifier.Equals(
                item.ProviderModelId.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var providerMatches = identifierMatches
            .Where(association => association.Provider == provider.ProviderType)
            .ToList();

        if (providerMatches.Count == 0)
        {
            var errorType = identifierMatches.Count == 0
                ? BulkModelMappingErrorType.AssociationNotFound
                : BulkModelMappingErrorType.AssociationProviderMismatch;
            var message = identifierMatches.Count == 0
                ? $"No model association exists for provider identifier '{item.ProviderModelId}'."
                : $"Model identifier '{item.ProviderModelId}' is not associated with provider type {provider.ProviderType}.";
            return new ResolvedBulkItem(Fail(item, index, errorType, message), Provider: provider);
        }

        var enabledMatches = providerMatches.Where(association => association.IsEnabled).ToList();
        if (enabledMatches.Count == 0)
        {
            return new ResolvedBulkItem(Fail(
                item,
                index,
                BulkModelMappingErrorType.AssociationDisabled,
                $"The model association for '{item.ProviderModelId}' is disabled."),
                Provider: provider);
        }

        var exactMatches = enabledMatches
            .Where(association => association.Identifier.Equals(
                item.ProviderModelId.Trim(), StringComparison.Ordinal))
            .ToList();
        var candidates = exactMatches.Count > 0 ? exactMatches : enabledMatches;
        if (candidates.Count != 1)
        {
            return new ResolvedBulkItem(Fail(
                item,
                index,
                BulkModelMappingErrorType.AmbiguousAssociation,
                $"Multiple model associations match '{item.ProviderModelId}' for provider type {provider.ProviderType}."),
                Provider: provider);
        }

        var association = candidates[0];
        var identity = MappingIdentity.From(item.ModelAlias, item.ProviderId);
        if (context.ExistingByIdentity.TryGetValue(identity, out var existingMapping))
        {
            var equivalent = IsEquivalentMapping(existingMapping, item, association);
            return new ResolvedBulkItem(
                Fail(
                    item,
                    index,
                    equivalent
                        ? BulkModelMappingErrorType.ExistingMapping
                        : BulkModelMappingErrorType.ExistingMappingMismatch,
                    equivalent
                        ? $"A mapping for alias '{item.ModelAlias}' and provider {item.ProviderId} already exists."
                        : $"Alias '{item.ModelAlias}' already maps provider {item.ProviderId} to a different model.",
                    association.Id,
                    existingMapping),
                association,
                provider,
                existingMapping);
        }

        if (context.ExistingByAlias.TryGetValue(identity.Alias, out var aliasMappings) &&
            aliasMappings.Any(existing =>
                existing.ModelProviderTypeAssociation?.ModelId != association.ModelId))
        {
            return new ResolvedBulkItem(Fail(
                item,
                index,
                BulkModelMappingErrorType.CanonicalModelMismatch,
                $"Alias '{item.ModelAlias}' is already assigned to a different canonical model.",
                association.Id),
                association,
                provider);
        }

        if (seen != null && !seen.Add(identity))
        {
            return new ResolvedBulkItem(Fail(
                item,
                index,
                BulkModelMappingErrorType.DuplicateRequest,
                $"Alias '{item.ModelAlias}' and provider {item.ProviderId} appear more than once in this request.",
                association.Id),
                association,
                provider);
        }

        return new ResolvedBulkItem(
            new BulkModelMappingResolutionDto
            {
                Index = index,
                ModelAlias = item.ModelAlias,
                ProviderId = item.ProviderId,
                ProviderModelId = item.ProviderModelId,
                ModelProviderTypeAssociationId = association.Id
            },
            association,
            provider);
    }

    private async Task PublishBulkMappingCreatedAsync(ModelProviderMapping mapping) =>
        await PublishEventAsync(
            new ModelMappingChanged
            {
                MappingId = mapping.Id,
                ModelAlias = mapping.ModelAlias,
                ProviderId = mapping.ProviderId,
                IsEnabled = mapping.IsEnabled,
                ChangeType = "Created",
                CorrelationId = Guid.NewGuid().ToString()
            },
            $"bulk create model mapping for {mapping.ModelAlias}",
            new { ModelAlias = mapping.ModelAlias, mapping.ProviderId });

    private static bool IsEquivalentMapping(
        ModelProviderMapping mapping,
        BulkModelMappingItemDto item,
        ModelProviderTypeAssociation association) =>
        mapping.ProviderId == item.ProviderId &&
        mapping.ModelProviderTypeAssociationId == association.Id &&
        mapping.ProviderModelId.Equals(item.ProviderModelId.Trim(), StringComparison.OrdinalIgnoreCase);

    private static BulkModelMappingResolutionDto Fail(
        BulkModelMappingItemDto item,
        int index,
        BulkModelMappingErrorType errorType,
        string errorMessage,
        int? associationId = null,
        ModelProviderMapping? existingMapping = null) => new()
        {
            Index = index,
            ModelAlias = item.ModelAlias,
            ProviderId = item.ProviderId,
            ProviderModelId = item.ProviderModelId,
            ModelProviderTypeAssociationId = associationId,
            HasConflict = true,
            ExistingMapping = existingMapping?.ToDto(),
            ErrorType = errorType,
            ErrorMessage = errorMessage
        };

    private static void AddMappingToContext(BulkResolutionContext context, ModelProviderMapping mapping)
    {
        var identity = MappingIdentity.From(mapping.ModelAlias, mapping.ProviderId);
        context.ExistingByIdentity[identity] = mapping;
        if (!context.ExistingByAlias.TryGetValue(identity.Alias, out var aliasMappings))
        {
            aliasMappings = new List<ModelProviderMapping>();
            context.ExistingByAlias[identity.Alias] = aliasMappings;
        }
        aliasMappings.Add(mapping);
    }

    private readonly record struct MappingIdentity(string Alias, int ProviderId)
    {
        public static MappingIdentity From(string alias, int providerId) =>
            new(alias.Trim().ToUpperInvariant(), providerId);
    }

    private sealed class BulkResolutionContext
    {
        public BulkResolutionContext(
            Dictionary<int, Provider> providers,
            List<ModelProviderTypeAssociation> associations,
            List<ModelProviderMapping> existingMappings)
        {
            Providers = providers;
            Associations = associations;
            ExistingByIdentity = existingMappings
                .GroupBy(mapping => MappingIdentity.From(mapping.ModelAlias, mapping.ProviderId))
                .ToDictionary(group => group.Key, group => group.First());
            ExistingByAlias = existingMappings
                .GroupBy(mapping => MappingIdentity.From(mapping.ModelAlias, mapping.ProviderId).Alias)
                .ToDictionary(group => group.Key, group => group.ToList());
        }

        public Dictionary<int, Provider> Providers { get; }
        public List<ModelProviderTypeAssociation> Associations { get; }
        public Dictionary<MappingIdentity, ModelProviderMapping> ExistingByIdentity { get; }
        public Dictionary<string, List<ModelProviderMapping>> ExistingByAlias { get; }
    }

    private sealed record ResolvedBulkItem(
        BulkModelMappingResolutionDto Result,
        ModelProviderTypeAssociation? Association = null,
        Provider? Provider = null,
        ModelProviderMapping? ExistingMapping = null);
    
}
