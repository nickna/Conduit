using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Utilities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// EF Core implementation of the explicit global-setting persistence contract.
/// </summary>
/// <remarks>
/// Global settings intentionally do not inherit <see cref="RepositoryBase{TEntity,TKey}"/>.
/// Each query is a complete, fixed operation so another backend can implement the
/// same contract without accepting expression trees or IQueryable delegates.
/// </remarks>
public sealed class GlobalSettingRepository : IGlobalSettingRepository
{
    private const int SlowQueryThresholdMs = 500;

    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;
    private readonly ILogger<GlobalSettingRepository> _logger;

    /// <summary>
    /// Creates the EF Core implementation.
    /// </summary>
    public GlobalSettingRepository(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        ILogger<GlobalSettingRepository> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GlobalSetting>> ListAsync(CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.GlobalSettings
                .AsNoTracking()
                .OrderBy(setting => setting.Key)
                .ToListAsync(cancellationToken),
            "listing all settings",
            cancellationToken);

    /// <inheritdoc />
    public async Task<GlobalSetting?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.GlobalSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(setting => setting.Id == id, cancellationToken),
            $"getting by ID {id}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<GlobalSetting?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        return await ExecuteAsync(
            context => context.GlobalSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(setting => setting.Key == key, cancellationToken),
            $"getting by key {LoggingSanitizer.S(key)}",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        GlobalSetting setting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ValidateKey(setting.Key);
        ArgumentNullException.ThrowIfNull(setting.Value);

        return await ExecuteAsync(async context =>
        {
            var now = DateTime.UtcNow;
            if (setting.CreatedAt == default)
            {
                setting.CreatedAt = now;
            }

            setting.UpdatedAt = now;
            context.GlobalSettings.Add(setting);
            await context.SaveChangesAsync(cancellationToken);
            return setting.Id;
        }, $"creating key {LoggingSanitizer.S(setting.Key)}", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        GlobalSetting setting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ValidateKey(setting.Key);
        ArgumentNullException.ThrowIfNull(setting.Value);

        return await ExecuteAsync(async context =>
        {
            setting.UpdatedAt = DateTime.UtcNow;
            context.GlobalSettings.Update(setting);
            return await context.SaveChangesAsync(cancellationToken) > 0;
        }, $"updating ID {setting.Id}", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> UpsertAsync(
        string key,
        string value,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        return await ExecuteAsync(async context =>
        {
            var existingSetting = await context.GlobalSettings
                .FirstOrDefaultAsync(setting => setting.Key == key, cancellationToken);
            var now = DateTime.UtcNow;

            if (existingSetting is null)
            {
                context.GlobalSettings.Add(new GlobalSetting
                {
                    Key = key,
                    Value = value,
                    Description = description,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                existingSetting.Value = value;
                existingSetting.UpdatedAt = now;
                if (description is not null)
                {
                    existingSetting.Description = description;
                }
            }

            return await context.SaveChangesAsync(cancellationToken) > 0;
        }, $"upserting by key {LoggingSanitizer.S(key)}", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(async context =>
        {
            var setting = await context.GlobalSettings.FindAsync([id], cancellationToken);
            if (setting is null)
            {
                return false;
            }

            context.GlobalSettings.Remove(setting);
            return await context.SaveChangesAsync(cancellationToken) > 0;
        }, $"deleting by ID {id}", cancellationToken);

    /// <inheritdoc />
    public async Task<bool> DeleteByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        return await ExecuteAsync(async context =>
        {
            var setting = await context.GlobalSettings
                .FirstOrDefaultAsync(candidate => candidate.Key == key, cancellationToken);
            if (setting is null)
            {
                return false;
            }

            context.GlobalSettings.Remove(setting);
            return await context.SaveChangesAsync(cancellationToken) > 0;
        }, $"deleting by key {LoggingSanitizer.S(key)}", cancellationToken);
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
                "Slow repository operation: {OperationName} GlobalSetting took {ElapsedMs}ms",
                operationName,
                stopwatch.ElapsedMilliseconds);
        }

        return result;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Key cannot be null or empty", nameof(key));
        }
    }
}
