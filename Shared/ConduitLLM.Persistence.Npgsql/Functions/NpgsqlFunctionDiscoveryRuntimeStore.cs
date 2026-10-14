using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Typed PostgreSQL reads for function discovery without EF query execution.</summary>
public sealed class NpgsqlFunctionDiscoveryRuntimeStore : IFunctionDiscoveryRuntimeStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlFunctionDiscoveryRuntimeStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<IReadOnlyList<FunctionConfiguration>> ListEnabledAsync(
        FunctionPurpose? purpose = null,
        FunctionProviderType? providerType = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FunctionConfigurationRowReader.Columns} FROM "FunctionConfigurations"
            WHERE "IsEnabled" AND (@purpose IS NULL OR "Purpose" = @purpose)
                AND (@provider IS NULL OR "ProviderType" = @provider)
            """;
        command.Parameters.AddWithValue("purpose", NpgsqlDbType.Integer,
            purpose.HasValue ? (object)(int)purpose.Value : DBNull.Value);
        command.Parameters.AddWithValue("provider", NpgsqlDbType.Integer,
            providerType.HasValue ? (object)(int)providerType.Value : DBNull.Value);
        var configurations = new List<FunctionConfiguration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) configurations.Add(FunctionConfigurationRowReader.Read(reader));
        return configurations;
    }

    public async Task<FunctionConfiguration?> GetEnabledByIdAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {FunctionConfigurationRowReader.Columns} FROM \"FunctionConfigurations\" WHERE \"Id\" = @id AND \"IsEnabled\"";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? FunctionConfigurationRowReader.Read(reader) : null;
    }

}
