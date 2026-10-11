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

    internal static FunctionConfiguration Read(NpgsqlDataReader reader) => new()
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

    internal static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
