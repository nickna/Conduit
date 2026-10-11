using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Configuration.Data;

/// <summary>Read-only presence checks for the pinned Wolverine PostgreSQL runtime resources.</summary>
/// <remarks>
/// This checks mandatory tables and columns, not full schema equivalence. The release
/// migrator owns provisioning; actual message delivery is verified by process tests.
/// Keep this inventory aligned with the pinned Wolverine package and Conduit topology.
/// Definitions: JasperFx/wolverine commit c1eab2a7f04de7350c3b54741ae00351c6c303d1,
/// PostgreSQL Schema/*Table.cs, PostgresqlMessageStore and Transport/*Table.cs.
/// </remarks>
public sealed class WolverineStorageStartupProbe
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string[] _relations;
    private readonly string[] _columns;

    public WolverineStorageStartupProbe(NpgsqlDataSource dataSource, string durabilitySchema,
        string transportSchema, IEnumerable<string> queueNames)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var relations = new List<string>();
        var columns = new List<string>();
        var durability = Quote(durabilitySchema);
        var transport = Quote(transportSchema);

        Add(durability, "wolverine_incoming_envelopes",
            ["id", "status", "owner_id", "execution_time", "attempts", "body", "message_type", "received_at", "keep_until"]);
        Add(durability, "wolverine_outgoing_envelopes",
            ["id", "owner_id", "destination", "deliver_by", "body", "attempts", "message_type"]);
        Add(durability, "wolverine_dead_letters",
            ["id", "execution_time", "body", "message_type", "received_at", "source", "exception_type", "exception_message", "sent_at", "replayable"]);
        Add(durability, "wolverine_nodes",
            ["id", "node_number", "description", "uri", "started", "health_check", "version", "capabilities"]);
        Add(durability, "wolverine_node_assignments", ["id", "node_id", "started"]);
        Add(durability, "wolverine_agent_restrictions", ["id", "uri", "type", "node"]);
        Add(durability, "wolverine_control_queue", ["id", "message_type", "node_id", "body", "posted", "expires"]);
        Add(durability, "wolverine_node_records", ["id", "node_number", "event_name", "timestamp", "description"]);

        ArgumentNullException.ThrowIfNull(queueNames);
        foreach (var queue in queueNames.Distinct(StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            // Conduit's fixed queue names fit PostgreSQL's identifier limit without
            // Wolverine's optional shortening algorithm.
            var table = "wolverine_queue_" + queue.Replace('-', '_').ToLowerInvariant();
            Add(transport, table, ["id", "body", "message_type", "keep_until", "timestamp"]);
            Add(transport, table + "_scheduled", ["id", "body", "message_type", "keep_until", "timestamp", "execution_time"]);
        }
        _relations = relations.ToArray();
        _columns = columns.ToArray();

        void Add(string schema, string table, string[] required)
        {
            var relation = schema + "." + Quote(table);
            foreach (var column in required)
            {
                relations.Add(relation);
                columns.Add(column);
            }
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT NOT EXISTS (
                SELECT 1
                FROM unnest(@relations::text[], @columns::text[]) AS expected(relation, column_name)
                WHERE NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_class AS resource
                    JOIN pg_catalog.pg_attribute AS actual ON actual.attrelid = resource.oid
                    WHERE resource.oid = to_regclass(expected.relation)
                      AND resource.relkind IN ('r', 'p')
                      AND actual.attname = expected.column_name
                      AND actual.attnum > 0 AND NOT actual.attisdropped
                )
            )
            """;
        command.Parameters.AddWithValue("relations", NpgsqlDbType.Array | NpgsqlDbType.Text, _relations);
        command.Parameters.AddWithValue("columns", NpgsqlDbType.Array | NpgsqlDbType.Text, _columns);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (identifier.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(identifier) > 63)
            throw new ArgumentException("PostgreSQL resource identifiers must fit 63 bytes and contain no null characters.", nameof(identifier));
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}
