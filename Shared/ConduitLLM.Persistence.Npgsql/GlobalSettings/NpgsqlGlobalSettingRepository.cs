using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql implementation of global-setting persistence.
/// </summary>
public sealed class NpgsqlGlobalSettingRepository : IGlobalSettingRepository
{
    private const string SelectColumns =
        "\"Id\", \"Key\", \"Value\", \"Description\", \"CreatedAt\", \"UpdatedAt\"";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates the repository using an application-owned data source.
    /// </summary>
    public NpgsqlGlobalSettingRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GlobalSetting>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM \"GlobalSettings\" ORDER BY \"Key\"";

        var settings = new List<GlobalSetting>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            settings.Add(ReadSetting(reader));
        }

        return settings;
    }

    /// <inheritdoc />
    public async Task<GlobalSetting?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM \"GlobalSettings\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        return await ReadSingleAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GlobalSetting?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM \"GlobalSettings\" WHERE \"Key\" = @key";
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, key);
        return await ReadSingleAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        GlobalSetting setting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ValidateSetting(setting);

        var now = DateTime.UtcNow;
        if (setting.CreatedAt == default)
        {
            setting.CreatedAt = now;
        }

        setting.UpdatedAt = now;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "GlobalSettings" ("Key", "Value", "Description", "CreatedAt", "UpdatedAt")
            VALUES (@key, @value, @description, @createdAt, @updatedAt)
            RETURNING "Id"
            """;
        AddSettingParameters(command, setting, includeId: false);

        setting.Id = (int)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Global setting insert returned no identifier."));
        return setting.Id;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        GlobalSetting setting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ValidateSetting(setting);
        setting.UpdatedAt = DateTime.UtcNow;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "GlobalSettings"
            SET "Key" = @key,
                "Value" = @value,
                "Description" = @description,
                "CreatedAt" = @createdAt,
                "UpdatedAt" = @updatedAt
            WHERE "Id" = @id
            """;
        AddSettingParameters(command, setting, includeId: true);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "GlobalSettings" ("Key", "Value", "Description", "CreatedAt", "UpdatedAt")
            VALUES (@key, @value, @description, now(), now())
            ON CONFLICT ("Key") DO UPDATE
            SET "Value" = EXCLUDED."Value",
                "Description" = COALESCE(EXCLUDED."Description", "GlobalSettings"."Description"),
                "UpdatedAt" = now()
            """;
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, key);
        command.Parameters.AddWithValue("value", NpgsqlDbType.Varchar, value);
        AddNullableText(command, "description", description);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"GlobalSettings\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"GlobalSettings\" WHERE \"Key\" = @key";
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, key);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<GlobalSetting?> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSetting(reader) : null;
    }

    private static GlobalSetting ReadSetting(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Key = reader.GetString(1),
        Value = reader.GetString(2),
        Description = reader.IsDBNull(3) ? null : reader.GetString(3),
        CreatedAt = reader.GetDateTime(4),
        UpdatedAt = reader.GetDateTime(5)
    };

    private static void AddSettingParameters(
        NpgsqlCommand command,
        GlobalSetting setting,
        bool includeId)
    {
        if (includeId)
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, setting.Id);
        }

        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, setting.Key);
        command.Parameters.AddWithValue("value", NpgsqlDbType.Varchar, setting.Value);
        AddNullableText(command, "description", setting.Description);
        command.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, setting.CreatedAt);
        command.Parameters.AddWithValue("updatedAt", NpgsqlDbType.TimestampTz, setting.UpdatedAt);
    }

    private static void AddNullableText(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar)
        {
            Value = value is null ? DBNull.Value : value
        });
    }

    private static void ValidateSetting(GlobalSetting setting)
    {
        ValidateKey(setting.Key);
        ArgumentNullException.ThrowIfNull(setting.Value);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Key cannot be null or empty", nameof(key));
        }
    }
}
