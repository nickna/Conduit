using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>NativeAOT-compatible typed writer for atomic scalar billing audit batches.</summary>
public sealed class NpgsqlBillingAuditRuntimeWriter : IBillingAuditRuntimeWriter
{
    private const int CommandsPerBatch = 100;

    private const string InsertSql = """
        INSERT INTO "BillingAuditEvents" (
            "Id", "Timestamp", "EventType", "VirtualKeyId", "VirtualKeyGroupId",
            "Model", "RequestId", "UsageJson", "CalculatedCost", "FailureReason",
            "ProviderType", "HttpStatusCode", "MetadataJson", "RequestPath", "IsEstimated",
            "ToolUsageJson", "ToolUsageCost")
        VALUES (
            COALESCE(@id, gen_random_uuid()), COALESCE(@timestamp, CURRENT_TIMESTAMP),
            @eventType, @virtualKeyId, @virtualKeyGroupId,
            @model, @requestId, @usageJson, @calculatedCost, @failureReason,
            @providerType, @httpStatusCode, @metadataJson, @requestPath, @isEstimated,
            @toolUsageJson, @toolUsageCost)
        RETURNING "Id", "Timestamp"
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlBillingAuditRuntimeWriter(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BillingAuditRuntimeRecord>> WriteBatchAsync(
        IReadOnlyList<BillingAuditRuntimeRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var snapshots = new BillingAuditRuntimeRecord[records.Count];
        for (var index = 0; index < snapshots.Length; index++)
            snapshots[index] = records[index] ??
                throw new ArgumentException("The batch cannot contain null records.", nameof(records));
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshots.Length == 0) return Array.Empty<BillingAuditRuntimeRecord>();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var committed = new BillingAuditRuntimeRecord[snapshots.Length];
        for (var offset = 0; offset < snapshots.Length;)
        {
            // Bound transient parameters during a large shutdown drain, while
            // retaining the whole call's single transaction and commit.
            var count = Math.Min(CommandsPerBatch, snapshots.Length - offset);
            await using var batch = new NpgsqlBatch(connection, transaction);
            for (var index = 0; index < count; index++)
            {
                var command = new NpgsqlBatchCommand(InsertSql);
                AddParameters(command.Parameters, snapshots[offset + index]);
                batch.BatchCommands.Add(command);
            }
            await using (var reader = await batch.ExecuteReaderAsync(cancellationToken))
            {
                for (var index = 0; index < count; index++)
                {
                    if (index > 0 && !await reader.NextResultAsync(cancellationToken))
                        throw new InvalidOperationException("Billing audit batch did not return every insert result.");
                    if (!await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("Billing audit insert did not return its generated values.");
                    var id = reader.GetGuid(0);
                    var timestamp = reader.GetDateTime(1);
                    if (await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("Billing audit insert returned more than one row.");
                    var row = snapshots[offset + index];
                    committed[offset + index] = row with
                    {
                        Id = row.Id == Guid.Empty ? id : row.Id,
                        Timestamp = row.Timestamp == default ? timestamp : row.Timestamp
                    };
                }
                if (await reader.NextResultAsync(cancellationToken))
                    throw new InvalidOperationException("Billing audit batch returned an unexpected insert result.");
            }
            offset += count;
        }

        await transaction.CommitAsync(cancellationToken);
        return Array.AsReadOnly(committed);
    }

    private static void AddParameters(NpgsqlParameterCollection parameters, BillingAuditRuntimeRecord row)
    {
        AddNullable(parameters, "id", NpgsqlDbType.Uuid, row.Id == Guid.Empty ? null : row.Id);
        AddNullable(parameters, "timestamp", NpgsqlDbType.TimestampTz, row.Timestamp == default ? null : row.Timestamp);
        parameters.AddWithValue("eventType", NpgsqlDbType.Integer, row.EventType);
        AddNullable(parameters, "virtualKeyId", NpgsqlDbType.Integer, row.VirtualKeyId);
        AddNullable(parameters, "virtualKeyGroupId", NpgsqlDbType.Integer, row.VirtualKeyGroupId);
        AddNullable(parameters, "model", NpgsqlDbType.Varchar, row.Model);
        AddNullable(parameters, "requestId", NpgsqlDbType.Varchar, row.RequestId);
        AddNullable(parameters, "usageJson", NpgsqlDbType.Jsonb, row.UsageJson);
        AddNullable(parameters, "calculatedCost", NpgsqlDbType.Numeric, row.CalculatedCost);
        AddNullable(parameters, "failureReason", NpgsqlDbType.Varchar, row.FailureReason);
        AddNullable(parameters, "providerType", NpgsqlDbType.Varchar, row.ProviderType);
        AddNullable(parameters, "httpStatusCode", NpgsqlDbType.Integer, row.HttpStatusCode);
        AddNullable(parameters, "metadataJson", NpgsqlDbType.Jsonb, row.MetadataJson);
        AddNullable(parameters, "requestPath", NpgsqlDbType.Varchar, row.RequestPath);
        parameters.AddWithValue("isEstimated", NpgsqlDbType.Boolean, row.IsEstimated);
        AddNullable(parameters, "toolUsageJson", NpgsqlDbType.Jsonb, row.ToolUsageJson);
        AddNullable(parameters, "toolUsageCost", NpgsqlDbType.Numeric, row.ToolUsageCost);
    }

    private static void AddNullable(NpgsqlParameterCollection parameters, string name, NpgsqlDbType type, object? value) =>
        parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
