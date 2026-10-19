using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using Npgsql;
using NpgsqlTypes;
using System.Data;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Native-compatible request-log batches with stable, verified replay identities.</summary>
public sealed class NpgsqlRequestLogBatchRuntimeStore(NpgsqlDataSource dataSource) : IRequestLogBatchRuntimeStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private const int BatchSize = 100;

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> AllocateIdsAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0) return Array.Empty<int>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ordinal, nextval(pg_get_serial_sequence('"RequestLogs"', 'Id')::regclass)
            FROM generate_series(1, @count) AS allocation(ordinal) ORDER BY ordinal
            """;
        command.Parameters.AddWithValue("count", NpgsqlDbType.Integer, count);
        var ids = new List<int>(count);
        var unique = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = checked((int)reader.GetInt64(1));
            if (reader.GetInt32(0) != ids.Count + 1 || id <= 0 || !unique.Add(id))
                throw new InvalidOperationException("Invalid request-log sequence allocation.");
            ids.Add(id);
        }
        if (ids.Count != count) throw new InvalidOperationException("Incomplete request-log sequence allocation.");
        return ids;
    }

    /// <inheritdoc />
    public async Task WriteBatchAsync(IReadOnlyList<RequestLogRuntimeBatchEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();
        var identities = new HashSet<int>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Record);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Record.ModelName);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Record.RequestType);
            if (entry.Id <= 0 || !identities.Add(entry.Id))
                throw new ArgumentException("Request-log batch identities must be distinct positive integers.", nameof(entries));
        }
        if (entries.Count == 0) return;

        // Stable order also keeps concurrent deliberate replays from taking
        // conflict/verification locks in opposite orders.
        var ordered = entries.OrderBy(entry => entry.Id).ToArray();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        for (var offset = 0; offset < ordered.Length; offset += BatchSize)
        {
            var count = Math.Min(BatchSize, ordered.Length - offset);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var conflicts = new List<RequestLogRuntimeBatchEntry>();
            await using (var batch = new NpgsqlBatch(connection, transaction))
            {
                for (var index = offset; index < offset + count; index++)
                    batch.BatchCommands.Add(CreateCommand(InsertSql, ordered[index]));
                await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
                for (var index = offset; index < offset + count; index++)
                {
                    var entry = ordered[index];
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        if (reader.GetInt32(0) != entry.Id || await reader.ReadAsync(cancellationToken))
                            throw new InvalidOperationException("Unexpected request-log INSERT result.");
                    }
                    else conflicts.Add(entry);
                    if (await reader.NextResultAsync(cancellationToken) != (index + 1 < offset + count))
                        throw new InvalidOperationException("Incomplete request-log INSERT batch.");
                }
            }

            // A separate command gets a new READ COMMITTED snapshot after an
            // INSERT conflict waited for another transaction. FOR SHARE keeps
            // every verified scalar stable through this transaction's commit.
            if (conflicts.Count != 0)
            {
                await using var verification = new NpgsqlBatch(connection, transaction);
                foreach (var entry in conflicts)
                    verification.BatchCommands.Add(CreateCommand(VerifySql, entry));
                await using var reader = await verification.ExecuteReaderAsync(cancellationToken);
                for (var index = 0; index < conflicts.Count; index++)
                {
                    if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0) ||
                        await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("A request-log replay conflicts with its persisted scalar values.");
                    if (await reader.NextResultAsync(cancellationToken) != (index + 1 < conflicts.Count))
                        throw new InvalidOperationException("Incomplete request-log replay verification.");
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static NpgsqlBatchCommand CreateCommand(string sql, RequestLogRuntimeBatchEntry entry)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, entry.Id);
        NpgsqlRequestLogRuntimeStore.AddParameters(command.Parameters, entry.Record);
        return command;
    }

    private const string InsertSql = """
        INSERT INTO "RequestLogs" (
            "Id", "VirtualKeyId", "ModelName", "ProviderId", "ProviderType",
            "ModelProviderMappingId", "PromptCachingEligible", "PromptCachingPolicyApplied",
            "CachedReadSavings", "CacheWritePremium", "RoutingAffinityUsed",
            "RoutingDecisionReason", "RoutingFailoverCount", "RequestType", "InputTokens",
            "OutputTokens", "CachedInputTokens", "CachedWriteTokens", "Cost",
            "BillingMethod", "ProviderReportedCostUsd", "ProviderCostMarkupMultiplier",
            "BilledAtUtc", "ResponseTimeMs", "Timestamp", "UserId", "ClientIp",
            "RequestPath", "StatusCode", "Metadata")
        VALUES (
            @id, @virtualKeyId, @modelName, @providerId, @providerType,
            @modelProviderMappingId, @promptCachingEligible, @promptCachingPolicyApplied,
            @cachedReadSavings, @cacheWritePremium, @routingAffinityUsed,
            @routingDecisionReason, @routingFailoverCount, @requestType, @inputTokens,
            @outputTokens, @cachedInputTokens, @cachedWriteTokens, @cost,
            @billingMethod, @providerReportedCostUsd, @providerCostMarkupMultiplier,
            @billedAtUtc, @responseTimeMs, @timestamp, @userId, @clientIp,
            @requestPath, @statusCode, @metadata)
        ON CONFLICT ("Id") DO NOTHING RETURNING "Id"
        """;

    private const string VerifySql = """
        SELECT
            "VirtualKeyId" IS NOT DISTINCT FROM @virtualKeyId AND
            "ModelName" IS NOT DISTINCT FROM @modelName AND
            "ProviderId" IS NOT DISTINCT FROM @providerId AND
            "ProviderType" IS NOT DISTINCT FROM @providerType AND
            "ModelProviderMappingId" IS NOT DISTINCT FROM @modelProviderMappingId AND
            "PromptCachingEligible" IS NOT DISTINCT FROM @promptCachingEligible AND
            "PromptCachingPolicyApplied" IS NOT DISTINCT FROM @promptCachingPolicyApplied AND
            "CachedReadSavings" IS NOT DISTINCT FROM CAST(@cachedReadSavings AS numeric(18,8)) AND
            "CacheWritePremium" IS NOT DISTINCT FROM CAST(@cacheWritePremium AS numeric(18,8)) AND
            "RoutingAffinityUsed" IS NOT DISTINCT FROM @routingAffinityUsed AND
            "RoutingDecisionReason" IS NOT DISTINCT FROM @routingDecisionReason AND
            "RoutingFailoverCount" IS NOT DISTINCT FROM @routingFailoverCount AND
            "RequestType" IS NOT DISTINCT FROM @requestType AND
            "InputTokens" IS NOT DISTINCT FROM @inputTokens AND
            "OutputTokens" IS NOT DISTINCT FROM @outputTokens AND
            "CachedInputTokens" IS NOT DISTINCT FROM @cachedInputTokens AND
            "CachedWriteTokens" IS NOT DISTINCT FROM @cachedWriteTokens AND
            "Cost" IS NOT DISTINCT FROM CAST(@cost AS numeric(12,8)) AND
            "BillingMethod" IS NOT DISTINCT FROM @billingMethod AND
            "ProviderReportedCostUsd" IS NOT DISTINCT FROM CAST(@providerReportedCostUsd AS numeric(18,8)) AND
            "ProviderCostMarkupMultiplier" IS NOT DISTINCT FROM CAST(@providerCostMarkupMultiplier AS numeric(18,8)) AND
            "BilledAtUtc" IS NOT DISTINCT FROM @billedAtUtc AND
            "ResponseTimeMs" IS NOT DISTINCT FROM @responseTimeMs AND
            "Timestamp" IS NOT DISTINCT FROM @timestamp AND
            "UserId" IS NOT DISTINCT FROM @userId AND
            "ClientIp" IS NOT DISTINCT FROM @clientIp AND
            "RequestPath" IS NOT DISTINCT FROM @requestPath AND
            "StatusCode" IS NOT DISTINCT FROM @statusCode AND
            "Metadata" IS NOT DISTINCT FROM @metadata
        FROM "RequestLogs" WHERE "Id" = @id FOR SHARE
        """;
}
