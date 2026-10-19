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
            COALESCE($1, gen_random_uuid()), COALESCE($2, CURRENT_TIMESTAMP),
            $3, $4, $5,
            $6, $7, $8, $9, $10,
            $11, $12, $13, $14, $15,
            $16, $17)
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
        // Every batch command has its own ordinal bindings, in INSERT column
        // order. Explicit types preserve null/default handling without rewriting.
        AddNullable(parameters, NpgsqlDbType.Uuid, row.Id == Guid.Empty ? null : row.Id);
        AddNullable(parameters, NpgsqlDbType.TimestampTz, row.Timestamp == default ? null : row.Timestamp);
        parameters.Add(new NpgsqlParameter<int> { NpgsqlDbType = NpgsqlDbType.Integer, TypedValue = row.EventType });
        AddNullable(parameters, NpgsqlDbType.Integer, row.VirtualKeyId);
        AddNullable(parameters, NpgsqlDbType.Integer, row.VirtualKeyGroupId);
        AddNullable(parameters, NpgsqlDbType.Varchar, row.Model);
        AddNullable(parameters, NpgsqlDbType.Varchar, row.RequestId);
        AddNullable(parameters, NpgsqlDbType.Jsonb, row.UsageJson);
        AddNullable(parameters, NpgsqlDbType.Numeric, row.CalculatedCost);
        AddNullable(parameters, NpgsqlDbType.Varchar, row.FailureReason);
        AddNullable(parameters, NpgsqlDbType.Varchar, row.ProviderType);
        AddNullable(parameters, NpgsqlDbType.Integer, row.HttpStatusCode);
        AddNullable(parameters, NpgsqlDbType.Jsonb, row.MetadataJson);
        AddNullable(parameters, NpgsqlDbType.Varchar, row.RequestPath);
        parameters.Add(new NpgsqlParameter<bool> { NpgsqlDbType = NpgsqlDbType.Boolean, TypedValue = row.IsEstimated });
        AddNullable(parameters, NpgsqlDbType.Jsonb, row.ToolUsageJson);
        AddNullable(parameters, NpgsqlDbType.Numeric, row.ToolUsageCost);
    }

    private static void AddNullable(NpgsqlParameterCollection parameters, NpgsqlDbType type, object? value) =>
        parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value });
}
