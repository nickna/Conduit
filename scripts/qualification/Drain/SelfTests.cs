using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

internal static class SelfTests
{
    internal static int Run(string[] args)
    {
        var results = new List<object>();
        var baseline = new Poll { Database = "phase2_test", ReadOnly = "on", KeyCount = 1, GroupCount = 1, LogCount = 18, LogCost = ".00840000", LedgerDebit = ".00840000", Spent = ".00840000", Credits = "100.00000000", Balance = "99.99160000", Queues = [
            new("wolverine_conduit_gateway", "wolverine_incoming_envelopes", ["message_type", "status"], 0, 2, false),
            new("wolverine_conduit_gateway", "wolverine_outgoing_envelopes", ["message_type"], 0, 0, false),
            new("wolverine_queues", "wolverine_queue_spend_update_events", ["message_type"], 0, 0, false),
            new("wolverine_queues", "wolverine_queue_spend_update_events_scheduled", ["message_type"], 0, 0, false)] };
        void Assert(string name, bool passed) { results.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
        Poll Change(Action<JsonObject> change) { var node = JsonNode.Parse(JsonSerializer.Serialize(baseline))!.AsObject(); change(node); return node.Deserialize<Poll>()!; }
        string? output = null;
        string? replay = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output") output = args[++i];
            else if (args[i] == "--replay-dump") replay = args[++i];
            else if (args[i] != "--self-test") throw new InvalidOperationException("Invalid test arguments");
        }
        object? replayResult = null;
        try
        {
            Assert("exact-settled-state-passes-with-handled-retention", DrainRules.Failures(baseline, 18).Count == 0);
            foreach (var (name, field, value) in new[] { ("one-unit-ledger-gap", "LedgerDebit", ".00839999"), ("one-unit-spent-gap", "Spent", ".00839999"), ("one-unit-balance-gap", "Balance", "99.99160001"), ("nonfinite", "LogCost", "NaN"), ("binary-exponent-rejected", "LogCost", "8.4e-3") })
                Assert(name, DrainRules.Failures(Change(node => node[field] = value), 18).Count > 0);
            Assert("wrong-log-count-fails", DrainRules.Failures(baseline, 19).Contains("expected-request-log-count"));
            Assert("unowned-db-fails", DrainRules.Failures(Change(node => node["Database"] = "user_database"), 18).Contains("owned-database-readonly"));
            Assert("missing-queue-table-fails", DrainRules.Failures(Change(node => node["Queues"]!.AsArray().RemoveAt(3)), 18).Any(error => error.StartsWith("required-table:", StringComparison.Ordinal)));
            foreach (var index in Enumerable.Range(0, 4))
                Assert("queued-spend-fails:" + index, DrainRules.Failures(Change(node => node["Queues"]![index]!["ActiveKnownSpendRows"] = 1), 18).Contains("known-spend-queues-empty"));
            Assert("deadletter-spend-fails", DrainRules.Failures(Change(node => node["Queues"]!.AsArray().Add(JsonSerializer.SerializeToNode(new QueueState("wolverine_conduit_gateway", "wolverine_dead_letters", ["message_type"], 1, 0, true)))), 18).Contains("known-spend-queues-empty"));
            if (replay is not null)
            {
                var lines = File.ReadAllLines(replay);
                var start = Array.FindIndex(lines, line => line.StartsWith("COPY wolverine_queues.wolverine_queue_spend_update_events (", StringComparison.Ordinal));
                Assert("preserved-dump-spend-table-exists", start >= 0);
                var count = 0;
                decimal amount = 0;
                for (var i = start + 1; i < lines.Length && lines[i] != "\\."; i++)
                {
                    var fields = lines[i].Split('\t');
                    if (fields[2] != Program.SpendTypes[0]) continue;
                    var decoded = Encoding.UTF8.GetString(Convert.FromHexString(fields[1].TrimStart('\\', 'x')));
                    var jsonStart = decoded.IndexOf("{\"keyId\":", StringComparison.Ordinal);
                    Assert("preserved-envelope-has-json:" + count, jsonStart >= 0);
                    using var payload = JsonDocument.Parse(decoded[jsonStart..]);
                    amount += payload.RootElement.GetProperty("amount").GetDecimal();
                    count++;
                }
                Assert("preserved-dump-has-two-queued-spends-exact-point0025", count == 2 && amount == .0025m);
                var failed = Change(node => { node["LedgerDebit"] = ".00590000"; node["Spent"] = ".00590000"; node["Balance"] = "99.99410000"; node["Queues"]![2]!["ActiveKnownSpendRows"] = count; });
                var failures = DrainRules.Failures(failed, 18);
                Assert("preserved-dump-state-rejected-not-treated-as-drained", failures.Contains("known-spend-queues-empty") && failures.Contains("logs-ledger-spent-exact"));
                replayResult = new { file = Path.GetFileName(replay), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(replay))).ToLowerInvariant(), queuedRows = count, queuedAmount = amount.ToString(CultureInfo.InvariantCulture), failures, interpretation = "Retained durable pending spend; not a live database query or a permanent-loss assertion" };
            }
            if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(new { schemaVersion = 1, passed = true, tests = results, replay = replayResult }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"DrainProbe self-test passed {results.Count} checks");
            return 0;
        }
        catch (Exception error)
        {
            if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(new { schemaVersion = 1, passed = false, tests = results, failureType = error.GetType().Name }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine("DrainProbe self-test failed");
            return 1;
        }
    }
}
