using System.Text.Json.Nodes;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

internal static class FunctionExecutionPersistenceProbe
{
    private static readonly DateTime Instant = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    internal static async Task RunAsync(NpgsqlDataSource source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        Require(new NpgsqlConnectionStringBuilder(source.ConnectionString).MaxPoolSize == 1, "single-slot source");
        await using (var connection = await source.OpenConnectionAsync(token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "FunctionExecutions" (
                    "Id" uuid PRIMARY KEY, "FunctionConfigurationId" integer NOT NULL REFERENCES "FunctionConfigurations" ("Id") ON DELETE CASCADE,
                    "VirtualKeyId" integer NOT NULL, "ExecutionMode" integer NOT NULL, "State" integer NOT NULL,
                    "RequestedAt" timestamptz NOT NULL, "StartedAt" timestamptz NULL, "CompletedAt" timestamptz NULL,
                    "Duration" interval NULL, "RequestJson" jsonb NULL, "ResponseJson" jsonb NULL,
                    "ErrorMessage" varchar(2000) NULL, "EstimatedCost" numeric(18,8) NULL, "ActualCost" numeric(18,8) NULL,
                    "CostCalculationDetails" jsonb NULL, "RetryCount" integer NOT NULL, "NextRetryAt" timestamptz NULL,
                    "LeasedBy" varchar(100) NULL, "LeaseExpiryTime" timestamptz NULL, "Version" integer NOT NULL,
                    "WebhookUrl" varchar(1000) NULL, "WebhookDelivered" boolean NOT NULL,
                    "ProgressPercentage" integer NULL, "StatusMessage" varchar(500) NULL);
                """;
            await command.ExecuteNonQueryAsync(token);
        }

        var writer = new NpgsqlFunctionExecutionRuntimeWriter(source);
        var original = Snapshot(true);
        var suppliedId = original.Id;
        Require(await writer.CreateAsync(original, token) == suppliedId, "supplied create ID");
        await AssertRowAsync(source, original, token);
        var nullable = Snapshot(false);
        Require(await writer.CreateAsync(nullable, token) == nullable.Id && nullable.Id.Version == 4, "generated create ID");
        await AssertRowAsync(source, nullable, token);

        var replacement = Snapshot(false);
        replacement.Id = original.Id;
        replacement.VirtualKeyId = -99;
        replacement.RequestedAt = Instant.AddDays(-2);
        replacement.ExecutionMode = ExecutionMode.Synchronous;
        replacement.State = ExecutionState.Completed;
        replacement.RetryCount = -3;
        Require(await writer.UpdateAsync(replacement, token) && replacement.Version == 8, "replace snapshot with nulls");
        await AssertRowAsync(source, replacement, token);
        Require(!await writer.UpdateAsync(original, token) && original.Version == 8, "stale miss keeps caller increment");
        await AssertRowAsync(source, replacement, token);

        var first = Snapshot(true);
        var second = Snapshot(true);
        first.Id = second.Id = replacement.Id;
        first.Version = second.Version = 8;
        first.StatusMessage = "first";
        second.StatusMessage = "second";
        var results = await Task.WhenAll(writer.UpdateAsync(first, token), writer.UpdateAsync(second, token));
        Require(results.Count(result => result) == 1 && first.Version == 9 && second.Version == 9, "one CAS winner");
        var winner = results[0] ? first : second;
        await AssertRowAsync(source, winner, token);

        var missing = Snapshot(false);
        Require(!await writer.UpdateAsync(missing, token) && missing.Id.Version == 7 && missing.Version == 8,
            "empty update generates v7 ID and returns missing");
        Require(await ReadAsync(source, missing.Id, token) is null, "missing update never inserts");
        var overflow = Snapshot(true);
        overflow.Version = int.MaxValue;
        await writer.CreateAsync(overflow, token);
        Require(await writer.UpdateAsync(overflow, token) && overflow.Version == int.MinValue, "unchecked version wrap");
        await AssertRowAsync(source, overflow, token);

        var duplicate = Snapshot(true);
        duplicate.Id = winner.Id;
        await ExpectSqlStateAsync(() => writer.CreateAsync(duplicate, token), PostgresErrorCodes.UniqueViolation);
        var invalidForeignKey = Snapshot(false);
        invalidForeignKey.FunctionConfigurationId = int.MinValue;
        await ExpectSqlStateAsync(() => writer.CreateAsync(invalidForeignKey, token), PostgresErrorCodes.ForeignKeyViolation);
        Require(invalidForeignKey.Id.Version == 4 && await ReadAsync(source, invalidForeignKey.Id, token) is null,
            "failed create retains ID without row");
        var invalidJson = Snapshot(true);
        invalidJson.Id = winner.Id;
        invalidJson.Version = winner.Version;
        invalidJson.ResponseJson = "invalid json";
        await ExpectSqlStateAsync(() => writer.UpdateAsync(invalidJson, token), PostgresErrorCodes.InvalidTextRepresentation);
        Require(invalidJson.Version == 10, "failed update keeps increment");
        await AssertRowAsync(source, winner, token);

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var create = Snapshot(false);
            await ExpectCancelledAsync(writer.CreateAsync(create, cancelled.Token), cancelled);
            Require(create.Id.Version == 4 && create.Version == 7 && await ReadAsync(source, create.Id, token) is null,
                "precancelled create mutation without row");
            var update = Snapshot(false);
            await ExpectCancelledAsync(writer.UpdateAsync(update, cancelled.Token), cancelled);
            Require(update.Id.Version == 7 && update.Version == 8 && await ReadAsync(source, update.Id, token) is null,
                "precancelled update mutation without row");
        }

        var cancelledInsertIds = new List<Guid>();
        await using (await source.OpenConnectionAsync(token))
        {
            foreach (var create in new[] { true, false })
            {
                var row = Snapshot(!create);
                if (!create) { row.Id = winner.Id; row.Version = winner.Version; }
                using var cancelled = new CancellationTokenSource();
                Task write = create ? writer.CreateAsync(row, cancelled.Token) : writer.UpdateAsync(row, cancelled.Token);
                try
                {
                    Require(!write.IsCompleted && row.Id != Guid.Empty && row.Version == (create ? 7 : 10), "occupied pool mutation");
                    if (create) cancelledInsertIds.Add(row.Id);
                    cancelled.Cancel();
                    await ExpectCancelledAsync(write, cancelled);
                }
                finally
                {
                    cancelled.Cancel();
                    try { await write.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { /* Expected owned cancellation; every other failure propagates. */ }
                }
            }
        }
        foreach (var id in cancelledInsertIds) Require(await ReadAsync(source, id, token) is null, "pool-cancelled create writes no row");
        await AssertRowAsync(source, winner, token);
        Require(await writer.UpdateAsync(winner, token), "single-slot recovery update");
        await AssertRowAsync(source, winner, token);
        Console.WriteLine("Native function execution scalar writer passed: 24 fields, generated IDs, CAS, nulls, JSONB, failures, cancellation and pool reuse.");
    }

    private static FunctionExecution Snapshot(bool populated) => new()
    {
        Id = populated ? Guid.NewGuid() : Guid.Empty, FunctionConfigurationId = populated ? 11 : 12,
        VirtualKeyId = 42, ExecutionMode = (ExecutionMode)123, State = (ExecutionState)(-9), RequestedAt = Instant,
        StartedAt = populated ? Instant.AddSeconds(1) : null, CompletedAt = populated ? Instant.AddSeconds(3) : null,
        Duration = populated ? TimeSpan.FromTicks(-12345670) : null,
        RequestJson = populated ? """{"quoted":"O'Hare 雪","nested":[1,null,false]}""" : null,
        ResponseJson = populated ? "null" : null, ErrorMessage = populated ? "O'Hare 雪" : null,
        EstimatedCost = populated ? 1234567890.12345678m : null, ActualCost = populated ? -0.00000001m : null,
        CostCalculationDetails = populated ? "[]" : null, RetryCount = 4, NextRetryAt = populated ? Instant.AddDays(1) : null,
        LeasedBy = populated ? "worker 雪" : null, LeaseExpiryTime = populated ? Instant.AddMinutes(5) : null,
        Version = 7, WebhookUrl = populated ? "https://example.test/O'Hare" : null, WebhookDelivered = populated,
        ProgressPercentage = populated ? -1 : null, StatusMessage = populated ? "" : null
    };

    private static async Task<FunctionExecution?> ReadAsync(NpgsqlDataSource source, Guid id, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "FunctionConfigurationId", "VirtualKeyId", "ExecutionMode", "State",
                "RequestedAt", "StartedAt", "CompletedAt", "Duration", "RequestJson", "ResponseJson",
                "ErrorMessage", "EstimatedCost", "ActualCost", "CostCalculationDetails", "RetryCount",
                "NextRetryAt", "LeasedBy", "LeaseExpiryTime", "Version", "WebhookUrl", "WebhookDelivered",
                "ProgressPercentage", "StatusMessage" FROM "FunctionExecutions" WHERE "Id" = @id
            """;
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new FunctionExecution
        {
            Id = reader.GetGuid(0), FunctionConfigurationId = reader.GetInt32(1), VirtualKeyId = reader.GetInt32(2),
            ExecutionMode = (ExecutionMode)reader.GetInt32(3), State = (ExecutionState)reader.GetInt32(4),
            RequestedAt = reader.GetDateTime(5), StartedAt = Date(reader, 6), CompletedAt = Date(reader, 7),
            Duration = reader.IsDBNull(8) ? null : reader.GetTimeSpan(8), RequestJson = Text(reader, 9),
            ResponseJson = Text(reader, 10), ErrorMessage = Text(reader, 11),
            EstimatedCost = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
            ActualCost = reader.IsDBNull(13) ? null : reader.GetDecimal(13), CostCalculationDetails = Text(reader, 14),
            RetryCount = reader.GetInt32(15), NextRetryAt = Date(reader, 16), LeasedBy = Text(reader, 17),
            LeaseExpiryTime = Date(reader, 18), Version = reader.GetInt32(19), WebhookUrl = Text(reader, 20),
            WebhookDelivered = reader.GetBoolean(21), ProgressPercentage = reader.IsDBNull(22) ? null : reader.GetInt32(22),
            StatusMessage = Text(reader, 23)
        };
    }

    private static async Task AssertRowAsync(NpgsqlDataSource source, FunctionExecution expected, CancellationToken token)
    {
        var row = await ReadAsync(source, expected.Id, token) ?? throw new InvalidOperationException("Execution row missing.");
        Require(row.Id == expected.Id && row.FunctionConfigurationId == expected.FunctionConfigurationId &&
            row.VirtualKeyId == expected.VirtualKeyId && row.ExecutionMode == expected.ExecutionMode && row.State == expected.State &&
            row.RequestedAt == expected.RequestedAt && row.RequestedAt.Kind == DateTimeKind.Utc && row.StartedAt == expected.StartedAt &&
            row.CompletedAt == expected.CompletedAt && row.Duration == expected.Duration && SameJson(row.RequestJson, expected.RequestJson) &&
            SameJson(row.ResponseJson, expected.ResponseJson) && row.ErrorMessage == expected.ErrorMessage && row.EstimatedCost == expected.EstimatedCost &&
            row.ActualCost == expected.ActualCost && SameJson(row.CostCalculationDetails, expected.CostCalculationDetails) && row.RetryCount == expected.RetryCount &&
            row.NextRetryAt == expected.NextRetryAt && row.LeasedBy == expected.LeasedBy && row.LeaseExpiryTime == expected.LeaseExpiryTime &&
            row.Version == expected.Version && row.WebhookUrl == expected.WebhookUrl && row.WebhookDelivered == expected.WebhookDelivered &&
            row.ProgressPercentage == expected.ProgressPercentage && row.StatusMessage == expected.StatusMessage, "24 scalar snapshot");
    }

    private static bool SameJson(string? actual, string? expected) =>
        actual is null || expected is null ? actual is null && expected is null : JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected));
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTime? Date(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static async Task ExpectSqlStateAsync(Func<Task> write, string sqlState)
    {
        try { await write(); }
        catch (PostgresException exception) when (exception.SqlState == sqlState) { return; }
        throw new InvalidOperationException($"Expected execution write PostgreSQL error {sqlState}.");
    }

    private static async Task ExpectCancelledAsync(Task write, CancellationTokenSource cancellation)
    {
        try { await write.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        throw new InvalidOperationException("Execution write ignored cancellation.");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Native function execution writer failed: {description}.");
    }
}
