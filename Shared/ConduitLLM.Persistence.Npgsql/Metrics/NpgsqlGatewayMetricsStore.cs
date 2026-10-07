using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql store for Gateway operational metrics.
/// </summary>
public sealed class NpgsqlGatewayMetricsStore : IGatewayMetricsStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlGatewayMetricsStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<IReadOnlyList<GatewayModelUsageMetric>> GetModelUsageAsync(
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ModelName", COALESCE("ProviderType", 'unknown'), AVG("ResponseTimeMs")
            FROM "RequestLogs"
            WHERE "Timestamp" >= @since
            GROUP BY "ModelName", COALESCE("ProviderType", 'unknown')
            """;
        AddTimestamp(command, "since", since);
        var results = new List<GatewayModelUsageMetric>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new GatewayModelUsageMetric(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDouble(2)));
        }
        return results;
    }

    public async Task<IReadOnlyList<GatewayProviderCostMetric>> GetProviderCostsAsync(
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE("ProviderType", 'unknown'), SUM("Cost")
            FROM "RequestLogs"
            WHERE COALESCE("BilledAtUtc", "Timestamp") >= @since AND "Cost" > 0
            GROUP BY COALESCE("ProviderType", 'unknown')
            """;
        AddTimestamp(command, "since", since);
        var results = new List<GatewayProviderCostMetric>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new GatewayProviderCostMetric(reader.GetString(0), reader.GetDecimal(1)));
        }
        return results;
    }

    public async Task<GatewayActiveEntityMetrics> GetActiveEntitiesAsync(
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var keyCommand = connection.CreateCommand();
        keyCommand.CommandText = """
            SELECT COUNT(*)::integer
            FROM "VirtualKeys"
            WHERE "IsEnabled" AND ("ExpiresAt" IS NULL OR "ExpiresAt" > @now)
            """;
        AddTimestamp(keyCommand, "now", now);
        var activeVirtualKeyCount = (int)(await keyCommand.ExecuteScalarAsync(cancellationToken) ?? 0);

        await using var mappingCommand = connection.CreateCommand();
        mappingCommand.CommandText = """
            SELECT mapping."ProviderId", COUNT(*)::integer
            FROM "ModelProviderMappings" mapping
            INNER JOIN "Providers" provider ON provider."Id" = mapping."ProviderId"
            WHERE mapping."IsEnabled" AND provider."IsEnabled"
            GROUP BY mapping."ProviderId"
            """;
        var mappings = new List<GatewayProviderMappingMetric>();
        await using var reader = await mappingCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mappings.Add(new GatewayProviderMappingMetric(reader.GetInt32(0), reader.GetInt32(1)));
        }
        return new GatewayActiveEntityMetrics(activeVirtualKeyCount, mappings);
    }

    public async Task<GatewayTaskQueueMetrics> GetTaskQueueMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var queueCommand = connection.CreateCommand();
        queueCommand.CommandText = """
            SELECT "Type", "State", COUNT(*)::integer
            FROM "AsyncTasks"
            WHERE NOT "IsArchived" AND "State" IN (0, 1)
            GROUP BY "Type", "State"
            """;
        var queueDepths = new List<GatewayTaskQueueMetric>();
        await using (var reader = await queueCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                queueDepths.Add(new GatewayTaskQueueMetric(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2)));
            }
        }

        await using var pendingCommand = connection.CreateCommand();
        pendingCommand.CommandText = """
            SELECT "Type", MIN("CreatedAt")
            FROM "AsyncTasks"
            WHERE NOT "IsArchived" AND "State" = 0
            GROUP BY "Type"
            """;
        var oldestPendingTasks = new List<GatewayPendingTaskMetric>();
        await using var pendingReader = await pendingCommand.ExecuteReaderAsync(cancellationToken);
        while (await pendingReader.ReadAsync(cancellationToken))
        {
            oldestPendingTasks.Add(new GatewayPendingTaskMetric(
                pendingReader.GetString(0),
                pendingReader.GetDateTime(1)));
        }
        return new GatewayTaskQueueMetrics(queueDepths, oldestPendingTasks);
    }

    public async Task<IReadOnlyList<GatewayGenerationTaskMetric>> GetGenerationTaskMetricsAsync(
        string taskType,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "State", COUNT(*)::integer,
                AVG(EXTRACT(EPOCH FROM ("CompletedAt" - "CreatedAt")))
                    FILTER (WHERE "CompletedAt" IS NOT NULL)
            FROM "AsyncTasks"
            WHERE "Type" = @taskType AND "CreatedAt" >= @since
            GROUP BY "State"
            """;
        command.Parameters.AddWithValue("taskType", NpgsqlDbType.Varchar, taskType);
        AddTimestamp(command, "since", since);
        var results = new List<GatewayGenerationTaskMetric>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new GatewayGenerationTaskMetric(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : Convert.ToDouble(reader.GetValue(2))));
        }
        return results;
    }

    public async Task<IReadOnlyList<GatewayVirtualKeySpendMetric>> GetTopVirtualKeySpendAsync(
        DateTime from,
        DateTime before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "VirtualKeyId", SUM("Amount")
            FROM "VirtualKeySpendHistory"
            WHERE "Timestamp" >= @from AND "Timestamp" < @before
            GROUP BY "VirtualKeyId"
            ORDER BY SUM("Amount") DESC
            LIMIT @limit
            """;
        AddTimestamp(command, "from", from);
        AddTimestamp(command, "before", before);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        var results = new List<GatewayVirtualKeySpendMetric>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new GatewayVirtualKeySpendMetric(reader.GetInt32(0), reader.GetDecimal(1)));
        }
        return results;
    }

    private static void AddTimestamp(NpgsqlCommand command, string name, DateTime value) =>
        command.Parameters.AddWithValue(name, NpgsqlDbType.TimestampTz, value);
}
