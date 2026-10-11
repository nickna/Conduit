using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Typed PostgreSQL reads for function discovery without EF query execution.</summary>
public sealed class NpgsqlFunctionDiscoveryRuntimeStore : IFunctionDiscoveryRuntimeStore
{
    private const string Columns = """
        "Id", "ProviderType", "ConfigurationName", "Purpose", "DefaultExecutionMode",
        "BaseUrl", "IsEnabled", "TimeoutSeconds", "CacheTtlMinutes", "MaxRetries",
        "ProviderSettings", "ParameterSchema", "Description", "CreatedAt", "UpdatedAt"
        """;
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
            SELECT {Columns} FROM "FunctionConfigurations"
            WHERE "IsEnabled" AND (@purpose IS NULL OR "Purpose" = @purpose)
                AND (@provider IS NULL OR "ProviderType" = @provider)
            """;
        command.Parameters.AddWithValue("purpose", NpgsqlDbType.Integer,
            purpose.HasValue ? (object)(int)purpose.Value : DBNull.Value);
        command.Parameters.AddWithValue("provider", NpgsqlDbType.Integer,
            providerType.HasValue ? (object)(int)providerType.Value : DBNull.Value);
        var configurations = new List<FunctionConfiguration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) configurations.Add(ReadConfiguration(reader));
        return configurations;
    }

    public async Task<FunctionConfiguration?> GetEnabledByIdAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM \"FunctionConfigurations\" WHERE \"Id\" = @id AND \"IsEnabled\"";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConfiguration(reader) : null;
    }

    private static FunctionConfiguration ReadConfiguration(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ProviderType = (FunctionProviderType)reader.GetInt32(1),
        ConfigurationName = reader.GetString(2),
        Purpose = (FunctionPurpose)reader.GetInt32(3),
        DefaultExecutionMode = (ExecutionMode)reader.GetInt32(4),
        BaseUrl = NullableString(reader, 5),
        IsEnabled = reader.GetBoolean(6),
        TimeoutSeconds = NullableInt(reader, 7),
        CacheTtlMinutes = NullableInt(reader, 8),
        MaxRetries = NullableInt(reader, 9),
        ProviderSettings = NullableString(reader, 10),
        ParameterSchema = NullableString(reader, 11),
        Description = NullableString(reader, 12),
        CreatedAt = reader.GetDateTime(13),
        UpdatedAt = reader.GetDateTime(14)
    };

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
