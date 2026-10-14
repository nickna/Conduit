using Npgsql;

namespace ConduitLLM.Configuration.Data;

/// <summary>
/// Schema version compiled into this release. Update this value whenever a migration is added.
/// </summary>
public static class ConduitSchemaVersion
{
    public const string Current = "20261011204857_AddExactOriginalBalanceAdjustmentEvidence";
}

public sealed record SchemaVersionStatus(string? AppliedVersion, string ExpectedVersion)
{
    public bool IsCurrent => string.Equals(AppliedVersion, ExpectedVersion, StringComparison.Ordinal);
}

/// <summary>Reads only the latest row from EF's stable migration-history table.</summary>
public interface ISchemaVersionProbe
{
    Task<SchemaVersionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Probes the schema through a pooled PostgreSQL connection without loading EF metadata.</summary>
public sealed class SchemaVersionProbe(NpgsqlDataSource dataSource) : ISchemaVersionProbe
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<SchemaVersionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadStatusAsync(connection, cancellationToken);
    }

    internal static async Task<SchemaVersionStatus> ReadStatusAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "MigrationId"
            FROM "__EFMigrationsHistory"
            ORDER BY "MigrationId" DESC
            LIMIT 1
            """;

        try
        {
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return new SchemaVersionStatus(value is null or DBNull ? null : (string)value, ConduitSchemaVersion.Current);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // An uninitialized database, or a history table removed during the probe,
            // has no applied version. All other errors belong to the caller's retry policy.
            return new SchemaVersionStatus(null, ConduitSchemaVersion.Current);
        }
    }
}
