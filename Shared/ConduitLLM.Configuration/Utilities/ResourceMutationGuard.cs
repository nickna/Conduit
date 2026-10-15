using System.Diagnostics.CodeAnalysis;
using ConduitLLM.Configuration.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ConduitLLM.Configuration.Utilities;

/// <summary>The database values used to identify one resource snapshot.</summary>
public sealed record ResourceVersion(byte[]? RowVersion, DateTime UpdatedAt)
{
    /// <summary>Creates a group version that includes its member count.</summary>
    public ResourceVersion(byte[]? rowVersion, DateTime updatedAt, int virtualKeyCount)
        : this(rowVersion, updatedAt)
    {
        VirtualKeyCount = virtualKeyCount;
    }

    /// <summary>Member count when the represented resource is a virtual key group.</summary>
    public int? VirtualKeyCount { get; init; }

    // Some supported stores do not generate RowVersion. Always include the stored timestamp.
    public string ETag => VirtualKeyCount is { } count
        ? $"\"{Convert.ToBase64String(RowVersion ?? [])}.{UpdatedAt.Ticks:x}.{count:x}\""
        : $"\"{Convert.ToBase64String(RowVersion ?? [])}.{UpdatedAt.Ticks:x}\"";
}

/// <summary>A strong If-Match condition passed explicitly to the persistence boundary.</summary>
public sealed class ResourcePrecondition
{
    private readonly string[] _candidates;
    public ResourcePrecondition(IEnumerable<string?> headers) => _candidates = headers
        .SelectMany(value => value?.Split(',') ?? [])
        .Select(value => value.Trim()).ToArray();
    public bool IsMissing => _candidates.Length == 0 || _candidates.All(string.IsNullOrEmpty);
    public bool Matches(ResourceVersion version) => _candidates.Any(value =>
        value == "*" || string.Equals(value, version.ETag, StringComparison.Ordinal));
}

public sealed class ResourcePreconditionRequiredException : Exception
{
    public ResourcePreconditionRequiredException() : base("Fetch the resource and send its ETag in the If-Match header.") { }
}

public sealed class ResourcePreconditionFailedException : Exception
{
    public ResourcePreconditionFailedException() : base("The resource changed after it was read. Fetch it again and retry.") { }
}

/// <summary>Server-only version metadata for a returned representation.</summary>
public interface IVersionedRepresentation
{
    string? ResourceETag { get; }
}

/// <summary>
/// Acquires the resource write lock with a compare-and-swap before invoking any mutation.
/// All database work on the locked resource, including reads, must use the supplied context. Only failed lock
/// acquisition is retried; callbacks, including external deletion, are never replayed.
/// </summary>
public static class ResourceMutationGuard
{
    private const DynamicallyAccessedMemberTypes EntityMembers =
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors |
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields |
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties |
        DynamicallyAccessedMemberTypes.Interfaces;

    public static Task<ResourceVersion?> ReadVersionAsync<[DynamicallyAccessedMembers(EntityMembers)] TEntity>(ConduitDbContext db, int id,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        if (typeof(TEntity) == typeof(VirtualKeyGroup))
        {
            // One statement pairs the row metadata with the count in the same snapshot.
            // Constructor projection avoids reflection-based property binding under NativeAOT.
            return db.VirtualKeyGroups.AsNoTracking().Where(group => group.Id == id)
                .Select(group => new ResourceVersion(group.RowVersion, group.UpdatedAt, group.VirtualKeys.Count))
                .SingleOrDefaultAsync(cancellationToken);
        }

        return db.Set<TEntity>().AsNoTracking().Where(entity => EF.Property<int>(entity, "Id") == id)
            .Select(entity => new ResourceVersion(EF.Property<byte[]?>(entity, "RowVersion"),
                EF.Property<DateTime>(entity, "UpdatedAt")))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public static Task<TResult?> ExecuteAsync<[DynamicallyAccessedMembers(EntityMembers)] TEntity, TResult>(ConduitDbContext db, int id,
        ResourcePrecondition precondition,
        Func<ConduitDbContext, TEntity, CancellationToken, Task> mutation,
        Func<TEntity, TResult> project,
        CancellationToken cancellationToken = default)
        where TEntity : class where TResult : class
        => new ConditionalExecutionStrategy(db).ExecuteAsync(() => ExecuteCoreAsync(
            db, id, precondition, mutation, project, cancellationToken));

    private sealed class ConditionalExecutionStrategy(DbContext db) : ExecutionStrategy(db, 0, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    private static async Task<TResult?> ExecuteCoreAsync<[DynamicallyAccessedMembers(EntityMembers)] TEntity, TResult>(ConduitDbContext db, int id,
        ResourcePrecondition precondition,
        Func<ConduitDbContext, TEntity, CancellationToken, Task> mutation,
        Func<TEntity, TResult> project, CancellationToken cancellationToken)
        where TEntity : class where TResult : class
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Conditional mutations must own their transaction.");
        if (!db.Database.IsRelational())
            throw new InvalidOperationException("Conditional mutations require a relational store.");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Read outside the transaction: SQLite cannot upgrade a stale read snapshot to
            // a writer after another connection commits. The CAS is the first transaction SQL.
            var version = await ReadVersionAsync<TEntity>(db, id, cancellationToken);
            if (version is null) return null;
            if (precondition.IsMissing) throw new ResourcePreconditionRequiredException();
            if (!precondition.Matches(version)) throw new ResourcePreconditionFailedException();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var candidates = db.Set<TEntity>()
                .Where(entity => EF.Property<int>(entity, "Id") == id &&
                    EF.Property<DateTime>(entity, "UpdatedAt") == version.UpdatedAt &&
                    EF.Property<byte[]?>(entity, "RowVersion") == version.RowVersion);
            if (typeof(TEntity) == typeof(VirtualKeyGroup))
            {
                candidates = candidates.Where(entity => db.VirtualKeys.Count(key =>
                    key.VirtualKeyGroupId == EF.Property<int>(entity, "Id")) == version.VirtualKeyCount);
            }
            var rows = await candidates.ExecuteUpdateAsync(setters => setters.SetProperty(
                    entity => EF.Property<DateTime>(entity, "UpdatedAt"),
                    entity => EF.Property<DateTime>(entity, "UpdatedAt")), cancellationToken);
            if (rows == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                // A wildcard or multi-tag request can still match the new snapshot. Strong
                // stale tags fail on the next read, before the callback has run.
                continue;
            }

            var entity = await db.Set<TEntity>()
                .SingleAsync(entity => EF.Property<int>(entity, "Id") == id, cancellationToken);
            // A scoped context may already track an older copy. Identity resolution must
            // not substitute that copy for the row whose current version we just locked.
            await db.Entry(entity).ReloadAsync(cancellationToken);
            if (typeof(TEntity) == typeof(VirtualKeyGroup))
            {
                // Membership writers need not update the group row. Check again after any
                // wait for its lock, before executing the mutation or external side effects.
                // This evaluates If-Match; it does not lock membership for the transaction.
                var lockedVersion = await ReadVersionAsync<TEntity>(db, id, cancellationToken);
                if (lockedVersion is null || !precondition.Matches(lockedVersion))
                    throw new ResourcePreconditionFailedException();
            }
            await mutation(db, entity, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (db.Entry(entity).State != EntityState.Detached)
            {
                // PostgreSQL timestamps have microsecond precision. Return the persisted
                // snapshot so the immediately following If-Match can use the response tag.
                await db.Entry(entity).ReloadAsync(cancellationToken);
            }
            var result = project(entity);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
    }
}
