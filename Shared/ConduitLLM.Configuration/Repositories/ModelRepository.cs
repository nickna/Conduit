using ConduitLLM.Configuration.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// Repository implementation for Model entity operations.
/// Inherits common CRUD operations from RepositoryBase.
/// </summary>
public class ModelRepository : RepositoryBase<Model, int>, IModelRepository
{
    /// <summary>
    /// Creates a new instance of the repository.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory</param>
    /// <param name="logger">The logger</param>
    public ModelRepository(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        ILogger<ModelRepository> logger)
        : base(dbContextFactory, logger)
    {
    }

    /// <inheritdoc/>
    protected override DbSet<Model> GetDbSet(ConduitDbContext context) => context.Models;

    /// <inheritdoc/>
    protected override IQueryable<Model> ApplyDefaultIncludes(IQueryable<Model> query)
    {
        return query
            .Include(m => m.Series)
            .Include(m => m.Identifiers);
    }

    /// <inheritdoc/>
    protected override IQueryable<Model> ApplyDefaultOrdering(IQueryable<Model> query)
    {
        return query.OrderBy(m => m.Name);
    }

    /// <summary>
    /// Applies includes for Series (with Author) and Identifiers — the full detail set.
    /// </summary>
    private static IQueryable<Model> ApplyDetailIncludes(IQueryable<Model> query)
    {
        return query
            .Include(m => m.Series)
                .ThenInclude(s => s.Author)
            .Include(m => m.Identifiers);
    }

    /// <inheritdoc/>
    public async Task<Model?> GetByIdWithDetailsAsync(int id, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            return await ApplyDetailIncludes(GetDbSet(context))
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        }, cancellationToken, $"getting with details for ID {id}");
    }

    /// <inheritdoc/>
    public async Task<List<Model>> GetAllWithDetailsAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            return await ApplyDetailIncludes(GetDbSet(context))
                .AsNoTracking()
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }, cancellationToken, "getting all with details");
    }

    /// <inheritdoc/>
    public async Task<(List<Model> Items, int TotalCount)> GetPaginatedWithFilterAsync(
        int? page = null,
        int? pageSize = null,
        string? search = null,
        string? capability = null,
        bool? hasProviders = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            var query = ApplyDetailIncludes(GetDbSet(context))
                .AsNoTracking();

            // Apply search filter
            if (!string.IsNullOrWhiteSpace(search))
            {
                var lowerSearch = search.ToLower();
                query = query.Where(m => m.Name.ToLower().Contains(lowerSearch));
            }

            // Apply capability filter
            if (!string.IsNullOrWhiteSpace(capability))
            {
                query = capability.ToLower() switch
                {
                    "chat" => query.Where(m => m.SupportsChat),
                    "vision" => query.Where(m => m.SupportsVision),
                    "image_input" => query.Where(m => m.InputModalitiesJson != null &&
                        EF.Functions.JsonContains(m.InputModalitiesJson, "[\"image\"]")),
                    "video_input" => query.Where(m => m.InputModalitiesJson != null &&
                        EF.Functions.JsonContains(m.InputModalitiesJson, "[\"video\"]")),
                    "audio_input" => query.Where(m => m.InputModalitiesJson != null &&
                        EF.Functions.JsonContains(m.InputModalitiesJson, "[\"audio\"]")),
                    "file_input" => query.Where(m => m.InputModalitiesJson != null &&
                        EF.Functions.JsonContains(m.InputModalitiesJson, "[\"file\"]")),
                    "video_understanding" => query.Where(m => m.SupportsChat &&
                        m.InputModalitiesJson != null &&
                        EF.Functions.JsonContains(m.InputModalitiesJson, "[\"video\"]") &&
                        m.OutputModalitiesJson != null &&
                        EF.Functions.JsonContains(m.OutputModalitiesJson, "[\"text\"]")),
                    "image" or "image_generation" => query.Where(m => m.SupportsImageGeneration),
                    "video" or "video_generation" => query.Where(m => m.SupportsVideoGeneration),
                    "embeddings" => query.Where(m => m.SupportsEmbeddings),
                    _ => query
                };
            }

            // Apply provider filter
            if (hasProviders.HasValue)
            {
                query = hasProviders.Value
                    ? query.Where(m => m.Identifiers.Any())
                    : query.Where(m => !m.Identifiers.Any());
            }

            var totalCount = await query.CountAsync(cancellationToken);

            query = query.OrderBy(m => m.Name);

            // Apply pagination if requested
            if (page.HasValue && pageSize.HasValue)
            {
                query = query
                    .Skip((page.Value - 1) * pageSize.Value)
                    .Take(pageSize.Value);
            }

            var items = await query.ToListAsync(cancellationToken);
            return (items, totalCount);
        }, cancellationToken, "getting paginated models with filter");
    }

    /// <inheritdoc/>
    public async Task<Model?> GetByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        return await ExecuteAsync(async context =>
        {
            // First check ModelProviderTypeAssociation table
            var modelIdentifier = await context.Set<ModelProviderTypeAssociation>()
                .Include(mi => mi.Model)
                    .ThenInclude(m => m.Series)
                .AsNoTracking()
                .Where(mi => mi.Identifier == identifier)
                .OrderBy(mi => mi.IsPrimary ? 0 : 1) // Prefer primary identifier
                .FirstOrDefaultAsync(cancellationToken);

            if (modelIdentifier != null)
            {
                return modelIdentifier.Model;
            }

            // Fallback: Check by model name
            return await GetDbSet(context)
                .Include(m => m.Series)
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Name == identifier, cancellationToken);
        }, cancellationToken, $"getting by identifier {identifier}");
    }

    /// <inheritdoc/>
    public async Task<List<Model>> GetBySeriesAsync(int seriesId, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            return await GetDbSet(context)
                .AsNoTracking()
                .Where(m => m.ModelSeriesId == seriesId)
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }, cancellationToken, $"getting by series ID {seriesId}");
    }

    /// <inheritdoc/>
    public async Task<Model?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return await ExecuteAsync(async context =>
        {
            return await GetDbSet(context)
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Name == name, cancellationToken);
        }, cancellationToken, $"getting by name {name}");
    }

    /// <inheritdoc/>
    public async Task<List<Model>> SearchByNameAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
        {
            return new List<Model>();
        }

        var lowerQuery = query.ToLower();
        return await ExecuteAsync(async context =>
        {
            return await GetDbSet(context)
                .AsNoTracking()
                .Where(m => m.Name.ToLower().Contains(lowerQuery) && m.IsActive)
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }, cancellationToken, $"searching by name {query}");
    }

    /// <inheritdoc/>
    public async Task<bool> HasMappingReferencesAsync(int modelId, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            return await context.Set<ModelProviderMapping>()
                .Include(m => m.ModelProviderTypeAssociation)
                .AnyAsync(m => m.ModelProviderTypeAssociation != null && m.ModelProviderTypeAssociation.ModelId == modelId, cancellationToken);
        }, cancellationToken, $"checking mapping references for ID {modelId}");
    }

    /// <inheritdoc/>
    public async Task<List<Model>> GetByProviderAsync(ProviderType providerType, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            // Get model IDs that have identifiers for this provider
            var modelIds = await context.Set<ModelProviderTypeAssociation>()
                .AsNoTracking()
                .Where(mi => mi.Provider == providerType)
                .Select(mi => mi.ModelId)
                .Distinct()
                .ToListAsync(cancellationToken);

            // Return models with those IDs, including series, author, and identifiers
            return await ApplyDetailIncludes(GetDbSet(context))
                .AsNoTracking()
                .Where(m => modelIds.Contains(m.Id))
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }, cancellationToken, $"getting by provider {providerType}");
    }

    /// <inheritdoc/>
    public async Task<ModelProviderTypeAssociation?> GetProviderTypeAssociationByIdAsync(
        int associationId,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            return await context.Set<ModelProviderTypeAssociation>()
                .AsNoTracking()
                .Include(association => association.Model)
                .FirstOrDefaultAsync(association => association.Id == associationId, cancellationToken);
        }, cancellationToken, $"getting provider type association {associationId}");
    }

    /// <inheritdoc/>
    public async Task<List<ModelProviderTypeAssociation>> GetProviderTypeAssociationsByIdentifiersAsync(
        IReadOnlyCollection<string> identifiers,
        CancellationToken cancellationToken = default)
    {
        if (identifiers.Count == 0)
        {
            return new List<ModelProviderTypeAssociation>();
        }

        var normalizedIdentifiers = identifiers
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Select(identifier => identifier.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await ExecuteAsync(async context =>
        {
            return await context.Set<ModelProviderTypeAssociation>()
                .AsNoTracking()
                .Include(association => association.Model)
                .Where(association => normalizedIdentifiers.Contains(association.Identifier.ToLower()))
                .ToListAsync(cancellationToken);
        }, cancellationToken, "getting provider type associations by identifiers");
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteIdentifierAsync(int modelId, int identifierId, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async context =>
        {
            var identifier = await context.Set<ModelProviderTypeAssociation>()
                .FirstOrDefaultAsync(i => i.Id == identifierId && i.ModelId == modelId, cancellationToken);

            if (identifier == null)
            {
                return false;
            }

            context.Set<ModelProviderTypeAssociation>().Remove(identifier);
            int rowsAffected = await context.SaveChangesAsync(cancellationToken);
            return rowsAffected > 0;
        }, cancellationToken, $"deleting identifier {identifierId} for model {modelId}");
    }

    /// <inheritdoc/>
    public async Task<Model> CreateModelAsync(Model model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return await ExecuteWriteAsync(async context =>
        {
            OnBeforeCreate(model);
            GetDbSet(context).Add(model);
            await context.SaveChangesAsync(cancellationToken);
            return model;
        }, "creating", cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Model> UpdateModelAsync(Model model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return await ExecuteWriteAsync(async context =>
        {
            OnBeforeUpdate(model);
            GetDbSet(context).Update(model);
            await context.SaveChangesAsync(cancellationToken);
            return model;
        }, $"updating ID {model.Id}", cancellationToken);
    }
}
