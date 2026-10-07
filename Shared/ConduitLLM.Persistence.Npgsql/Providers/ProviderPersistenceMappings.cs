using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

internal static class ProviderPersistenceMappings
{
    internal const string ProviderColumns =
        "\"Id\", \"ProviderType\", \"ProviderName\", \"BaseUrl\", \"Settings\", " +
        "\"IsEnabled\", \"TrustProviderReportedCosts\", \"ProviderCostMarkupMultiplier\", " +
        "\"CreatedAt\", \"UpdatedAt\"";

    internal const string CredentialColumns =
        "\"Id\", \"ProviderId\", \"ProviderAccountGroup\", \"ApiKey\", \"BaseUrl\", " +
        "\"SecretSettings\", \"KeyName\", \"IsPrimary\", \"IsEnabled\", \"CreatedAt\", \"UpdatedAt\"";

    internal static Provider ReadProvider(NpgsqlDataReader reader, int offset = 0) => new()
    {
        Id = reader.GetInt32(offset),
        ProviderType = (ProviderType)reader.GetInt32(offset + 1),
        ProviderName = reader.GetString(offset + 2),
        BaseUrl = reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
        Settings = DeserializeDictionary(reader, offset + 4),
        IsEnabled = reader.GetBoolean(offset + 5),
        TrustProviderReportedCosts = reader.GetBoolean(offset + 6),
        ProviderCostMarkupMultiplier = reader.GetDecimal(offset + 7),
        CreatedAt = reader.GetDateTime(offset + 8),
        UpdatedAt = reader.GetDateTime(offset + 9)
    };

    internal static ProviderKeyCredential ReadCredential(
        NpgsqlDataReader reader,
        int offset = 0) => new()
    {
        Id = reader.GetInt32(offset),
        ProviderId = reader.GetInt32(offset + 1),
        ProviderAccountGroup = reader.GetInt16(offset + 2),
        ApiKey = reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
        BaseUrl = reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
        SecretSettings = DeserializeDictionary(reader, offset + 5),
        KeyName = reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
        IsPrimary = reader.GetBoolean(offset + 7),
        IsEnabled = reader.GetBoolean(offset + 8),
        CreatedAt = reader.GetDateTime(offset + 9),
        UpdatedAt = reader.GetDateTime(offset + 10)
    };

    internal static void AddProviderParameters(
        NpgsqlCommand command,
        Provider provider,
        bool includeId)
    {
        if (includeId)
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, provider.Id);
        }

        command.Parameters.AddWithValue("providerType", NpgsqlDbType.Integer, (int)provider.ProviderType);
        command.Parameters.AddWithValue("providerName", NpgsqlDbType.Varchar, provider.ProviderName);
        AddNullableText(command, "baseUrl", provider.BaseUrl);
        AddNullableJson(command, "settings", provider.Settings);
        command.Parameters.AddWithValue("isEnabled", NpgsqlDbType.Boolean, provider.IsEnabled);
        command.Parameters.AddWithValue(
            "trustProviderReportedCosts",
            NpgsqlDbType.Boolean,
            provider.TrustProviderReportedCosts);
        command.Parameters.AddWithValue(
            "providerCostMarkupMultiplier",
            NpgsqlDbType.Numeric,
            provider.ProviderCostMarkupMultiplier);
        command.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, provider.CreatedAt);
        command.Parameters.AddWithValue("updatedAt", NpgsqlDbType.TimestampTz, provider.UpdatedAt);
    }

    internal static void AddCredentialParameters(
        NpgsqlCommand command,
        ProviderKeyCredential credential,
        bool includeId,
        bool includeCreatedAt)
    {
        if (includeId)
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, credential.Id);
        }

        command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, credential.ProviderId);
        command.Parameters.AddWithValue(
            "providerAccountGroup",
            NpgsqlDbType.Smallint,
            credential.ProviderAccountGroup);
        AddNullableText(command, "apiKey", credential.ApiKey);
        AddNullableText(command, "baseUrl", credential.BaseUrl);
        AddNullableJson(command, "secretSettings", credential.SecretSettings);
        AddNullableText(command, "keyName", credential.KeyName);
        command.Parameters.AddWithValue("isPrimary", NpgsqlDbType.Boolean, credential.IsPrimary);
        command.Parameters.AddWithValue("isEnabled", NpgsqlDbType.Boolean, credential.IsEnabled);
        if (includeCreatedAt)
        {
            command.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, credential.CreatedAt);
        }
        command.Parameters.AddWithValue("updatedAt", NpgsqlDbType.TimestampTz, credential.UpdatedAt);
    }

    internal static void AddNullableText(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = value is null ? DBNull.Value : value
        });
    }

    private static Dictionary<string, string>? DeserializeDictionary(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : JsonSerializer.Deserialize(
                reader.GetString(ordinal),
                ProviderPersistenceJsonContext.Default.DictionaryStringString);

    private static void AddNullableJson(
        NpgsqlCommand command,
        string name,
        Dictionary<string, string>? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb)
        {
            Value = value is null
                ? DBNull.Value
                : JsonSerializer.Serialize(
                    value,
                    ProviderPersistenceJsonContext.Default.DictionaryStringString)
        });
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ProviderPersistenceJsonContext : JsonSerializerContext;
