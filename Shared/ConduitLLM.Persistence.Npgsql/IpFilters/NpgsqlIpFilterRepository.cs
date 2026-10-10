using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql implementation of IP-filter persistence.
/// </summary>
public sealed class NpgsqlIpFilterRepository : IIpFilterRepository
{
    private const string SelectColumns =
        "\"Id\", \"FilterType\", \"IpAddressOrCidr\", \"Name\", \"Description\", " +
        "\"IsEnabled\", \"CreatedAt\", \"UpdatedAt\", \"CreatedBy\", \"UpdatedBy\", " +
        "\"VirtualKeyId\", \"RowVersion\"";

    private const string DefaultOrder = "\"FilterType\", \"IpAddressOrCidr\"";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates the repository using an application-owned data source.
    /// </summary>
    public NpgsqlIpFilterRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IpFilterEntity>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync(
            $"SELECT {SelectColumns} FROM \"IpFilters\" ORDER BY {DefaultOrder}",
            addParameters: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IpFilterEntity?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM \"IpFilters\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFilter(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IpFilterEntity>> GetEnabledAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync(
            $"SELECT {SelectColumns} FROM \"IpFilters\" " +
            $"WHERE \"IsEnabled\" AND \"VirtualKeyId\" IS NULL ORDER BY {DefaultOrder}",
            addParameters: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IpFilterEntity>> GetEnabledPerKeyAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync(
            $"SELECT {SelectColumns} FROM \"IpFilters\" " +
            "WHERE \"IsEnabled\" AND \"VirtualKeyId\" IS NOT NULL " +
            $"ORDER BY \"VirtualKeyId\", {DefaultOrder}",
            addParameters: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IpFilterEntity>> GetByVirtualKeyIdAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default) =>
        await ReadListAsync(
            $"SELECT {SelectColumns} FROM \"IpFilters\" " +
            $"WHERE \"VirtualKeyId\" = @virtualKeyId ORDER BY {DefaultOrder}",
            command => command.Parameters.AddWithValue(
                "virtualKeyId",
                NpgsqlDbType.Integer,
                virtualKeyId),
            cancellationToken);

    /// <inheritdoc />
    public async Task<IpFilterEntity> AddAsync(
        IpFilterEntity filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);

        var now = DateTime.UtcNow;
        if (filter.CreatedAt == default)
        {
            filter.CreatedAt = now;
        }

        filter.UpdatedAt = now;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "IpFilters" (
                "FilterType", "IpAddressOrCidr", "Name", "Description", "IsEnabled",
                "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "VirtualKeyId")
            VALUES (
                @filterType, @ipAddressOrCidr, @name, @description, @isEnabled,
                @createdAt, @updatedAt, @createdBy, @updatedBy, @virtualKeyId)
            RETURNING "Id", "RowVersion"
            """;
        AddFilterParameters(command, filter, includeId: false, includeRowVersion: false);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("IP filter insert returned no identifier.");
        }

        filter.Id = reader.GetInt32(0);
        filter.RowVersion = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
        return filter;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        IpFilterEntity filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        filter.UpdatedAt = DateTime.UtcNow;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "IpFilters"
            SET "FilterType" = @filterType,
                "IpAddressOrCidr" = @ipAddressOrCidr,
                "Name" = @name,
                "Description" = @description,
                "IsEnabled" = @isEnabled,
                "CreatedAt" = @createdAt,
                "UpdatedAt" = @updatedAt,
                "CreatedBy" = @createdBy,
                "UpdatedBy" = @updatedBy,
                "VirtualKeyId" = @virtualKeyId
            WHERE "Id" = @id
              AND "RowVersion" IS NOT DISTINCT FROM @rowVersion
            RETURNING "RowVersion"
            """;
        AddFilterParameters(command, filter, includeId: true, includeRowVersion: true);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        filter.RowVersion = reader.IsDBNull(0) ? null : reader.GetFieldValue<byte[]>(0);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"IpFilters\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<IReadOnlyList<IpFilterEntity>> ReadListAsync(
        string sql,
        Action<NpgsqlCommand>? addParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        addParameters?.Invoke(command);

        var filters = new List<IpFilterEntity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            filters.Add(ReadFilter(reader));
        }

        return filters;
    }

    private static IpFilterEntity ReadFilter(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        FilterType = reader.GetString(1),
        IpAddressOrCidr = reader.GetString(2),
        Name = reader.IsDBNull(3) ? null : reader.GetString(3),
        Description = reader.IsDBNull(4) ? null : reader.GetString(4),
        IsEnabled = reader.GetBoolean(5),
        CreatedAt = reader.GetDateTime(6),
        UpdatedAt = reader.GetDateTime(7),
        CreatedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
        UpdatedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
        VirtualKeyId = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        RowVersion = reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11)
    };

    private static void AddFilterParameters(
        NpgsqlCommand command,
        IpFilterEntity filter,
        bool includeId,
        bool includeRowVersion)
    {
        if (includeId)
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, filter.Id);
        }

        command.Parameters.AddWithValue("filterType", NpgsqlDbType.Varchar, filter.FilterType);
        command.Parameters.AddWithValue("ipAddressOrCidr", NpgsqlDbType.Varchar, filter.IpAddressOrCidr);
        AddNullableText(command, "name", filter.Name);
        AddNullableText(command, "description", filter.Description);
        command.Parameters.AddWithValue("isEnabled", NpgsqlDbType.Boolean, filter.IsEnabled);
        command.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, filter.CreatedAt);
        command.Parameters.AddWithValue("updatedAt", NpgsqlDbType.TimestampTz, filter.UpdatedAt);
        AddNullableText(command, "createdBy", filter.CreatedBy);
        AddNullableText(command, "updatedBy", filter.UpdatedBy);
        AddNullableInteger(command, "virtualKeyId", filter.VirtualKeyId);

        if (includeRowVersion)
        {
            command.Parameters.Add(new NpgsqlParameter("rowVersion", NpgsqlDbType.Bytea)
            {
                Value = filter.RowVersion is null ? DBNull.Value : filter.RowVersion
            });
        }
    }

    private static void AddNullableText(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar)
        {
            Value = value is null ? DBNull.Value : value
        });
    }

    private static void AddNullableInteger(NpgsqlCommand command, string name, int? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });
    }

    private static void ValidateFilter(IpFilterEntity filter)
    {
        ArgumentNullException.ThrowIfNull(filter.FilterType);
        ArgumentNullException.ThrowIfNull(filter.IpAddressOrCidr);
    }
}
