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
        AddParameters(command.Parameters, record);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    internal static void AddParameters(NpgsqlParameterCollection parameters, RequestLogRuntimeRecord record)
    {
        parameters.AddWithValue("virtualKeyId", NpgsqlDbType.Integer, record.VirtualKeyId);
        parameters.AddWithValue("modelName", NpgsqlDbType.Varchar, record.ModelName);
        AddNullable(parameters, "providerId", NpgsqlDbType.Integer, record.ProviderId);
        AddNullable(parameters, "providerType", NpgsqlDbType.Varchar, record.ProviderType);
        AddNullable(parameters, "modelProviderMappingId", NpgsqlDbType.Integer, record.ModelProviderMappingId);
        parameters.AddWithValue("promptCachingEligible", NpgsqlDbType.Boolean, record.PromptCachingEligible);
        parameters.AddWithValue("promptCachingPolicyApplied", NpgsqlDbType.Boolean, record.PromptCachingPolicyApplied);
        parameters.AddWithValue("cachedReadSavings", NpgsqlDbType.Numeric, record.CachedReadSavings);
        parameters.AddWithValue("cacheWritePremium", NpgsqlDbType.Numeric, record.CacheWritePremium);
        parameters.AddWithValue("routingAffinityUsed", NpgsqlDbType.Boolean, record.RoutingAffinityUsed);
        AddNullable(parameters, "routingDecisionReason", NpgsqlDbType.Varchar, record.RoutingDecisionReason);
        parameters.AddWithValue("routingFailoverCount", NpgsqlDbType.Integer, record.RoutingFailoverCount);
        parameters.AddWithValue("requestType", NpgsqlDbType.Varchar, record.RequestType);
        parameters.AddWithValue("inputTokens", NpgsqlDbType.Integer, record.InputTokens);
        parameters.AddWithValue("outputTokens", NpgsqlDbType.Integer, record.OutputTokens);
        AddNullable(parameters, "cachedInputTokens", NpgsqlDbType.Integer, record.CachedInputTokens);
        AddNullable(parameters, "cachedWriteTokens", NpgsqlDbType.Integer, record.CachedWriteTokens);
        parameters.AddWithValue("cost", NpgsqlDbType.Numeric, record.Cost);
        AddNullable(parameters, "billingMethod", NpgsqlDbType.Integer, record.BillingMethod);
        AddNullable(parameters, "providerReportedCostUsd", NpgsqlDbType.Numeric, record.ProviderReportedCostUsd);
        AddNullable(parameters, "providerCostMarkupMultiplier", NpgsqlDbType.Numeric, record.ProviderCostMarkupMultiplier);
        AddNullable(parameters, "billedAtUtc", NpgsqlDbType.TimestampTz, record.BilledAtUtc);
        parameters.AddWithValue("responseTimeMs", NpgsqlDbType.Double, record.ResponseTimeMs);
        parameters.AddWithValue("timestamp", NpgsqlDbType.TimestampTz, record.Timestamp);
        AddNullable(parameters, "userId", NpgsqlDbType.Varchar, record.UserId);
        AddNullable(parameters, "clientIp", NpgsqlDbType.Varchar, record.ClientIp);
        AddNullable(parameters, "requestPath", NpgsqlDbType.Varchar, record.RequestPath);
        AddNullable(parameters, "statusCode", NpgsqlDbType.Integer, record.StatusCode);
        AddNullable(parameters, "metadata", NpgsqlDbType.Jsonb, record.MetadataJson);
    }

    private static void AddNullable(
        NpgsqlParameterCollection parameters,
        string name,
        NpgsqlDbType type,
        object? value) =>
        parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
