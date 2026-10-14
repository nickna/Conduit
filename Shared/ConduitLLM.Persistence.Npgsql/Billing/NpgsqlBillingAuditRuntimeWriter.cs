using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>NativeAOT-compatible typed writer for atomic scalar billing audit batches.</summary>
public sealed class NpgsqlBillingAuditRuntimeWriter : IBillingAuditRuntimeWriter
{
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
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;

        var committed = new BillingAuditRuntimeRecord[snapshots.Length];
        for (var index = 0; index < snapshots.Length; index++)
        {
            var row = snapshots[index];
            command.Parameters.Clear();
            AddParameters(command, row);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Billing audit insert did not return its generated values.");
            var id = reader.GetGuid(0);
            var timestamp = reader.GetDateTime(1);
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Billing audit insert returned more than one row.");
            committed[index] = row with
            {
                Id = row.Id == Guid.Empty ? id : row.Id,
                Timestamp = row.Timestamp == default ? timestamp : row.Timestamp
            };
        }

        await transaction.CommitAsync(cancellationToken);
        return Array.AsReadOnly(committed);
    }

    private static void AddParameters(NpgsqlCommand command, BillingAuditRuntimeRecord row)
    {
        AddNullable(command, "id", NpgsqlDbType.Uuid, row.Id == Guid.Empty ? null : row.Id);
        AddNullable(command, "timestamp", NpgsqlDbType.TimestampTz, row.Timestamp == default ? null : row.Timestamp);
        command.Parameters.AddWithValue("eventType", NpgsqlDbType.Integer, row.EventType);
        AddNullable(command, "virtualKeyId", NpgsqlDbType.Integer, row.VirtualKeyId);
        AddNullable(command, "virtualKeyGroupId", NpgsqlDbType.Integer, row.VirtualKeyGroupId);
        AddNullable(command, "model", NpgsqlDbType.Varchar, row.Model);
        AddNullable(command, "requestId", NpgsqlDbType.Varchar, row.RequestId);
        AddNullable(command, "usageJson", NpgsqlDbType.Jsonb, row.UsageJson);
        AddNullable(command, "calculatedCost", NpgsqlDbType.Numeric, row.CalculatedCost);
        AddNullable(command, "failureReason", NpgsqlDbType.Varchar, row.FailureReason);
        AddNullable(command, "providerType", NpgsqlDbType.Varchar, row.ProviderType);
        AddNullable(command, "httpStatusCode", NpgsqlDbType.Integer, row.HttpStatusCode);
        AddNullable(command, "metadataJson", NpgsqlDbType.Jsonb, row.MetadataJson);
        AddNullable(command, "requestPath", NpgsqlDbType.Varchar, row.RequestPath);
        command.Parameters.AddWithValue("isEstimated", NpgsqlDbType.Boolean, row.IsEstimated);
        AddNullable(command, "toolUsageJson", NpgsqlDbType.Jsonb, row.ToolUsageJson);
        AddNullable(command, "toolUsageCost", NpgsqlDbType.Numeric, row.ToolUsageCost);
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
