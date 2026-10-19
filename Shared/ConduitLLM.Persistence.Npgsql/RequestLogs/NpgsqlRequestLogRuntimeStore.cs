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
        Validate(record);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = InsertSql;
        AddParameters(command.Parameters, record);
        // Autocommit may already have completed if a trigger changes RETURNING.
        // A cardinality failure is therefore never a signal that retry is safe.
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Request log insert did not return its generated ID.");
        var id = reader.GetInt32(0);
        if (await reader.ReadAsync(cancellationToken) || await reader.NextResultAsync(cancellationToken))
            throw new InvalidOperationException("Request log insert returned unexpected generated IDs.");
        return id;
    }

    internal static void Validate(RequestLogRuntimeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RequestType);
    }

    internal const string InsertSql = """
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
                $1, $2, $3, $4,
                $5, $6, $7,
                $8, $9, $10,
                $11, $12, $13, $14,
                $15, $16, $17, $18,
                $19, $20, $21,
                $22, $23, $24, $25, $26,
                $27, $28, $29)
            RETURNING "Id"
            """;
    internal static void AddParameters(NpgsqlParameterCollection parameters, RequestLogRuntimeRecord record)
    {
        // Native PostgreSQL placeholders bypass named-SQL rewriting. Keep this
        // list in INSERT column order, including explicitly typed SQL nulls.
        parameters.Add(new NpgsqlParameter<int> { NpgsqlDbType = NpgsqlDbType.Integer, TypedValue = record.VirtualKeyId });
        parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Varchar, TypedValue = record.ModelName });
        AddNullable(parameters, NpgsqlDbType.Integer, record.ProviderId);
        AddNullable(parameters, NpgsqlDbType.Varchar, record.ProviderType);
        AddNullable(parameters, NpgsqlDbType.Integer, record.ModelProviderMappingId);
        parameters.Add(new NpgsqlParameter<bool> { NpgsqlDbType = NpgsqlDbType.Boolean, TypedValue = record.PromptCachingEligible });
        parameters.Add(new NpgsqlParameter<bool> { NpgsqlDbType = NpgsqlDbType.Boolean, TypedValue = record.PromptCachingPolicyApplied });
        parameters.Add(new NpgsqlParameter<decimal> { NpgsqlDbType = NpgsqlDbType.Numeric, TypedValue = record.CachedReadSavings });
        parameters.Add(new NpgsqlParameter<decimal> { NpgsqlDbType = NpgsqlDbType.Numeric, TypedValue = record.CacheWritePremium });
        parameters.Add(new NpgsqlParameter<bool> { NpgsqlDbType = NpgsqlDbType.Boolean, TypedValue = record.RoutingAffinityUsed });
        AddNullable(parameters, NpgsqlDbType.Varchar, record.RoutingDecisionReason);
        parameters.Add(new NpgsqlParameter<int> { NpgsqlDbType = NpgsqlDbType.Integer, TypedValue = record.RoutingFailoverCount });
        parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Varchar, TypedValue = record.RequestType });
        parameters.Add(new NpgsqlParameter<int> { NpgsqlDbType = NpgsqlDbType.Integer, TypedValue = record.InputTokens });
        parameters.Add(new NpgsqlParameter<int> { NpgsqlDbType = NpgsqlDbType.Integer, TypedValue = record.OutputTokens });
        AddNullable(parameters, NpgsqlDbType.Integer, record.CachedInputTokens);
        AddNullable(parameters, NpgsqlDbType.Integer, record.CachedWriteTokens);
        parameters.Add(new NpgsqlParameter<decimal> { NpgsqlDbType = NpgsqlDbType.Numeric, TypedValue = record.Cost });
        AddNullable(parameters, NpgsqlDbType.Integer, record.BillingMethod);
        AddNullable(parameters, NpgsqlDbType.Numeric, record.ProviderReportedCostUsd);
        AddNullable(parameters, NpgsqlDbType.Numeric, record.ProviderCostMarkupMultiplier);
        AddNullable(parameters, NpgsqlDbType.TimestampTz, record.BilledAtUtc);
        parameters.Add(new NpgsqlParameter<double> { NpgsqlDbType = NpgsqlDbType.Double, TypedValue = record.ResponseTimeMs });
        parameters.Add(new NpgsqlParameter<DateTime> { NpgsqlDbType = NpgsqlDbType.TimestampTz, TypedValue = record.Timestamp });
        AddNullable(parameters, NpgsqlDbType.Varchar, record.UserId);
        AddNullable(parameters, NpgsqlDbType.Varchar, record.ClientIp);
        AddNullable(parameters, NpgsqlDbType.Varchar, record.RequestPath);
        AddNullable(parameters, NpgsqlDbType.Integer, record.StatusCode);
        AddNullable(parameters, NpgsqlDbType.Jsonb, record.MetadataJson);
    }

    private static void AddNullable(
        NpgsqlParameterCollection parameters,
        NpgsqlDbType type,
        object? value) =>
        parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value });
}
