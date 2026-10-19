using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Npgsql;
using Npgsql;

internal static class RequestLogBatchPersistenceProbe
{
    internal static async Task RunAsync(NpgsqlDataSource source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var store = new NpgsqlRequestLogBatchRuntimeStore(source);
        var instant = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(9);
        var record = new RequestLogRuntimeRecord
        {
            VirtualKeyId = 41, ModelName = "native-request-batch-probe", ProviderId = 12, ProviderType = "OpenAI",
            ModelProviderMappingId = 77, PromptCachingEligible = true, PromptCachingPolicyApplied = true,
            CachedReadSavings = .123456785m, CacheWritePremium = .023456785m, RoutingAffinityUsed = true,
            RoutingDecisionReason = "affinity", RoutingFailoverCount = 1, RequestType = "chat",
            InputTokens = 5, OutputTokens = 3, CachedInputTokens = 2, CachedWriteTokens = 1,
            Cost = 1.234567895m, BillingMethod = 1, ProviderReportedCostUsd = .123456785m,
            ProviderCostMarkupMultiplier = 1.123456785m, BilledAtUtc = instant,
            ResponseTimeMs = 12.5, Timestamp = instant, UserId = "native-user", ClientIp = "192.0.2.41",
            RequestPath = "/v1/chat/completions", StatusCode = 200, MetadataJson = """{"native":true,"x":1}"""
        };
        var ids = await store.AllocateIdsAsync(103, token);
        Require(ids.Count == 103 && ids.All(id => id > 0) && ids.Distinct().Count() == 103, "sequence identities");
        var entries = ids.Select(id => new RequestLogRuntimeBatchEntry(id, record)).ToArray();
        await store.WriteBatchAsync(entries, token); // Cross the transactional chunk boundary.
        await store.WriteBatchAsync(entries.Select(entry => entry with
        {
            Record = record with
            {
                Timestamp = instant.AddTicks(-1), BilledAtUtc = instant.AddTicks(-1),
                Cost = 1.234567899m, CachedReadSavings = .123456789m, CacheWritePremium = .023456789m,
                ProviderReportedCostUsd = .123456789m, ProviderCostMarkupMultiplier = 1.123456789m,
                MetadataJson = """{ "x": 1, "native": true }"""
            }
        }).ToArray(), token);
        Require(await CountAsync(source, token) == 103, "canonical replay without duplicates");
        var extra = (await store.AllocateIdsAsync(1, token))[0];
        var rejected = false;
        try
        {
            await store.WriteBatchAsync([entries[0] with { Record = record with { ProviderId = 99 } },
                new RequestLogRuntimeBatchEntry(extra, record)], token);
        }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && await CountAsync(source, token) == 103, "changed scalar rejects and rolls back new row");
        await using var command = source.CreateCommand("""
            SELECT "Cost", "Timestamp", "CachedReadSavings", "Metadata"->>'native'
            FROM "RequestLogs" WHERE "Id" = @id
            """);
        command.Parameters.AddWithValue("id", ids[0]);
        await using var reader = await command.ExecuteReaderAsync(token);
        Require(await reader.ReadAsync(token) && reader.GetDecimal(0) == 1.23456790m &&
            reader.GetDateTime(1) == instant.AddTicks(-9) && reader.GetDecimal(2) == .12345679m &&
            reader.GetString(3) == "true", "typed numeric/UTC/jsonb values");
    }

    private static async Task<long> CountAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT count(*) FROM \"RequestLogs\" WHERE \"ModelName\" = 'native-request-batch-probe'");
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Native request-log batch probe failed: " + description);
    }
}
