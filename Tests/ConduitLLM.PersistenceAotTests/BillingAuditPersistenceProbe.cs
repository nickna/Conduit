using System.Text.Json;

using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

internal static class BillingAuditPersistenceProbe
{
    private static readonly DateTime Instant = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

    internal static async Task RunAsync(NpgsqlDataSource source, string originalConnectionString)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        Require(new NpgsqlConnectionStringBuilder(source.ConnectionString).MaxPoolSize == 1, "single-slot fixture");
        var writer = new NpgsqlBillingAuditRuntimeWriter(source);
        var populated = Populated();
        var rows = new[]
        {
            populated,
            new BillingAuditRuntimeRecord { Id = Guid.NewGuid(), Timestamp = Instant, EventType = -197 },
            Populated() with
            {
                UsageJson = "null", MetadataJson = "[]", ToolUsageJson = "null", ProviderType = "",
                Model = "", RequestId = "", FailureReason = "", RequestPath = "", IsEstimated = false,
                CalculatedCost = -0.000000005m, ToolUsageCost = 9999.99999999m
            }
        };
        var original = rows.ToArray();
        var committed = await writer.WriteBatchAsync(rows, token);
        Require(committed.Count == rows.Length && rows.SequenceEqual(original), "ordered immutable inputs");
        for (var index = 0; index < rows.Length; index++)
        {
            Require(committed[index] == rows[index], "supplied snapshots retain all 17 fields and 100ns ticks");
            await AssertRowAsync(source, committed[index], token);
        }

        var before = DateTime.UtcNow.AddSeconds(-5);
        var sentinels = new[]
        {
            Populated() with { Id = Guid.Empty, Timestamp = default },
            Populated() with { Id = Guid.Empty },
            Populated() with { Timestamp = default }
        };
        var defaults = await writer.WriteBatchAsync(sentinels, token);
        Require(defaults.Count == sentinels.Length && defaults.Select(row => row.Id).Distinct().Count() == defaults.Count,
            "distinct database generated IDs");
        for (var index = 0; index < sentinels.Length; index++)
        {
            var input = sentinels[index];
            var output = defaults[index];
            Require(output.Id != Guid.Empty && (input.Id == Guid.Empty || output.Id == input.Id), "ID default applies only to sentinel");
            Require(input.Timestamp == default
                ? output.Timestamp.Kind == DateTimeKind.Utc && output.Timestamp >= before && output.Timestamp <= DateTime.UtcNow.AddSeconds(5)
                : output.Timestamp == input.Timestamp, "timestamp default applies only to sentinel");
            Require(output == input with { Id = output.Id, Timestamp = output.Timestamp }, "default generation preserves other 15 fields");
            await AssertRowAsync(source, output, token);
        }
        Require(sentinels[0].Id == Guid.Empty && sentinels[0].Timestamp == default &&
            sentinels[1].Id == Guid.Empty && sentinels[1].Timestamp == Instant &&
            sentinels[2].Id != Guid.Empty && sentinels[2].Timestamp == default, "sentinel inputs remain immutable");

        var late = new[]
        {
            Populated() with { Id = Guid.Empty, Timestamp = default },
            Populated() with { MetadataJson = "invalid json" }
        };
        var fingerprint = await FingerprintAsync(source, token);
        await ExpectSqlStateAsync(() => writer.WriteBatchAsync(late, token), PostgresErrorCodes.InvalidTextRepresentation);
        Require(late[0].Id == Guid.Empty && late[0].Timestamp == default &&
            fingerprint == await FingerprintAsync(source, token), "late failure rolls back defaults and all rows");
        late[1] = late[1] with { MetadataJson = populated.MetadataJson };
        foreach (var row in await writer.WriteBatchAsync(late, token)) await AssertRowAsync(source, row, token);
        Require(late[0].Id == Guid.Empty && late[0].Timestamp == default, "successful retry retains original sentinel input");

        var shutdownRows = Enumerable.Range(0, 129).Select(_ =>
            Populated() with { Id = Guid.Empty, Timestamp = default }).ToArray();
        shutdownRows[^1] = shutdownRows[^1] with { ToolUsageJson = "invalid final row" };
        fingerprint = await FingerprintAsync(source, token);
        await ExpectSqlStateAsync(() => writer.WriteBatchAsync(shutdownRows, token), PostgresErrorCodes.InvalidTextRepresentation);
        Require(fingerprint == await FingerprintAsync(source, token) &&
            shutdownRows.All(row => row.Id == Guid.Empty && row.Timestamp == default), "129-row late failure is atomic");
        shutdownRows[^1] = shutdownRows[^1] with { ToolUsageJson = "null" };
        var shutdownCount = await CountAsync(source, token);
        var shutdownCommitted = await writer.WriteBatchAsync(shutdownRows, token);
        Require(shutdownCommitted.Count == 129 && shutdownCommitted.Select(row => row.Id).Distinct().Count() == 129 &&
            await CountAsync(source, token) == shutdownCount + 129, "129-row retry commits its whole batch once");
        for (var index = 0; index < shutdownRows.Length; index++)
        {
            Require(shutdownRows[index].Id == Guid.Empty && shutdownRows[index].Timestamp == default &&
                shutdownCommitted[index] == shutdownRows[index] with
                    { Id = shutdownCommitted[index].Id, Timestamp = shutdownCommitted[index].Timestamp },
                "shutdown batch preserves ordered immutable records");
            await AssertRowAsync(source, shutdownCommitted[index], token);
        }

        await AssertHundredOrderedDefaultsAsync(source, writer, token);
        await AssertEmptyReturningRollbackAsync(source, writer, token);

        var failures = new (BillingAuditRuntimeRecord Row, string State)[]
        {
            (Populated() with { UsageJson = "{" }, PostgresErrorCodes.InvalidTextRepresentation),
            (Populated() with { MetadataJson = "{" }, PostgresErrorCodes.InvalidTextRepresentation),
            (Populated() with { ToolUsageJson = "{" }, PostgresErrorCodes.InvalidTextRepresentation),
            (Populated() with { CalculatedCost = 10000m }, PostgresErrorCodes.NumericValueOutOfRange),
            (Populated() with { ToolUsageCost = -10000m }, PostgresErrorCodes.NumericValueOutOfRange),
            (Populated() with { VirtualKeyId = int.MinValue }, PostgresErrorCodes.ForeignKeyViolation),
            (Populated() with { Model = new string('m', 101) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { RequestId = new string('r', 101) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { FailureReason = new string('f', 501) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { ProviderType = new string('p', 51) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { RequestPath = new string('x', 257) }, PostgresErrorCodes.StringDataRightTruncation),
            (Populated() with { Id = populated.Id }, PostgresErrorCodes.UniqueViolation)
        };
        fingerprint = await FingerprintAsync(source, token);
        foreach (var (row, state) in failures)
        {
            var first = Populated() with { Id = Guid.Empty, Timestamp = default };
            await ExpectSqlStateAsync(() => writer.WriteBatchAsync([first, row], token), state);
            Require(first.Id == Guid.Empty && first.Timestamp == default &&
                fingerprint == await FingerprintAsync(source, token), "constraint failure leaves every committed row unchanged");
        }
        var unreferencedGroup = Populated() with { VirtualKeyGroupId = int.MinValue };
        await writer.WriteBatchAsync([unreferencedGroup], token);
        await AssertRowAsync(source, unreferencedGroup, token);

        var reconciliation = Populated() with { EventType = 15, RequestId = "native reconciliation 雪" };
        await writer.WriteBatchAsync([reconciliation], token);
        fingerprint = await FingerprintAsync(source, token);
        await ExpectSqlStateAsync(() => writer.WriteBatchAsync(
            [Populated() with { Id = Guid.Empty }, reconciliation with { Id = Guid.NewGuid() }], token), PostgresErrorCodes.UniqueViolation);
        Require(fingerprint == await FingerprintAsync(source, token), "Event15 duplicate request ID rolls back its entire batch");
        var nonUnique = new[]
        {
            reconciliation with { Id = Guid.NewGuid(), EventType = 1 },
            reconciliation with { Id = Guid.NewGuid(), EventType = 1 },
            reconciliation with { Id = Guid.NewGuid(), RequestId = null },
            reconciliation with { Id = Guid.NewGuid(), RequestId = null }
        };
        foreach (var row in await writer.WriteBatchAsync(nonUnique, token)) await AssertRowAsync(source, row, token);

        var cancelledRow = Populated() with { Id = Guid.Empty, Timestamp = default };
        fingerprint = await FingerprintAsync(source, token);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ExpectArgumentAsync(() => writer.WriteBatchAsync(null!, cancellation.Token), nullBatch: true);
            await ExpectArgumentAsync(() => writer.WriteBatchAsync([cancelledRow, null!], cancellation.Token), nullBatch: false);
            await ExpectCancelledAsync(writer.WriteBatchAsync([], cancellation.Token), cancellation);
            await ExpectCancelledAsync(writer.WriteBatchAsync([cancelledRow], cancellation.Token), cancellation);
        }
        Require(fingerprint == await FingerprintAsync(source, token), "validation and precancellation write no rows");
        await using (await source.OpenConnectionAsync(token))
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pending = writer.WriteBatchAsync([cancelledRow], cancellation.Token);
            try
            {
                Require(!pending.IsCompleted, "occupied pool actually blocks nonempty batch");
                Require((await writer.WriteBatchAsync([], token)).Count == 0, "empty batch requires no connection");
                cancellation.Cancel();
                await ExpectCancelledAsync(pending, cancellation);
            }
            finally { await DrainAsync(pending, cancellation); }
        }
        Require(cancelledRow.Id == Guid.Empty && cancelledRow.Timestamp == default &&
            fingerprint == await FingerprintAsync(source, token), "pool cancellation preserves sentinels and database");
        foreach (var row in await writer.WriteBatchAsync([cancelledRow], token)) await AssertRowAsync(source, row, token);

        fingerprint = await FingerprintAsync(source, token);
        // DataSource.ConnectionString sanitizes credentials after opening. The
        // independent lock connections need the original owned-schema string.
        await using (var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(originalConnectionString)
            { Pooling = false, ApplicationName = "native_billing_audit_blocker" }.ConnectionString))
        {
            await blocker.OpenAsync(token);
            await using var transaction = await blocker.BeginTransactionAsync(token);
            await using (var command = blocker.CreateCommand())
            {
                command.CommandText = """LOCK TABLE "BillingAuditEvents" IN ACCESS EXCLUSIVE MODE""";
                await command.ExecuteNonQueryAsync(token);
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pending = writer.WriteBatchAsync([cancelledRow], cancellation.Token);
            try
            {
                await WaitForLockWaitAsync(originalConnectionString, pending, token);
                cancellation.Cancel();
                await ExpectCancelledAsync(pending, cancellation);
            }
            finally { await DrainAsync(pending, cancellation); }
            await transaction.RollbackAsync(token);
        }
        Require(cancelledRow.Id == Guid.Empty && cancelledRow.Timestamp == default &&
            fingerprint == await FingerprintAsync(source, token), "SQL cancellation preserves sentinel input and every stored row");
        var beforeRecovery = await CountAsync(source, token);
        foreach (var row in await writer.WriteBatchAsync([cancelledRow, Populated()], token)) await AssertRowAsync(source, row, token);
        Require(await CountAsync(source, token) == beforeRecovery + 2, "SQL cancellation releases the single slot for atomic reuse");
        Console.WriteLine("Native billing audit writer passed: 17 scalars, ordered immutable pipelined batches, database defaults, empty RETURNING rollback, JSONB/decimal mappings, atomic failures, reconciliation uniqueness, SQL/pool cancellation and reuse.");
    }

    private static async Task AssertHundredOrderedDefaultsAsync(
        NpgsqlDataSource source, NpgsqlBillingAuditRuntimeWriter writer, CancellationToken token)
    {
        var rows = Enumerable.Range(0, 100).Select(index => Populated() with
        {
            Id = Guid.Empty, Timestamp = default, EventType = index - 50,
            RequestId = $"native-pipelined-{index:D3}"
        }).ToArray();
        var originals = rows.ToArray();
        var before = DateTime.UtcNow.AddSeconds(-5);
        var beforeCount = await CountAsync(source, token);
        var committed = await writer.WriteBatchAsync(rows, token);
        Require(committed.Count == 100 && rows.SequenceEqual(originals) &&
            committed.Select(row => row.Id).Distinct().Count() == 100 &&
            committed.Select(row => row.Timestamp).Distinct().Count() == 1 &&
            await CountAsync(source, token) == beforeCount + 100,
            "100-row pipeline retains ordered distinct defaults in one transaction");
        for (var index = 0; index < rows.Length; index++)
        {
            var output = committed[index];
            Require(output.Id != Guid.Empty && output.Timestamp.Kind == DateTimeKind.Utc &&
                output.Timestamp >= before && output.Timestamp <= DateTime.UtcNow.AddSeconds(5) &&
                output == rows[index] with { Id = output.Id, Timestamp = output.Timestamp },
                "100-row pipeline preserves every input ordinal and other scalar");
            await AssertRowAsync(source, output, token);
        }
    }

    private static async Task AssertEmptyReturningRollbackAsync(
        NpgsqlDataSource source, NpgsqlBillingAuditRuntimeWriter writer, CancellationToken token)
    {
        await using (var command = source.CreateCommand("""
            CREATE FUNCTION omit_native_audit_returning_row() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."RequestId" = 'omit-native-returning' THEN RETURN NULL; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER omit_native_audit_returning_row BEFORE INSERT ON "BillingAuditEvents"
                FOR EACH ROW EXECUTE FUNCTION omit_native_audit_returning_row();
            """))
        {
            await command.ExecuteNonQueryAsync(token);
        }
        var rows = Enumerable.Range(0, 3).Select(index => Populated() with
        {
            Id = Guid.Empty, Timestamp = default,
            RequestId = index == 1 ? "omit-native-returning" : $"native-preserved-{index}"
        }).ToArray();
        var originals = rows.ToArray();
        var fingerprint = await FingerprintAsync(source, token);
        try
        {
            await writer.WriteBatchAsync(rows, token);
            throw new InvalidOperationException("Billing audit probe accepted an empty RETURNING row.");
        }
        catch (InvalidOperationException exception) when
            (exception.Message == "Billing audit insert did not return its generated values.") { }
        Require(rows.SequenceEqual(originals) && fingerprint == await FingerprintAsync(source, token),
            "empty RETURNING rolls back earlier and later pipelined rows without changing inputs");
        rows[1] = rows[1] with { RequestId = "native-restored-returning" };
        var beforeCount = await CountAsync(source, token);
        var committed = await writer.WriteBatchAsync(rows, token);
        Require(committed.Count == 3 && committed.Select(row => row.Id).Distinct().Count() == 3 &&
            await CountAsync(source, token) == beforeCount + 3, "empty RETURNING leaves the writer reusable");
        for (var index = 0; index < rows.Length; index++)
        {
            var output = committed[index];
            Require(rows[index].Id == Guid.Empty && rows[index].Timestamp == default &&
                output == rows[index] with { Id = output.Id, Timestamp = output.Timestamp },
                "empty RETURNING recovery preserves ordered immutable snapshots");
            await AssertRowAsync(source, output, token);
        }
        await using var cleanup = source.CreateCommand("""
            DROP TRIGGER omit_native_audit_returning_row ON "BillingAuditEvents";
            DROP FUNCTION omit_native_audit_returning_row();
            """);
        await cleanup.ExecuteNonQueryAsync(token);
    }

    private static BillingAuditRuntimeRecord Populated() => new()
    {
        Id = Guid.NewGuid(), Timestamp = Instant, EventType = 197, VirtualKeyId = 41, VirtualKeyGroupId = 7,
        Model = "O'Hare 雪", RequestId = Guid.NewGuid().ToString("D"),
        UsageJson = """{"quoted":"café 雪 🔎","nested":[1,null,false]}""", CalculatedCost = 0.125000005m,
        FailureReason = "diagnostic O'Hare 雪", ProviderType = "future-provider 雪", HttpStatusCode = -197,
        MetadataJson = """{"missing":null,"value":true}""", RequestPath = "/v1/雪'O", IsEstimated = true,
        ToolUsageJson = """[{"name":"雪","count":2}]""", ToolUsageCost = -0.000000005m
    };

    private static async Task AssertRowAsync(NpgsqlDataSource source, BillingAuditRuntimeRecord expected, CancellationToken token)
    {
        var row = await ReadAsync(source, expected.Id, token) ?? throw new InvalidOperationException("Billing audit row missing.");
        Require(row.Id == expected.Id && row.Timestamp.Ticks == expected.Timestamp.Ticks / 10 * 10 &&
            row.Timestamp.Kind == DateTimeKind.Utc && row.EventType == expected.EventType &&
            row.VirtualKeyId == expected.VirtualKeyId && row.VirtualKeyGroupId == expected.VirtualKeyGroupId &&
            row.Model == expected.Model && row.RequestId == expected.RequestId && SameJson(row.UsageJson, expected.UsageJson) &&
            row.CalculatedCost == Rounded(expected.CalculatedCost) && row.FailureReason == expected.FailureReason &&
            row.ProviderType == expected.ProviderType && row.HttpStatusCode == expected.HttpStatusCode &&
            SameJson(row.MetadataJson, expected.MetadataJson) && row.RequestPath == expected.RequestPath &&
            row.IsEstimated == expected.IsEstimated && SameJson(row.ToolUsageJson, expected.ToolUsageJson) &&
            row.ToolUsageCost == Rounded(expected.ToolUsageCost), "independent 17-column PostgreSQL oracle");
    }

    private static async Task<BillingAuditRuntimeRecord?> ReadAsync(NpgsqlDataSource source, Guid id, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "Timestamp", "EventType", "VirtualKeyId", "VirtualKeyGroupId", "Model", "RequestId",
                "UsageJson", "CalculatedCost", "FailureReason", "ProviderType", "HttpStatusCode",
                "MetadataJson", "RequestPath", "IsEstimated", "ToolUsageJson", "ToolUsageCost"
            FROM "BillingAuditEvents" WHERE "Id" = @id
            """;
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var row = new BillingAuditRuntimeRecord
        {
            Id = reader.GetGuid(0), Timestamp = reader.GetDateTime(1), EventType = reader.GetInt32(2),
            VirtualKeyId = Number(reader, 3), VirtualKeyGroupId = Number(reader, 4), Model = Text(reader, 5),
            RequestId = Text(reader, 6), UsageJson = Text(reader, 7), CalculatedCost = Money(reader, 8),
            FailureReason = Text(reader, 9), ProviderType = Text(reader, 10), HttpStatusCode = Number(reader, 11),
            MetadataJson = Text(reader, 12), RequestPath = Text(reader, 13), IsEstimated = reader.GetBoolean(14),
            ToolUsageJson = Text(reader, 15), ToolUsageCost = Money(reader, 16)
        };
        Require(!await reader.ReadAsync(token), "primary key read has one row");
        return row;
    }

    private static async Task<string> FingerprintAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT md5(COALESCE(jsonb_agg(to_jsonb(row) ORDER BY row."Id")::text, '[]'))
            FROM "BillingAuditEvents" row
            """;
        return (string)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT count(*) FROM "BillingAuditEvents" """;
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task WaitForLockWaitAsync(string connectionString, Task pending, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = "native_billing_audit_observer" }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM pg_locks WHERE relation = '"BillingAuditEvents"'::regclass AND NOT granted)
            """;
        while (!(bool)(await command.ExecuteScalarAsync(token))!)
        {
            Require(!pending.IsCompleted, "SQL write waits behind the owned table lock");
            await Task.Delay(20, token);
        }
    }

    private static decimal? Rounded(decimal? value) =>
        value is null ? null : decimal.Round(value.Value, 8, MidpointRounding.AwayFromZero);
    private static bool SameJson(string? left, string? right)
    {
        if (left is null || right is null) return left is null && right is null;
        using var first = JsonDocument.Parse(left);
        using var second = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(first.RootElement, second.RootElement);
    }
    private static int? Number(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static decimal? Money(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task ExpectSqlStateAsync(Func<Task> write, string state)
    {
        try { await write(); }
        catch (PostgresException exception) when (exception.SqlState == state) { return; }
        throw new InvalidOperationException("Native billing audit writer did not reject the expected PostgreSQL constraint.");
    }

    private static async Task ExpectArgumentAsync(Func<Task> write, bool nullBatch)
    {
        try { await write(); }
        catch (ArgumentNullException exception) when (nullBatch && exception.ParamName == "records") { return; }
        catch (ArgumentException exception) when (!nullBatch && exception.GetType() == typeof(ArgumentException) && exception.ParamName == "records") { return; }
        throw new InvalidOperationException("Native billing audit writer did not validate its entire input batch.");
    }

    private static async Task ExpectCancelledAsync(Task write, CancellationTokenSource cancellation)
    {
        try { await write.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        throw new InvalidOperationException("Native billing audit writer ignored cancellation.");
    }

    private static async Task DrainAsync(Task write, CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        try { await write.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { /* Expected owned cancellation; every other failure propagates. */ }
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Native billing audit writer failed: " + description + ".");
    }
}
