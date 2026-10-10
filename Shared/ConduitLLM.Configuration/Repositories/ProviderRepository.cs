using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// EF Core reference implementation of configured-provider persistence.
/// </summary>
public sealed class ProviderRepository : IProviderRepository
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int SlowQueryThresholdMs = 500;

    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;
    private readonly ILogger<ProviderRepository> _logger;

    public ProviderRepository(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        ILogger<ProviderRepository> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Provider>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => WithCredentials(context.Providers.AsNoTracking())
                .OrderBy(provider => provider.ProviderType)
                .ToListAsync(cancellationToken),
            "listing providers",
            cancellationToken);

    /// <inheritdoc />
    public async Task<(List<Provider> Items, int TotalCount)> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePagination(page, pageSize);
        return await ExecuteAsync(async context =>
        {
            var query = WithCredentials(context.Providers.AsNoTracking());
            var totalCount = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderBy(provider => provider.ProviderType)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            return (items, totalCount);
        }, $"getting paginated providers (page {page}, size {pageSize})", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Provider?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => WithCredentials(context.Providers.AsNoTracking())
                .FirstOrDefaultAsync(provider => provider.Id == id, cancellationToken),
            $"getting provider by ID {id}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        Provider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.ProviderKeyCredentials.Count > 0)
        {
            throw new InvalidOperationException(
                "Create provider credentials through IProviderKeyCredentialRepository.");
        }

        return await ExecuteAsync(async context =>
        {
            var now = DateTime.UtcNow;
            if (provider.CreatedAt == default)
            {
                provider.CreatedAt = now;
            }

            provider.UpdatedAt = now;
            context.Providers.Add(provider);
            await context.SaveChangesAsync(cancellationToken);
            return provider.Id;
        }, "creating provider", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Provider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.UpdatedAt = DateTime.UtcNow;

        try
        {
            return await ExecuteAsync(async context =>
            {
                context.Entry(provider).State = EntityState.Modified;
                return await context.SaveChangesAsync(cancellationToken) > 0;
            }, $"updating provider ID {provider.Id}", cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(async context =>
        {
            var provider = await context.Providers.FindAsync([id], cancellationToken);
            if (provider is null)
            {
                return false;
            }

            context.Providers.Remove(provider);
            return await context.SaveChangesAsync(cancellationToken) > 0;
        }, $"deleting provider ID {id}", cancellationToken);

    /// <inheritdoc />
    public async Task<Dictionary<int, string>> GetProviderNameMapAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.Providers
                .AsNoTracking()
                .ToDictionaryAsync(
                    provider => provider.Id,
                    provider => provider.ProviderName ?? provider.ProviderType.ToString(),
                    cancellationToken),
            "getting provider name map",
            cancellationToken);

    /// <inheritdoc />
    public async Task<int> CountAsync(
        bool? enabledOnly,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => enabledOnly.HasValue
                ? context.Providers.CountAsync(
                    provider => provider.IsEnabled == enabledOnly.Value,
                    cancellationToken)
                : context.Providers.CountAsync(cancellationToken),
            $"counting providers (enabledOnly: {enabledOnly})",
            cancellationToken);

    private static IQueryable<Provider> WithCredentials(IQueryable<Provider> query) =>
        query.Include(provider => provider.ProviderKeyCredentials);

    private static (int Page, int PageSize) NormalizePagination(int page, int pageSize)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1)
        {
            pageSize = DefaultPageSize;
        }

        return (page, Math.Min(pageSize, MaxPageSize));
    }

    private async Task<TResult> ExecuteAsync<TResult>(
        Func<ConduitDbContext, Task<TResult>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var result = await operation(context);
        stopwatch.Stop();

        if (stopwatch.ElapsedMilliseconds > SlowQueryThresholdMs)
        {
            _logger.LogWarning(
                "Slow repository operation: {OperationName} Provider took {ElapsedMs}ms",
                operationName,
                stopwatch.ElapsedMilliseconds);
        }

        return result;
    }
}
