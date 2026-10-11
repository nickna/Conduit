using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;

using Npgsql;

namespace ConduitLLM.Persistence.Npgsql;

internal static class FunctionConfigurationRowReader
{
    internal const string Columns = """
        "Id", "ProviderType", "ConfigurationName", "Purpose", "DefaultExecutionMode",
        "BaseUrl", "IsEnabled", "TimeoutSeconds", "CacheTtlMinutes", "MaxRetries",
        "ProviderSettings", "ParameterSchema", "Description", "CreatedAt", "UpdatedAt"
        """;

    internal static FunctionConfiguration Read(NpgsqlDataReader reader, int offset = 0) => new()
    {
        Id = reader.GetInt32(offset),
        ProviderType = (FunctionProviderType)reader.GetInt32(offset + 1),
        ConfigurationName = reader.GetString(offset + 2),
        Purpose = (FunctionPurpose)reader.GetInt32(offset + 3),
        DefaultExecutionMode = (ExecutionMode)reader.GetInt32(offset + 4),
        BaseUrl = NullableString(reader, offset + 5),
        IsEnabled = reader.GetBoolean(offset + 6),
        TimeoutSeconds = NullableInt(reader, offset + 7),
        CacheTtlMinutes = NullableInt(reader, offset + 8),
        MaxRetries = NullableInt(reader, offset + 9),
        ProviderSettings = NullableString(reader, offset + 10),
        ParameterSchema = NullableString(reader, offset + 11),
        Description = NullableString(reader, offset + 12),
        CreatedAt = reader.GetDateTime(offset + 13),
        UpdatedAt = reader.GetDateTime(offset + 14)
    };

    internal static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
