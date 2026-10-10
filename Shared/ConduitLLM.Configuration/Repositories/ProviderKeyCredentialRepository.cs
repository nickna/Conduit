using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// EF Core reference implementation of provider-credential persistence.
/// </summary>
public sealed class ProviderKeyCredentialRepository : IProviderKeyCredentialRepository
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int SlowQueryThresholdMs = 500;

    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;
    private readonly ILogger<ProviderKeyCredentialRepository> _logger;

    public ProviderKeyCredentialRepository(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        ILogger<ProviderKeyCredentialRepository> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<(List<ProviderKeyCredential> Items, int TotalCount)> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePagination(page, pageSize);
        return await ExecuteAsync(async context =>
        {
            var query = context.ProviderKeyCredentials
                .AsNoTracking()
                .Include(credential => credential.Provider);
            var totalCount = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderBy(credential => credential.ProviderId)
                .ThenByDescending(credential => credential.IsPrimary)
                .ThenBy(credential => credential.ProviderAccountGroup)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            return (items, totalCount);
        }, $"getting paginated credentials (page {page}, size {pageSize})", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(List<ProviderKeyCredential> Items, int TotalCount)> GetByProviderIdPaginatedAsync(
        int providerId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        (pageNumber, pageSize) = NormalizePagination(pageNumber, pageSize);
        return await ExecuteAsync(async context =>
        {
            var query = context.ProviderKeyCredentials
                .AsNoTracking()
                .Include(credential => credential.Provider)
                .Where(credential => credential.ProviderId == providerId);
            var totalCount = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderByDescending(credential => credential.IsPrimary)
                .ThenBy(credential => credential.ProviderAccountGroup)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            return (items, totalCount);
        }, $"getting paginated credentials for provider {providerId}", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ProviderKeyCredential?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.ProviderKeyCredentials
                .AsNoTracking()
                .Include(credential => credential.Provider)
                .FirstOrDefaultAsync(credential => credential.Id == id, cancellationToken),
            $"getting credential by ID {id}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        ProviderKeyCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return await ExecuteAsync(async context =>
        {
            var now = DateTime.UtcNow;
            if (credential.CreatedAt == default)
            {
                credential.CreatedAt = now;
            }
            credential.UpdatedAt = now;

            if (credential.IsEnabled && credential.IsPrimary)
            {
                var existingPrimary = await context.ProviderKeyCredentials
                    .FirstOrDefaultAsync(
                        candidate => candidate.ProviderId == credential.ProviderId && candidate.IsPrimary,
                        cancellationToken);
                if (existingPrimary is not null)
                {
                    existingPrimary.IsPrimary = false;
                    existingPrimary.UpdatedAt = now;
                    _logger.LogInformation(
                        "Demoted existing primary key {KeyId} for provider {ProviderId}",
                        existingPrimary.Id,
                        credential.ProviderId);
                }
            }
            else if (credential.IsEnabled)
            {
                var enabledKeysCount = await context.ProviderKeyCredentials.CountAsync(
                    candidate => candidate.ProviderId == credential.ProviderId && candidate.IsEnabled,
                    cancellationToken);
                if (enabledKeysCount == 0)
                {
                    credential.IsPrimary = true;
                    _logger.LogInformation(
                        "Automatically setting key as primary since it is the only enabled key for provider {ProviderId}",
                        credential.ProviderId);
                }
            }

            context.ProviderKeyCredentials.Add(credential);
            await context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Created key credential {KeyId} for provider {ProviderId} (IsPrimary: {IsPrimary})",
                credential.Id,
                credential.ProviderId,
                credential.IsPrimary);
            return credential.Id;
        }, $"creating credential for provider {credential.ProviderId}", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        ProviderKeyCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return await ExecuteAsync(async context =>
        {
            var existing = await context.ProviderKeyCredentials
                .FirstOrDefaultAsync(candidate => candidate.Id == credential.Id, cancellationToken);
            if (existing is null)
            {
                return false;
            }

            var wasEnabled = existing.IsEnabled;
            var now = DateTime.UtcNow;
            existing.ProviderAccountGroup = credential.ProviderAccountGroup;
            existing.ApiKey = credential.ApiKey;
            existing.BaseUrl = credential.BaseUrl;
            existing.SecretSettings = credential.SecretSettings;
            existing.KeyName = credential.KeyName;
            existing.IsPrimary = credential.IsPrimary;
            existing.IsEnabled = credential.IsEnabled;
            existing.UpdatedAt = now;

            if (credential.IsPrimary && credential.IsEnabled)
            {
                var otherPrimary = await context.ProviderKeyCredentials.FirstOrDefaultAsync(
                    candidate => candidate.ProviderId == existing.ProviderId &&
                        candidate.IsPrimary &&
                        candidate.Id != existing.Id,
                    cancellationToken);
                if (otherPrimary is not null)
                {
                    otherPrimary.IsPrimary = false;
                    otherPrimary.UpdatedAt = now;
                    _logger.LogInformation(
                        "Demoted existing primary key {KeyId} for provider {ProviderId}",
                        otherPrimary.Id,
                        existing.ProviderId);
                }
            }
            else if (!wasEnabled && credential.IsEnabled && !credential.IsPrimary)
            {
                var enabledKeysCount = await context.ProviderKeyCredentials.CountAsync(
                    candidate => candidate.ProviderId == existing.ProviderId &&
                        candidate.IsEnabled &&
                        candidate.Id != existing.Id,
                    cancellationToken);
                if (enabledKeysCount == 0)
                {
                    existing.IsPrimary = true;
                    _logger.LogInformation(
                        "Automatically setting key {KeyId} as primary since it is the only enabled key for provider {ProviderId}",
                        existing.Id,
                        existing.ProviderId);
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            credential.ProviderId = existing.ProviderId;
            credential.IsPrimary = existing.IsPrimary;
            credential.UpdatedAt = existing.UpdatedAt;
            _logger.LogInformation(
                "Updated key credential {KeyId} for provider {ProviderId} (IsPrimary: {IsPrimary})",
                existing.Id,
                existing.ProviderId,
                existing.IsPrimary);
            return true;
        }, $"updating credential ID {credential.Id}", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(async context =>
        {
            var credential = await context.ProviderKeyCredentials
                .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            if (credential is null)
            {
                return false;
            }

            context.ProviderKeyCredentials.Remove(credential);
            await context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Deleted key credential {KeyId} for provider {ProviderId}",
                id,
                credential.ProviderId);
            return true;
        }, $"deleting credential ID {id}", cancellationToken);

    /// <inheritdoc />
    public async Task<ProviderKeyCredential?> GetPrimaryKeyAsync(
        int providerId,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.ProviderKeyCredentials
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    credential => credential.ProviderId == providerId &&
                        credential.IsPrimary &&
                        credential.IsEnabled,
                    cancellationToken),
            $"getting primary credential for provider {providerId}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<List<ProviderKeyCredential>> GetEnabledKeysByProviderIdAsync(
        int providerId,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.ProviderKeyCredentials
                .AsNoTracking()
                .Where(credential => credential.ProviderId == providerId && credential.IsEnabled)
                .OrderByDescending(credential => credential.IsPrimary)
                .ThenBy(credential => credential.ProviderAccountGroup)
                .ToListAsync(cancellationToken),
            $"getting enabled credentials for provider {providerId}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<bool> SetPrimaryKeyAsync(
        int providerId,
        int keyId,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.ExecuteInTransactionAsync(async transactionCancellationToken =>
            {
                var newPrimary = await context.ProviderKeyCredentials.FirstOrDefaultAsync(
                    credential => credential.Id == keyId && credential.ProviderId == providerId,
                    transactionCancellationToken);
                if (newPrimary is null)
                {
                    return false;
                }

                var existingPrimaries = await context.ProviderKeyCredentials
                    .Where(credential => credential.ProviderId == providerId && credential.IsPrimary)
                    .ToListAsync(transactionCancellationToken);
                var now = DateTime.UtcNow;
                foreach (var credential in existingPrimaries)
                {
                    credential.IsPrimary = false;
                    credential.UpdatedAt = now;
                }

                if (existingPrimaries.Count > 0)
                {
                    await context.SaveChangesAsync(transactionCancellationToken);
                }

                newPrimary.IsPrimary = true;
                newPrimary.UpdatedAt = now;
                await context.SaveChangesAsync(transactionCancellationToken);
                _logger.LogInformation(
                    "Set key {KeyId} as primary for provider {ProviderId}",
                    keyId,
                    providerId);
                return true;
            }, cancellationToken),
            $"setting primary credential {keyId} for provider {providerId}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<bool> HasKeyCredentialsAsync(
        int providerId,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.ProviderKeyCredentials.AnyAsync(
                credential => credential.ProviderId == providerId,
                cancellationToken),
            $"checking credentials for provider {providerId}",
            cancellationToken);

    /// <inheritdoc />
    public async Task<int> CountByProviderIdAsync(
        int providerId,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            context => context.ProviderKeyCredentials.CountAsync(
                credential => credential.ProviderId == providerId,
                cancellationToken),
            $"counting credentials for provider {providerId}",
            cancellationToken);

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
                "Slow repository operation: {OperationName} ProviderKeyCredential took {ElapsedMs}ms",
                operationName,
                stopwatch.ElapsedMilliseconds);
        }

        return result;
    }
}
