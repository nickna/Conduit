using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql writer for Gateway request accounting.
/// </summary>
public sealed class NpgsqlRequestLogRuntimeStore : IRequestLogRuntimeStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlRequestLogRuntimeStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public async Task<int> WriteAsync(
        RequestLogRuntimeRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RequestType);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "RequestLogs" (
                "VirtualKeyId", "ModelName", "ProviderId", "ProviderType",
                "ModelProviderMappingId", "PromptCachingEligible", "PromptCachingPolicyApplied",
                "CachedReadSavings", "CacheWritePremium", "RoutingAffinityUsed",
                "RoutingDecisionReason", "RoutingFailoverCount", "RequestType", "InputTokens",
                "OutputTokens", "CachedInputTokens", "CachedWriteTokens", "Cost",
                "BillingMethod", "ProviderReportedCostUsd", "ProviderCostMarkupMultiplier",
                "BilledAtUtc", "ResponseTimeMs", "Timestamp", "UserId", "ClientIp",
                "RequestPath", "StatusCode", "Metadata")
            VALUES (
                @virtualKeyId, @modelName, @providerId, @providerType,
                @modelProviderMappingId, @promptCachingEligible, @promptCachingPolicyApplied,
                @cachedReadSavings, @cacheWritePremium, @routingAffinityUsed,
                @routingDecisionReason, @routingFailoverCount, @requestType, @inputTokens,
                @outputTokens, @cachedInputTokens, @cachedWriteTokens, @cost,
                @billingMethod, @providerReportedCostUsd, @providerCostMarkupMultiplier,
                @billedAtUtc, @responseTimeMs, @timestamp, @userId, @clientIp,
                @requestPath, @statusCode, @metadata)
            RETURNING "Id"
            """;
        AddParameters(command, record);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void AddParameters(NpgsqlCommand command, RequestLogRuntimeRecord record)
    {
        command.Parameters.AddWithValue("virtualKeyId", NpgsqlDbType.Integer, record.VirtualKeyId);
        command.Parameters.AddWithValue("modelName", NpgsqlDbType.Varchar, record.ModelName);
        AddNullable(command, "providerId", NpgsqlDbType.Integer, record.ProviderId);
        AddNullable(command, "providerType", NpgsqlDbType.Varchar, record.ProviderType);
        AddNullable(command, "modelProviderMappingId", NpgsqlDbType.Integer, record.ModelProviderMappingId);
        command.Parameters.AddWithValue("promptCachingEligible", NpgsqlDbType.Boolean, record.PromptCachingEligible);
        command.Parameters.AddWithValue("promptCachingPolicyApplied", NpgsqlDbType.Boolean, record.PromptCachingPolicyApplied);
        command.Parameters.AddWithValue("cachedReadSavings", NpgsqlDbType.Numeric, record.CachedReadSavings);
        command.Parameters.AddWithValue("cacheWritePremium", NpgsqlDbType.Numeric, record.CacheWritePremium);
        command.Parameters.AddWithValue("routingAffinityUsed", NpgsqlDbType.Boolean, record.RoutingAffinityUsed);
        AddNullable(command, "routingDecisionReason", NpgsqlDbType.Varchar, record.RoutingDecisionReason);
        command.Parameters.AddWithValue("routingFailoverCount", NpgsqlDbType.Integer, record.RoutingFailoverCount);
        command.Parameters.AddWithValue("requestType", NpgsqlDbType.Varchar, record.RequestType);
        command.Parameters.AddWithValue("inputTokens", NpgsqlDbType.Integer, record.InputTokens);
        command.Parameters.AddWithValue("outputTokens", NpgsqlDbType.Integer, record.OutputTokens);
        AddNullable(command, "cachedInputTokens", NpgsqlDbType.Integer, record.CachedInputTokens);
        AddNullable(command, "cachedWriteTokens", NpgsqlDbType.Integer, record.CachedWriteTokens);
        command.Parameters.AddWithValue("cost", NpgsqlDbType.Numeric, record.Cost);
        AddNullable(command, "billingMethod", NpgsqlDbType.Integer, record.BillingMethod);
        AddNullable(command, "providerReportedCostUsd", NpgsqlDbType.Numeric, record.ProviderReportedCostUsd);
        AddNullable(command, "providerCostMarkupMultiplier", NpgsqlDbType.Numeric, record.ProviderCostMarkupMultiplier);
        AddNullable(command, "billedAtUtc", NpgsqlDbType.TimestampTz, record.BilledAtUtc);
        command.Parameters.AddWithValue("responseTimeMs", NpgsqlDbType.Double, record.ResponseTimeMs);
        command.Parameters.AddWithValue("timestamp", NpgsqlDbType.TimestampTz, record.Timestamp);
        AddNullable(command, "userId", NpgsqlDbType.Varchar, record.UserId);
        AddNullable(command, "clientIp", NpgsqlDbType.Varchar, record.ClientIp);
        AddNullable(command, "requestPath", NpgsqlDbType.Varchar, record.RequestPath);
        AddNullable(command, "statusCode", NpgsqlDbType.Integer, record.StatusCode);
        AddNullable(command, "metadata", NpgsqlDbType.Jsonb, record.MetadataJson);
    }

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
