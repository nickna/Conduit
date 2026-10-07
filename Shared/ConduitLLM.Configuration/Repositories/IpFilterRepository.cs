using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Utilities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// EF Core implementation of the explicit IP-filter persistence contract.
/// </summary>
public sealed class IpFilterRepository : IIpFilterRepository
{
    private const int SlowQueryThresholdMs = 500;

    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;
    private readonly ILogger<IpFilterRepository> _logger;

    /// <summary>
    /// Creates the EF Core implementation.
    /// </summary>
    public IpFilterRepository(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        ILogger<IpFilterRepository> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IpFilterEntity>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => Ordered(context.IpFilters.AsNoTracking()).ToListAsync(cancellationToken),
            "listing all filters",
            cancellationToken);

    /// <inheritdoc />
    public async Task<IpFilterEntity?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.IpFilters
                .AsNoTracking()
                .FirstOrDefaultAsync(filter => filter.Id == id, cancellationToken),
            $"getting by ID {id}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IpFilterEntity>> GetEnabledAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => Ordered(context.IpFilters
                    .AsNoTracking()
                    .Where(filter => filter.IsEnabled && filter.VirtualKeyId == null))
                .ToListAsync(cancellationToken),
            "getting enabled global filters",
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IpFilterEntity>> GetEnabledPerKeyAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.IpFilters
                .AsNoTracking()
                .Where(filter => filter.IsEnabled && filter.VirtualKeyId != null)
                .OrderBy(filter => filter.VirtualKeyId)
                .ThenBy(filter => filter.FilterType)
                .ThenBy(filter => filter.IpAddressOrCidr)
                .ToListAsync(cancellationToken),
            "getting enabled per-key filters",
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IpFilterEntity>> GetByVirtualKeyIdAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => Ordered(context.IpFilters
                    .AsNoTracking()
                    .Where(filter => filter.VirtualKeyId == virtualKeyId))
                .ToListAsync(cancellationToken),
            $"getting filters for virtual key {virtualKeyId}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<IpFilterEntity> AddAsync(
        IpFilterEntity filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await ExecuteAsync(async context =>
        {
            var now = DateTime.UtcNow;
            if (filter.CreatedAt == default)
            {
                filter.CreatedAt = now;
            }

            filter.UpdatedAt = now;
            context.IpFilters.Add(filter);
            await context.SaveChangesAsync(cancellationToken);
            return filter;
        }, "adding filter", cancellationToken);

        _logger.LogInformation(
            "Added new IP filter: {FilterType} {IpAddressOrCidr}",
            LoggingSanitizer.S(filter.FilterType),
            LoggingSanitizer.S(filter.IpAddressOrCidr));
        return filter;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        IpFilterEntity filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.UpdatedAt = DateTime.UtcNow;

        try
        {
            return await ExecuteAsync(async context =>
            {
                context.IpFilters.Update(filter);
                return await context.SaveChangesAsync(cancellationToken) > 0;
            }, $"updating ID {filter.Id}", cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning("IP filter {FilterId} was changed by another writer", filter.Id);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(async context =>
        {
            var filter = await context.IpFilters.FindAsync([id], cancellationToken);
            if (filter is null)
            {
                return false;
            }

            context.IpFilters.Remove(filter);
            return await context.SaveChangesAsync(cancellationToken) > 0;
        }, $"deleting by ID {id}", cancellationToken);

    private static IOrderedQueryable<IpFilterEntity> Ordered(IQueryable<IpFilterEntity> query) =>
        query.OrderBy(filter => filter.FilterType)
            .ThenBy(filter => filter.IpAddressOrCidr);

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
                "Slow repository operation: {OperationName} IpFilterEntity took {ElapsedMs}ms",
                operationName,
                stopwatch.ElapsedMilliseconds);
        }

        return result;
    }
}
