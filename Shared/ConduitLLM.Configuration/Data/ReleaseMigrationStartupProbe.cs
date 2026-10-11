using Npgsql;

namespace ConduitLLM.Configuration.Data;

/// <summary>Serializes the standalone release job across EF and message-storage provisioning.</summary>
public static class ReleaseMigrationLock
{
    public const long Id = 7_891_099;
}

/// <summary>Read-only startup barrier for a current schema after an active release job finishes.</summary>
public sealed class ReleaseMigrationStartupProbe(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        // The transaction-scoped shared lock conflicts with the migrator's session
        // lock. Disposal releases it even after cancellation or a failed query.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_xact_lock_shared(@lock)";
        command.Parameters.AddWithValue("lock", ReleaseMigrationLock.Id);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true) return false;

        // Read on this same connection while the lock is held. A pool of one must
        // not need a second checkout, nor observe history mid-release-job.
        return (await SchemaVersionProbe.ReadStatusAsync(connection, cancellationToken)).IsCurrent;
    }
}
