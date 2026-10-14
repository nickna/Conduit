using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Typed PostgreSQL scalar reads for function configuration and opaque credentials.</summary>
public sealed class NpgsqlFunctionExecutionConfigurationStore : IFunctionExecutionConfigurationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlFunctionExecutionConfigurationStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<FunctionConfiguration?> GetConfigurationAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {FunctionConfigurationRowReader.Columns} FROM \"FunctionConfigurations\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? FunctionConfigurationRowReader.Read(reader) : null;
    }

    public async Task<IReadOnlyList<FunctionConfiguration>> GetConfigurationsAsync(
        IReadOnlyList<int>? ids, CancellationToken cancellationToken = default)
    {
        if (ids is null || ids.Count == 0) return Array.Empty<FunctionConfiguration>();
        var requestedIds = ids.ToArray();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {FunctionConfigurationRowReader.Columns} FROM \"FunctionConfigurations\" WHERE \"Id\" = ANY(@ids)";
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, requestedIds);
        var configurations = new List<FunctionConfiguration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) configurations.Add(FunctionConfigurationRowReader.Read(reader));
        return configurations;
    }

    public async Task<IReadOnlyList<FunctionCredential>> ListCredentialsForProviderAsync(
        FunctionProviderType providerType, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "ProviderType", "FunctionConfigurationId", "ApiKey", "BaseUrl", "Organization",
                "FunctionAccountGroup", "IsPrimary", "IsEnabled", "KeyName", "CreatedAt", "UpdatedAt"
            FROM "FunctionCredentials" WHERE "ProviderType" = @provider
            ORDER BY "IsPrimary" DESC, "KeyName" ASC
            """;
        command.Parameters.AddWithValue("provider", NpgsqlDbType.Integer, (int)providerType);
        var credentials = new List<FunctionCredential>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            credentials.Add(new FunctionCredential
            {
                Id = reader.GetInt32(0),
                ProviderType = (FunctionProviderType)reader.GetInt32(1),
                FunctionConfigurationId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                ApiKey = FunctionConfigurationRowReader.NullableString(reader, 3),
                BaseUrl = FunctionConfigurationRowReader.NullableString(reader, 4),
                Organization = FunctionConfigurationRowReader.NullableString(reader, 5),
                FunctionAccountGroup = reader.GetInt16(6),
                IsPrimary = reader.GetBoolean(7),
                IsEnabled = reader.GetBoolean(8),
                KeyName = FunctionConfigurationRowReader.NullableString(reader, 9),
                CreatedAt = reader.GetDateTime(10),
                UpdatedAt = reader.GetDateTime(11)
            });
        }
        return credentials;
    }
}
