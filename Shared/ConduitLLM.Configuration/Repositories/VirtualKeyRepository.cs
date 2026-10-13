using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Utilities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Repositories
{
    /// <summary>
    /// Repository implementation for virtual keys using Entity Framework Core.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This repository provides data access operations for virtual key entities using Entity Framework Core.
    /// It extends <see cref="RepositoryBase{TEntity, TKey}"/> for standard CRUD operations and implements
    /// <see cref="IVirtualKeyRepository"/> for domain-specific virtual key operations.
    /// </para>
    /// <para>
    /// The implementation follows these principles:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Using short-lived DbContext instances for better performance and reliability</description></item>
    ///   <item><description>Comprehensive error handling with detailed logging</description></item>
    ///   <item><description>Optimistic concurrency control for update operations with retry logic</description></item>
    ///   <item><description>Non-tracking queries for read operations to improve performance</description></item>
    ///   <item><description>Automatic timestamp management for auditing purposes</description></item>
    /// </list>
    /// </remarks>
    public class VirtualKeyRepository : RepositoryBase<VirtualKey, int>, IVirtualKeyRepository
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="VirtualKeyRepository"/> class.
        /// </summary>
        /// <param name="dbContextFactory">The database context factory used to create DbContext instances.</param>
        /// <param name="logger">The logger for recording diagnostic information.</param>
        /// <exception cref="ArgumentNullException">Thrown when dbContextFactory or logger is null.</exception>
        public VirtualKeyRepository(
            IDbContextFactory<ConduitDbContext> dbContextFactory,
            ILogger<VirtualKeyRepository> logger)
            : base(dbContextFactory, logger)
        {
        }

        /// <inheritdoc/>
        protected override DbSet<VirtualKey> GetDbSet(ConduitDbContext context) => context.VirtualKeys;

        /// <inheritdoc/>
        protected override IQueryable<VirtualKey> ApplyDefaultIncludes(IQueryable<VirtualKey> query)
        {
            return query.Include(vk => vk.VirtualKeyGroup);
        }

        /// <inheritdoc/>
        protected override IQueryable<VirtualKey> ApplyDefaultOrdering(IQueryable<VirtualKey> query)
        {
            return query.OrderBy(vk => vk.KeyName);
        }

        /// <inheritdoc/>
        public override async Task<bool> UpdateAsync(VirtualKey virtualKey, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(virtualKey);

            try
            {
                await using var context = await DbContextFactory.CreateDbContextAsync(cancellationToken);

                // Set the updated timestamp
                OnBeforeUpdate(virtualKey);

                // Update this key's scalar values only. Reads include its group for authentication,
                // but that detached snapshot must not overwrite a later group change.
                context.Entry(virtualKey).State = EntityState.Modified;

                // Save changes
                int rowsAffected = await context.SaveChangesAsync(cancellationToken);
                return rowsAffected > 0;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                Logger.LogError(ex, "Concurrency error updating virtual key with ID {KeyId}", LoggingSanitizer.S(virtualKey.Id));

                // Handle concurrency issues by reloading and reapplying changes if needed
                await using var context = await DbContextFactory.CreateDbContextAsync(cancellationToken);
                var existingEntity = await context.VirtualKeys.FindAsync(new object[] { virtualKey.Id }, cancellationToken);

                if (existingEntity == null)
                {
                    return false;
                }

                // Update properties
                context.Entry(existingEntity).CurrentValues.SetValues(virtualKey);
                existingEntity.UpdatedAt = DateTime.UtcNow;

                int rowsAffected = await context.SaveChangesAsync(cancellationToken);
                return rowsAffected > 0;
            }

        }

        /// <inheritdoc/>
        public async Task<VirtualKey?> GetByKeyHashAsync(string keyHash, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(keyHash))
            {
                throw new ArgumentException("Key hash cannot be null or empty", nameof(keyHash));
            }

            // The group comes along because authentication stashes group-scope rate limits for
            // the request, and a second query on the hot auth path is not worth saving a join.
            // Group edits are propagated by invalidating the member keys' cache entries.
            return await ExecuteAsync(async context =>
                await context.VirtualKeys
                    .AsNoTracking()
                    .Include(vk => vk.VirtualKeyGroup)
                    .FirstOrDefaultAsync(vk => vk.KeyHash == keyHash, cancellationToken),
                cancellationToken, "getting by key hash");
        }

        /// <inheritdoc/>
        public async Task<(List<VirtualKey> Items, int TotalCount)> GetByVirtualKeyGroupIdPaginatedAsync(
            int virtualKeyGroupId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            return await GetFilteredPaginatedAsync(
                vk => vk.VirtualKeyGroupId == virtualKeyGroupId,
                pageNumber,
                pageSize,
                q => q.OrderBy(vk => vk.KeyName),
                cancellationToken,
                $"getting paginated by group ID {virtualKeyGroupId}");
        }

        /// <inheritdoc/>
        public async Task<Dictionary<int, string>> GetKeyNamesByIdsAsync(
            IEnumerable<int> ids,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(ids);

            var idList = ids.ToList();
            if (idList.Count == 0)
            {
                return new Dictionary<int, string>();
            }

            return await ExecuteAsync(async context =>
                await context.VirtualKeys
                    .AsNoTracking()
                    .Where(vk => idList.Contains(vk.Id))
                    .ToDictionaryAsync(vk => vk.Id, vk => vk.KeyName ?? "", cancellationToken),
                cancellationToken, $"getting key names for {idList.Count} IDs");
        }

        /// <inheritdoc/>
        public async Task<int> CountActiveAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
                await context.VirtualKeys
                    .AsNoTracking()
                    .Where(vk => vk.IsEnabled &&
                        (vk.ExpiresAt == null || vk.ExpiresAt > DateTime.UtcNow))
                    .CountAsync(cancellationToken),
                cancellationToken, "counting active");
        }

        /// <inheritdoc/>
        public async Task<int> CountExpiredAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
                await context.VirtualKeys
                    .AsNoTracking()
                    .Where(vk => vk.ExpiresAt != null && vk.ExpiresAt <= DateTime.UtcNow)
                    .CountAsync(cancellationToken),
                cancellationToken, "counting expired");
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string keyHash, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                var virtualKey = await context.VirtualKeys
                    .Where(vk => vk.KeyHash == keyHash)
                    .FirstOrDefaultAsync(cancellationToken);

                if (virtualKey == null)
                {
                    return false;
                }

                context.VirtualKeys.Remove(virtualKey);
                int rowsAffected = await context.SaveChangesAsync(cancellationToken);

                Logger.LogInformation("Deleted virtual key with hash {KeyHash}", LoggingSanitizer.S(keyHash));
                return rowsAffected > 0;
            }, cancellationToken, "deleting by key hash");
        }

        /// <inheritdoc/>
        public async Task<List<VirtualKey>> GetTopEnabledAsync(int count, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
                await context.VirtualKeys
                    .AsNoTracking()
                    .Where(vk => vk.IsEnabled)
                    .OrderBy(vk => vk.KeyName)
                    .Take(count)
                    .ToListAsync(cancellationToken),
                cancellationToken, $"getting top {count} enabled");
        }
    }
}
