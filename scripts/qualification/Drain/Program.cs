using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConduitLLM.Configuration.Utilities;
using Npgsql;
using NpgsqlTypes;

internal static class Program
{
    internal static readonly string[] SpendTypes = ["ConduitLLM.Core.Events.SpendUpdateRequested", "ConduitLLM.Configuration.Events.BatchSpendFlushRequestedEvent"];
    private const string Scope = "known persisted spend drained; not provider evidence completeness";
    private const string SchemaSql = """
        SELECT coalesce(json_agg(row_to_json(t)),'[]'::json) FROM (
          SELECT n.nspname AS schema,c.relname AS table,array_agg(a.attname::text ORDER BY a.attnum) AS columns
          FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
          JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
          WHERE c.relkind IN ('r','p') AND n.nspname LIKE 'wolverine%'
          GROUP BY n.nspname,c.relname HAVING bool_or(a.attname='message_type')
          ORDER BY n.nspname,c.relname) t;
        """;
    private const string QueueTemplate = "SELECT count(*) FILTER(WHERE {active}) AS active,count(*) FILTER(WHERE NOT ({active})) AS retained FROM {table} WHERE message_type=ANY(@types)";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string SnapshotSql = ReadResource("DrainProbe.snapshot.sql");
    private static readonly string QueryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SnapshotSql + SchemaSql + QueueTemplate + string.Join('|', SpendTypes)))).ToLowerInvariant();

    public static async Task<int> Main(string[] args)
    {
        string? output = null;
        var watch = Stopwatch.StartNew();
        var polls = new List<Poll>();
        var errors = new List<object>();
        long expected = -1;
        var timeout = 180;
        try
        {
            if (args.Contains("--self-test")) return SelfTests.Run(args);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length) throw new InvalidOperationException("Invalid arguments");
                switch (args[i])
                {
                    case "--expected-logs": expected = long.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                    case "--timeout-seconds": timeout = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                    case "--output": output = args[i + 1]; break;
                    default: throw new InvalidOperationException("Invalid arguments");
                }
            }
            if (expected < 0 || timeout is < 1 or > 600 || string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("Invalid arguments");
            var builder = PostgresConnectionStringParser.Parse(Environment.GetEnvironmentVariable("DATABASE_URL") ?? "");
            if (!Regex.IsMatch(builder.Database ?? "", "^phase2_[a-z0-9_]+$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("Owned database required");
            builder.Timeout = 5;
            builder.CommandTimeout = 5;
            builder.Pooling = false;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            while (!deadline.IsCancellationRequested)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(builder.ConnectionString);
                    await connection.OpenAsync(deadline.Token);
                    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, deadline.Token);
                    await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='5s';", connection, transaction)) await readOnly.ExecuteNonQueryAsync(deadline.Token);
                    var poll = await CaptureAsync(connection, transaction, deadline.Token);
                    poll.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
                    poll.Failures = DrainRules.Failures(poll, expected);
                    polls.Add(poll);
                    await transaction.RollbackAsync(deadline.Token);
                    if (poll.Failures.Count == 0)
                    {
                        Save(output, expected, true, watch, polls, errors);
                        return 0;
                    }
                    if (poll.LogCount > expected) break; // A polluted fixture cannot become valid by waiting.
                }
                catch (Exception error)
                {
                    errors.Add(new { elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, type = error.GetType().Name, sqlState = (error as PostgresException)?.SqlState });
                    if (error is OperationCanceledException) break;
                }
                Save(output, expected, false, watch, polls, errors);
                try { await Task.Delay(500, deadline.Token); } catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception error)
        {
            // Connection strings, provider exception messages and request payloads are never serialized.
            errors.Add(new { elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, type = error.GetType().Name, sqlState = (error as PostgresException)?.SqlState });
        }
        if (!string.IsNullOrWhiteSpace(output)) Save(output, expected, false, watch, polls, errors);
        else Console.Error.WriteLine("DrainProbe requires valid arguments and an output path.");
        return 1;
    }

    private static async Task<Poll> CaptureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(SnapshotSql, connection, transaction);
        using var json = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException()));
        var root = json.RootElement;
        var poll = new Poll { Database = root.GetProperty("database").GetString()!, ReadOnly = root.GetProperty("readOnly").GetString()!, KeyCount = root.GetProperty("keyCount").GetInt64(), GroupCount = root.GetProperty("groupCount").GetInt64(), LogCount = root.GetProperty("logCount").GetInt64(), LogCost = root.GetProperty("logCost").GetString()!, LedgerDebit = root.GetProperty("ledgerDebit").GetString()!, UnbilledOrNonpositiveLogs = root.GetProperty("unbilledOrNonpositiveLogs").GetInt64(), DeletedOrUnsupportedLedger = root.GetProperty("deletedOrUnsupportedLedger").GetInt64(), Balance = root.GetProperty("balance").ValueKind == JsonValueKind.String ? root.GetProperty("balance").GetString()! : "", Spent = root.GetProperty("spent").ValueKind == JsonValueKind.String ? root.GetProperty("spent").GetString()! : "", Credits = root.GetProperty("credits").ValueKind == JsonValueKind.String ? root.GetProperty("credits").GetString()! : "" };
        await using var schema = new NpgsqlCommand(SchemaSql, connection, transaction);
        using var tables = JsonDocument.Parse((string)(await schema.ExecuteScalarAsync(token) ?? throw new InvalidOperationException()));
        foreach (var table in tables.RootElement.EnumerateArray())
        {
            var schemaName = table.GetProperty("schema").GetString()!;
            var tableName = table.GetProperty("table").GetString()!;
            var columns = table.GetProperty("columns").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var incoming = tableName == "wolverine_incoming_envelopes";
            if (incoming && !columns.Contains("status")) throw new InvalidOperationException("Incoming status schema changed");
            var active = incoming ? "COALESCE(status::text,'')<>'Handled'" : "TRUE";
            var sql = QueueTemplate.Replace("{active}", active, StringComparison.Ordinal).Replace("{table}", Quote(schemaName) + "." + Quote(tableName), StringComparison.Ordinal);
            await using var count = new NpgsqlCommand(sql, connection, transaction);
            count.Parameters.AddWithValue("types", NpgsqlDbType.Array | NpgsqlDbType.Text, SpendTypes);
            await using var reader = await count.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            poll.Queues.Add(new QueueState(schemaName, tableName, columns, reader.GetInt64(0), reader.GetInt64(1), tableName.Contains("dead_letter", StringComparison.Ordinal)));
        }
        return poll;
    }

    private static string Quote(string name) => '"' + name.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string ReadResource(string name) { using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing embedded query"); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    private static void Save(string output, long expected, bool passed, Stopwatch watch, List<Poll> polls, List<object> errors) => File.WriteAllText(output, JsonSerializer.Serialize(new { schemaVersion = 1, passed, scope = Scope, expectedLogs = expected, elapsedMs = watch.Elapsed.TotalMilliseconds, polls, finalState = polls.LastOrDefault(), errors, queryHash = QueryHash, messageTypes = SpendTypes, runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), os = RuntimeInformation.OSDescription, host = Environment.MachineName, sourceSdk = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "SourceSdk").Value, assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))).ToLowerInvariant(), limitations = new[] { "Read-only repeatable-read PostgreSQL polls while application remains alive; no writes, queue consumption, holds or cleanup.", "Known spend message types only; unrelated media/admin queues may remain.", "A matching persisted cost/log/ledger state is not completeness of provider evidence. Redis reservation reconciliation remains separate.", "Handled incoming retention is reported separately; unhandled incoming, outgoing, transport/scheduled and dead-letter known spend rows must all be zero." } }, Json));
}

internal sealed class Poll
{
    public double ElapsedMilliseconds { get; set; }
    public string Database { get; init; } = "";
    public string ReadOnly { get; init; } = "";
    public long KeyCount { get; init; }
    public long GroupCount { get; init; }
    public long LogCount { get; init; }
    public long UnbilledOrNonpositiveLogs { get; init; }
    public long DeletedOrUnsupportedLedger { get; init; }
    public string LogCost { get; init; } = "";
    public string LedgerDebit { get; init; } = "";
    public string Balance { get; init; } = "";
    public string Spent { get; init; } = "";
    public string Credits { get; init; } = "";
    public List<QueueState> Queues { get; init; } = [];
    public List<string> Failures { get; set; } = [];
}
internal sealed record QueueState(string Schema, string Table, string[] Columns, long ActiveKnownSpendRows, long RetainedHandledKnownSpendRows, bool DeadLetters);
internal static class DrainRules
{
    internal static List<string> Failures(Poll poll, long expected)
    {
        var errors = new List<string>();
        void Check(string name, bool passed) { if (!passed) errors.Add(name); }
        Check("owned-database-readonly", Regex.IsMatch(poll.Database, "^phase2_[a-z0-9_]+$", RegexOptions.CultureInvariant) && poll.ReadOnly == "on");
        Check("unique-fixture-key-and-group", poll.KeyCount == 1 && poll.GroupCount == 1);
        Check("expected-request-log-count", poll.LogCount == expected);
        Check("all-recorded-requests-positive-and-billed", poll.UnbilledOrNonpositiveLogs == 0);
        Check("no-deleted-or-unsupported-ledger", poll.DeletedOrUnsupportedLedger == 0);
        static decimal Money(string value) => decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        try
        {
            Check("logs-ledger-spent-exact", Money(poll.LogCost) == Money(poll.LedgerDebit) && Money(poll.LogCost) == Money(poll.Spent));
            Check("balance-credits-minus-spent-exact", Money(poll.Balance) == Money(poll.Credits) - Money(poll.Spent));
        }
        catch (Exception error) when (error is FormatException or OverflowException) { errors.Add("invalid-exact-decimal"); }
        foreach (var required in new[] { ("wolverine_conduit_gateway", "wolverine_incoming_envelopes"), ("wolverine_conduit_gateway", "wolverine_outgoing_envelopes"), ("wolverine_queues", "wolverine_queue_spend_update_events"), ("wolverine_queues", "wolverine_queue_spend_update_events_scheduled") })
            Check("required-table:" + required.Item1 + "." + required.Item2, poll.Queues.Any(row => row.Schema == required.Item1 && row.Table == required.Item2));
        Check("known-spend-queues-empty", poll.Queues.All(row => row.ActiveKnownSpendRows == 0));
        return errors;
    }
}
