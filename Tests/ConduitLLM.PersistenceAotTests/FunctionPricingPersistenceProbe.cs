using System.Text.Json;

using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;
using NpgsqlTypes;

internal static class FunctionPricingPersistenceProbe
{
    // The caller owns the schema and seeded configuration IDs 11/12/13.
    internal static async Task RunAsync(NpgsqlDataSource source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var asOf = new DateTime(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc);
        await using (var connection = await source.OpenConnectionAsync(deadline.Token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "FunctionCosts" (
                    "Id" integer PRIMARY KEY, "CostName" varchar(200) NOT NULL, "ProviderType" integer NOT NULL,
                    "Purpose" integer NULL, "Description" varchar(500) NULL, "BaseCost" decimal(18,8) NULL,
                    "PricingModel" integer NOT NULL, "CostPerExecution" decimal(18,8) NULL,
                    "CostPerResult" decimal(18,8) NULL, "CostPerToken" decimal(18,8) NULL,
                    "CostPerMinute" decimal(18,8) NULL, "TieredPricing" jsonb NULL, "PricingConfiguration" jsonb NULL,
                    "IsActive" boolean NOT NULL, "EffectiveDate" timestamptz NOT NULL, "ExpiryDate" timestamptz NULL,
                    "Priority" integer NOT NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
                INSERT INTO "FunctionCosts" VALUES
                    (101, 'Nullable', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                        TRUE, @asOf - interval '2 days', NULL, 1, @created, @updated),
                    (102, 'O''Hare 雪', @tavily, @answer, '', 1234567890.12345678, @hybrid,
                        0.00000001, 0, -2.34567890, 9999999999.99999999, @tiers, 'null',
                        TRUE, @asOf - interval '1 day', @asOf + interval '1 day', 1, @created, @updated),
                    (103, 'Inactive', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                        FALSE, @asOf - interval '1 day', NULL, 90, @created, @updated),
                    (104, 'Future', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                        TRUE, @asOf + interval '1 day', NULL, 80, @created, @updated),
                    (105, 'Expired', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                        TRUE, @asOf - interval '1 day', @asOf, 70, @created, @updated),
                    (106, 'Shadow target', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                        TRUE, @asOf - interval '1 day', NULL, 100, @created, @updated),
                    (107, 'Exact boundary and unknown enums', 197, -9, NULL, NULL, 123, NULL, NULL, NULL, NULL, '[]', @settings,
                        TRUE, @asOf, @asOf + interval '2 microseconds', 5, @created, @updated),
                    (108, 'Newer lower priority', @exa, NULL, NULL, NULL, @flat, NULL, NULL, NULL, NULL, NULL, NULL,
                        TRUE, @asOf, NULL, 0, @created, @updated);
                CREATE TABLE "FunctionCostMappings" (
                    "Id" integer PRIMARY KEY, "FunctionConfigurationId" integer NOT NULL REFERENCES "FunctionConfigurations" ("Id"),
                    "FunctionCostId" integer NOT NULL REFERENCES "FunctionCosts" ("Id"),
                    "FunctionCostId1" integer NULL REFERENCES "FunctionCosts" ("Id"), "IsActive" boolean NOT NULL,
                    "CreatedAt" timestamptz NOT NULL, UNIQUE ("FunctionConfigurationId", "FunctionCostId"));
                INSERT INTO "FunctionCostMappings" VALUES
                    (1, 11, 101, NULL, TRUE, @asOf), (2, 11, 102, 106, TRUE, @asOf),
                    (3, 11, 103, NULL, TRUE, @asOf), (4, 11, 104, NULL, TRUE, @asOf),
                    (5, 11, 105, NULL, TRUE, @asOf), (6, 11, 106, NULL, FALSE, @asOf),
                    (7, 11, 108, NULL, TRUE, @asOf), (8, 12, 107, NULL, TRUE, @asOf),
                    (9, 13, 103, 102, TRUE, @asOf), (10, 13, 104, NULL, TRUE, @asOf),
                    (11, 13, 105, NULL, TRUE, @asOf), (12, 12, 101, 106, TRUE, @asOf);
                """;
            command.Parameters.AddWithValue("asOf", NpgsqlDbType.TimestampTz, asOf);
            command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, asOf.AddDays(-3));
            command.Parameters.AddWithValue("updated", NpgsqlDbType.TimestampTz, asOf.AddDays(-2));
            command.Parameters.AddWithValue("exa", NpgsqlDbType.Integer, (int)FunctionProviderType.Exa);
            command.Parameters.AddWithValue("tavily", NpgsqlDbType.Integer, (int)FunctionProviderType.Tavily);
            command.Parameters.AddWithValue("answer", NpgsqlDbType.Integer, (int)FunctionPurpose.Answer);
            command.Parameters.AddWithValue("flat", NpgsqlDbType.Integer, (int)FunctionPricingModel.FlatRate);
            command.Parameters.AddWithValue("hybrid", NpgsqlDbType.Integer, (int)FunctionPricingModel.Hybrid);
            command.Parameters.AddWithValue("tiers", NpgsqlDbType.Jsonb, """{"tiers":[{"cost":0.1,"name":"雪"}],"unknown":[1,null]}""");
            command.Parameters.AddWithValue("settings", NpgsqlDbType.Jsonb, """{"quoted":"O'Hare 雪","nested":{"mode":"deep"}}""");
            await command.ExecuteNonQueryAsync(deadline.Token);
        }

        var store = new NpgsqlFunctionPricingRuntimeStore(source);
        var cost = await store.FindEligibleCostAsync(11, asOf, deadline.Token)
            ?? throw new InvalidOperationException("Native pricing omitted the eligible cost.");
        Require(cost.Id == 102 && cost.CostName == "O'Hare 雪" && cost.ProviderType == FunctionProviderType.Tavily &&
            cost.Purpose == FunctionPurpose.Answer && cost.PricingModel == FunctionPricingModel.Hybrid && cost.Description == "",
            "canonical mapping, eligibility before ranking, priority before newer date, strings/enums");
        Require(cost.BaseCost == 1234567890.12345678m && cost.CostPerExecution == 0.00000001m &&
            cost.CostPerResult == 0m && cost.CostPerToken == -2.34567890m && cost.CostPerMinute == 9999999999.99999999m,
            "all five exact decimal fields");
        Require(cost.IsActive && cost.Priority == 1 && cost.EffectiveDate == asOf.AddDays(-1) &&
            cost.ExpiryDate == asOf.AddDays(1) && cost.CreatedAt == asOf.AddDays(-3) &&
            cost.UpdatedAt == asOf.AddDays(-2) && cost.CreatedAt.Kind == DateTimeKind.Utc &&
            cost.UpdatedAt.Kind == DateTimeKind.Utc && cost.EffectiveDate.Kind == DateTimeKind.Utc &&
            cost.ExpiryDate.Value.Kind == DateTimeKind.Utc, "UTC dates and flags");
        using (var tiers = JsonDocument.Parse(cost.TieredPricing!))
            Require(tiers.RootElement.GetProperty("tiers")[0].GetProperty("name").GetString() == "雪" &&
                tiers.RootElement.GetProperty("unknown")[1].ValueKind == JsonValueKind.Null, "opaque JSONB configuration");
        Require(cost.PricingConfiguration == "null" && cost.FunctionMappings.Count == 0,
            "JSON null and unloaded inverse navigation");
        cost.CostName = "detached mutation";
        Require((await store.FindEligibleCostAsync(11, asOf, deadline.Token))?.CostName == "O'Hare 雪", "detached read");
        Require(await store.FindEligibleCostAsync(13, asOf, deadline.Token) is null &&
            await store.FindEligibleCostAsync(-1, asOf, deadline.Token) is null, "all-invalid and missing candidates");
        Require((await store.FindEligibleCostAsync(12, asOf.AddTicks(-10), deadline.Token))?.Id == 101,
            "before effective instant uses eligible fallback");
        var boundary = await store.FindEligibleCostAsync(12, asOf, deadline.Token);
        Require(boundary is not null && boundary.Id == 107 && (int)boundary.ProviderType == 197 &&
            (int)boundary.Purpose! == -9 && (int)boundary.PricingModel == 123 && boundary.EffectiveDate == asOf &&
            boundary.ExpiryDate == asOf.AddTicks(20), "effective equality, unknown enums, disabled configuration");
        using (var configuration = JsonDocument.Parse(boundary!.PricingConfiguration!))
            Require(configuration.RootElement.GetProperty("quoted").GetString() == "O'Hare 雪" &&
                configuration.RootElement.GetProperty("nested").GetProperty("mode").GetString() == "deep",
                "object-valued JSONB pricing configuration");
        using (var tiers = JsonDocument.Parse(boundary.TieredPricing!))
            Require(tiers.RootElement.GetArrayLength() == 0, "array-valued JSONB tier configuration");
        Require((await store.FindEligibleCostAsync(12, asOf.AddTicks(10), deadline.Token))?.Id == 107,
            "immediately before expiry");
        var nullable = await store.FindEligibleCostAsync(12, asOf.AddTicks(20), deadline.Token);
        Require(nullable is not null && nullable.Id == 101 && nullable.Purpose is null && nullable.Description is null &&
            nullable.BaseCost is null && nullable.CostPerExecution is null && nullable.CostPerResult is null &&
            nullable.CostPerToken is null && nullable.CostPerMinute is null && nullable.TieredPricing is null &&
            nullable.PricingConfiguration is null && nullable.ExpiryDate is null,
            "expiry equality excludes cost, canonical shadow isolation and null fields");

        await using (await source.OpenConnectionAsync(deadline.Token))
        {
            using var cancelled = new CancellationTokenSource();
            var read = store.FindEligibleCostAsync(11, asOf, cancelled.Token);
            try
            {
                Require(!read.IsCompleted, "read pending on occupied pool slot");
                cancelled.Cancel();
                try
                {
                    await read.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("Native pricing ignored cancelled pool acquisition.");
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { /* Expected owned cancellation; every other failure propagates. */ }
            }
            finally
            {
                cancelled.Cancel();
                try { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { /* Expected owned cancellation; every other failure propagates. */ }
            }
        }
        Require((await store.FindEligibleCostAsync(11, asOf, deadline.Token))?.Id == 102, "single-slot cancellation recovery");
        Console.WriteLine("Function pricing persistence passed: eligibility/ranking/canonical IDs, exact date boundaries, decimal/JSONB/null/enum scalars, detached reads and cancelled single-slot acquisition/reuse.");
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException($"Native function pricing contract failed: {contract}.");
    }
}
