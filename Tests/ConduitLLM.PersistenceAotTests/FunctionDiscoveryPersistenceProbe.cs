using System.Text.Json;

using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;
using NpgsqlTypes;

internal static class FunctionDiscoveryPersistenceProbe
{
    // The caller's search path is an owned disposable schema. No cost/execution
    // tables are needed for this scalar-only discovery contract.
    internal static async Task RunAsync(NpgsqlDataSource source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var connection = await source.OpenConnectionAsync(deadline.Token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "FunctionConfigurations" (
                    "Id" integer PRIMARY KEY, "ProviderType" integer NOT NULL,
                    "ConfigurationName" varchar(200) NOT NULL, "Purpose" integer NOT NULL,
                    "DefaultExecutionMode" integer NOT NULL, "BaseUrl" varchar(500) NULL,
                    "IsEnabled" boolean NOT NULL, "TimeoutSeconds" integer NULL,
                    "CacheTtlMinutes" integer NULL, "MaxRetries" integer NULL,
                    "ProviderSettings" jsonb NULL, "ParameterSchema" jsonb NULL,
                    "Description" varchar(1000) NULL, "CreatedAt" timestamptz NOT NULL,
                    "UpdatedAt" timestamptz NOT NULL);
                INSERT INTO "FunctionConfigurations" VALUES
                    (11, @exa, 'Native 雪', @search, @async, 'https://example.test/custom', TRUE,
                        41, 7, 0, @settings, @schema, '', @created, @updated),
                    (12, @exa, 'Disabled', @search, @async, NULL, FALSE,
                        NULL, NULL, NULL, NULL, NULL, NULL, @created, @updated),
                    (13, 197, 'Unknown enum values', -9, 123, NULL, TRUE,
                        NULL, NULL, NULL, NULL, NULL, NULL, @created, @updated);
                """;
            command.Parameters.AddWithValue("exa", NpgsqlDbType.Integer, (int)FunctionProviderType.Exa);
            command.Parameters.AddWithValue("search", NpgsqlDbType.Integer, (int)FunctionPurpose.Search);
            command.Parameters.AddWithValue("async", NpgsqlDbType.Integer, (int)ExecutionMode.Asynchronous);
            command.Parameters.AddWithValue("settings", NpgsqlDbType.Jsonb, """{"mode":"deep"}""");
            command.Parameters.AddWithValue("schema", NpgsqlDbType.Jsonb, """{"example":{"query":"雪"}}""");
            var created = new DateTime(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc);
            command.Parameters.AddWithValue("created", created);
            command.Parameters.AddWithValue("updated", created.AddMinutes(1));
            await command.ExecuteNonQueryAsync(deadline.Token);
        }

        var store = new NpgsqlFunctionDiscoveryRuntimeStore(source);
        var catalog = await store.ListEnabledAsync(cancellationToken: deadline.Token);
        Require(catalog.Count == 2 && catalog.All(configuration => configuration.IsEnabled), "enabled catalog");
        var populated = await store.GetEnabledByIdAsync(11, deadline.Token)
            ?? throw new InvalidOperationException("Native function discovery omitted enabled ID.");
        Require(populated.ConfigurationName == "Native 雪" && populated.ProviderType == FunctionProviderType.Exa &&
            populated.Purpose == FunctionPurpose.Search && populated.DefaultExecutionMode == ExecutionMode.Asynchronous,
            "scalar strings/enums");
        Require(populated.BaseUrl == "https://example.test/custom" && populated.TimeoutSeconds == 41 &&
            populated.CacheTtlMinutes == 7 && populated.MaxRetries == 0 && populated.Description == "", "nullable scalars");
        Require(populated.CreatedAt.Kind == DateTimeKind.Utc && populated.UpdatedAt == populated.CreatedAt.AddMinutes(1),
            "UTC timestamps");
        Require(populated.CostMappings.Count == 0 && populated.Executions.Count == 0, "unloaded navigations");
        using (var settings = JsonDocument.Parse(populated.ProviderSettings!))
            Require(settings.RootElement.GetProperty("mode").GetString() == "deep", "JSONB settings");
        using (var schema = JsonDocument.Parse(populated.ParameterSchema!))
            Require(schema.RootElement.GetProperty("example").GetProperty("query").GetString() == "雪", "JSONB schema");
        populated.ConfigurationName = "detached mutation";
        Require((await store.GetEnabledByIdAsync(11, deadline.Token))?.ConfigurationName == "Native 雪", "detached reads");
        Require((await store.ListEnabledAsync(FunctionPurpose.Search, FunctionProviderType.Exa, deadline.Token))
            .Single().Id == 11, "combined filters");
        Require((await store.ListEnabledAsync(FunctionPurpose.Search, (FunctionProviderType)197, deadline.Token)).Count == 0,
            "empty filter result");
        var unknown = (await store.ListEnabledAsync((FunctionPurpose)(-9), (FunctionProviderType)197, deadline.Token)).Single();
        Require(unknown.Id == 13 && (int)unknown.DefaultExecutionMode == 123 && unknown.BaseUrl is null &&
            unknown.TimeoutSeconds is null && unknown.CacheTtlMinutes is null && unknown.MaxRetries is null &&
            unknown.ProviderSettings is null && unknown.ParameterSchema is null && unknown.Description is null,
            "unknown numeric enums and null columns");
        Require(await store.GetEnabledByIdAsync(12, deadline.Token) is null &&
            await store.GetEnabledByIdAsync(-1, deadline.Token) is null, "disabled/missing IDs");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await store.ListEnabledAsync(cancellationToken: cancelled.Token);
                throw new InvalidOperationException("Native function discovery ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { /* Expected owned cancellation; every other failure propagates. */ }
        }
        Require((await store.GetEnabledByIdAsync(11, deadline.Token))?.Id == 11, "single-slot reuse after cancellation");
        Console.WriteLine("Function discovery persistence passed: enabled/filter/ID reads, JSONB/null/enum scalars, detached graphs, cancellation and single-slot reuse.");
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException($"Native function discovery contract failed: {contract}.");
    }
}
