using ConduitLLM.Configuration.Entities;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

public sealed partial class NpgsqlIpFilterRepository
{
    /// <inheritdoc />
    public async Task<IpFilterEntity?> UpdateConditionalAsync(int id, Action<IpFilterEntity> validateVersion,
        Func<IpFilterEntity, bool> applyChanges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validateVersion);
        ArgumentNullException.ThrowIfNull(applyChanges);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadLockedFilterAsync(connection, transaction, id, cancellationToken);
        if (row is null) return null;
        validateVersion(row);
        if (applyChanges(row))
        {
            ValidateFilter(row);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE "IpFilters"
                SET "FilterType" = @filterType, "IpAddressOrCidr" = @ipAddressOrCidr,
                    "Name" = @name, "Description" = @description,
                    "IsEnabled" = @isEnabled, "UpdatedAt" = @updatedAt
                WHERE "Id" = @id
                RETURNING {SelectColumns}
                """;
            AddFilterParameters(command, row, includeId: true, includeRowVersion: false);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Locked IP filter update returned no row.");
            // Read PostgreSQL's persisted timestamp precision before producing the response ETag.
            row = ReadFilter(reader);
        }
        await transaction.CommitAsync(cancellationToken);
        return row;
    }

    /// <inheritdoc />
    public async Task<IpFilterEntity?> DeleteConditionalAsync(int id, Action<IpFilterEntity> validateVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validateVersion);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadLockedFilterAsync(connection, transaction, id, cancellationToken);
        if (row is null) return null;
        validateVersion(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM \"IpFilters\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Locked IP filter delete affected no row.");
        await transaction.CommitAsync(cancellationToken);
        return row;
    }

    private static async Task<IpFilterEntity?> ReadLockedFilterAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM \"IpFilters\" WHERE \"Id\" = @id FOR UPDATE";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFilter(reader) : null;
    }
}
