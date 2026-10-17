using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;
using NpgsqlTypes;

internal static class AsyncTaskGuardPersistenceProbe
{
    private const string Worker = "guard-worker";
    private const string UnknownOutcome = "Provider outcome is unknown after the processing lease expired.";
    private static readonly DateTime Past = new(2000, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Future = new(2100, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private const string Columns = """
        "Id","Type","State","Payload","Progress","ProgressMessage","Result","Error",
        "CreatedAt","UpdatedAt","CompletedAt","VirtualKeyId","Metadata","IsArchived","ArchivedAt",
        "LeasedBy","LeaseExpiryTime","ProviderInvocationStartedAt","ProviderInvocationCompletedAt",
        "ProviderOperationId","RetryDispatchId","Version","RetryCount","MaxRetries","IsRetryable","NextRetryAt"
        """;

    internal static async Task RunAsync(string connectionString)
    {
        var schema = PersistenceProbeLifecycle.SchemaPrefix + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = schema, ApplicationName = "native_task_guard_" + Guid.NewGuid().ToString("N"),
            Pooling = true, MinPoolSize = 0, MaxPoolSize = 2, Multiplexing = false,
            LogParameters = false, Timeout = 20, CommandTimeout = 20, CancellationTimeout = 1000
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        NpgsqlDataSource? source = null;
        Exception? primary = null;
        var failures = new List<Exception>();
        try
        {
            await CreateSchemaAsync(settings.ConnectionString, schema, deadline.Token);
            source = NpgsqlDataSource.Create(settings.ConnectionString);
            var store = new NpgsqlAsyncTaskRuntimeStore(source);
            string[] regressions =
            [
                "start-archived", "start-expired", "start-repeated", "complete-before-start",
                "extend-archived", "extend-terminal", "claim-pending-evidence",
                "claim-expired-completion-only", "claim-archived-evidence"
            ];
            foreach (var scenario in regressions)
                await AssertRegressionAsync(settings.ConnectionString, schema, store, scenario, deadline.Token);
            await AssertClaimsAsync(settings.ConnectionString, schema, store, deadline.Token);
            await AssertProviderOrderingAsync(settings.ConnectionString, schema, store, deadline.Token);
            await AssertExtensionAsync(settings.ConnectionString, schema, store, deadline.Token);
            await AssertConcurrentAsync(settings.ConnectionString, schema, store, false, deadline.Token);
            await AssertConcurrentAsync(settings.ConnectionString, schema, store, true, deadline.Token);
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            if (source is not null)
                await AttemptAsync(_ => source.DisposeAsync().AsTask(), failures);
            // The lifecycle owns its independent 15-second bound; fully join it
            // rather than abandoning a still-running drop after a shorter wait.
            try { await PersistenceProbeLifecycle.CleanupOwnedSchemaAsync(connectionString, schema); }
            catch (Exception exception) { failures.Add(exception); }
            await AttemptAsync(token => AssertAbsentAsync(settings.ConnectionString, schema, token), failures);
        }
        Rethrow(primary, failures);
        Console.WriteLine("Native async-task guards passed: regressions=9; transitionGroups=3; concurrentCases=2; durableFields=26; all before/after fields, archive/lease/provider ordering, operation IDs and versions; owned source and schema removed.");
    }

    private static async Task AssertRegressionAsync(string cs, string schema,
        NpgsqlAsyncTaskRuntimeStore store, string scenario, CancellationToken token)
    {
        var seed = Seed(scenario);
        await InsertAsync(cs, schema, seed, token);
        var before = await ReadAsync(cs, schema, seed.Id, token);
        var began = Wire(DateTime.UtcNow);
        var result = scenario switch
        {
            "start-archived" or "start-expired" or "start-repeated" =>
                (await store.MarkProviderInvocationStartedAsync(seed.Id, Worker, token)).ToString(),
            "complete-before-start" =>
                (await store.MarkProviderInvocationCompletedAsync(seed.Id, Worker, "new-operation", token)).ToString(),
            "extend-archived" or "extend-terminal" =>
                (await store.ExtendLeaseAsync(seed.Id, Worker, TimeSpan.FromMinutes(3), token)).ToString(),
            _ => (await store.TryClaimTaskAsync(seed.Id, "new-worker", TimeSpan.FromMinutes(2), token)).ToString()
        };
        var ended = DateTime.UtcNow;
        var after = await ReadAsync(cs, schema, seed.Id, token);
        var expected = before;
        var expectedResult = bool.FalseString;
        if (scenario is "claim-pending-evidence" or "claim-expired-completion-only")
        {
            expectedResult = nameof(AsyncTaskRuntimeClaimStatus.Indeterminate);
            Timestamp(after.UpdatedAt, began, ended);
            Require(after.CompletedAt == after.UpdatedAt, "uncertainty uses one captured timestamp");
            expected = before with
            {
                State = 6, IsRetryable = false, Error = UnknownOutcome, LeasedBy = null,
                LeaseExpiryTime = null, CompletedAt = after.UpdatedAt,
                UpdatedAt = after.UpdatedAt, Version = before.Version + 1
            };
        }
        else if (scenario == "claim-archived-evidence")
            expectedResult = nameof(AsyncTaskRuntimeClaimStatus.AlreadyClaimed);
        Witness(scenario, result, before, after);
        Require(result == expectedResult && after == expected, "nine-case complete durable guard contract");
    }

    private static async Task AssertClaimsAsync(string cs, string schema,
        NpgsqlAsyncTaskRuntimeStore store, CancellationToken token)
    {
        foreach (var scenario in new[] { "clean-pending", "clean-expired" })
        {
            var seed = Seed(scenario) with
            {
                State = scenario == "clean-pending" ? 0 : 1,
                LeaseExpiryTime = scenario == "clean-pending" ? Future : Past
            };
            await InsertAsync(cs, schema, seed, token);
            var before = await ReadAsync(cs, schema, seed.Id, token);
            var began = Wire(DateTime.UtcNow);
            var result = await store.TryClaimTaskAsync(seed.Id, "claim-winner", TimeSpan.FromMinutes(2), token);
            var ended = DateTime.UtcNow;
            var after = await ReadAsync(cs, schema, seed.Id, token);
            Timestamp(after.UpdatedAt, began, ended);
            var expected = before with
            {
                State = 1, LeasedBy = "claim-winner", UpdatedAt = after.UpdatedAt,
                LeaseExpiryTime = after.UpdatedAt.AddMinutes(2), Version = before.Version + 1
            };
            Witness(scenario, result.ToString(), before, after);
            Require(result == AsyncTaskRuntimeClaimStatus.Claimed && after == expected, "clean claim complete row");
        }
        foreach (var state in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var scenario = "claim-fallback-" + state;
            var seed = Seed(scenario) with
            {
                State = state, LeaseExpiryTime = null, IsArchived = state == 6,
                ArchivedAt = state == 6 ? Past : null,
                ProviderInvocationStartedAt = state == 6 ? Past : null
            };
            await InsertAsync(cs, schema, seed, token);
            var before = await ReadAsync(cs, schema, seed.Id, token);
            var result = await store.TryClaimTaskAsync(seed.Id, Worker, TimeSpan.FromMinutes(2), token);
            var after = await ReadAsync(cs, schema, seed.Id, token);
            var expectedResult = state == 1 ? AsyncTaskRuntimeClaimStatus.AlreadyClaimed
                : state == 6 ? AsyncTaskRuntimeClaimStatus.Indeterminate : AsyncTaskRuntimeClaimStatus.Terminal;
            Witness(scenario, result.ToString(), before, after);
            Require(result == expectedResult && before == after, "fallback does not alter a durable field");
        }
        Require(await store.TryClaimTaskAsync("guard-missing", Worker, TimeSpan.FromMinutes(2), token)
            == AsyncTaskRuntimeClaimStatus.Missing, "missing claim fallback");
    }

    private static async Task AssertProviderOrderingAsync(string cs, string schema,
        NpgsqlAsyncTaskRuntimeStore store, CancellationToken token)
    {
        var seed = Seed("ordered-provider");
        await InsertAsync(cs, schema, seed, token);
        var before = await ReadAsync(cs, schema, seed.Id, token);
        var began = Wire(DateTime.UtcNow);
        var result = await store.MarkProviderInvocationStartedAsync(seed.Id, Worker, token);
        var ended = DateTime.UtcNow;
        var after = await ReadAsync(cs, schema, seed.Id, token);
        Timestamp(after.UpdatedAt, began, ended);
        Witness("valid-start", result.ToString(), before, after);
        Require(result && after == (before with
        {
            ProviderInvocationStartedAt = after.UpdatedAt, UpdatedAt = after.UpdatedAt
        }), "valid start changes only start and updated, not version");
        before = after;
        result = await store.MarkProviderInvocationStartedAsync(seed.Id, Worker, token);
        after = await ReadAsync(cs, schema, seed.Id, token);
        Witness("valid-start-repeated", result.ToString(), before, after);
        Require(!result && after == before, "a repeated start cannot overwrite evidence");
        foreach (var operation in new string?[] { null, null, string.Empty, "replacement-operation" })
        {
            before = after;
            began = Wire(DateTime.UtcNow);
            result = await store.MarkProviderInvocationCompletedAsync(seed.Id, Worker, operation, token);
            ended = DateTime.UtcNow;
            after = await ReadAsync(cs, schema, seed.Id, token);
            Timestamp(after.UpdatedAt, began, ended);
            Require(after.UpdatedAt >= before.UpdatedAt, "ordered phase timestamps");
            Witness(operation is null ? "valid-complete-null" : operation.Length == 0
                ? "valid-complete-empty" : "valid-complete-replace", result.ToString(), before, after);
            Require(result && after == (before with
            {
                ProviderInvocationCompletedAt = after.UpdatedAt, UpdatedAt = after.UpdatedAt,
                ProviderOperationId = operation ?? before.ProviderOperationId
            }), "completion may repeat, null retains, empty replaces, and version is unchanged");
        }
        foreach (var rejection in new[] { "archived", "expired", "null-lease", "terminal", "wrong-owner" })
        {
            var scenario = "complete-rejected-" + rejection;
            var rejected = Seed(scenario) with { ProviderInvocationStartedAt = Past.AddYears(4) };
            rejected = rejection switch
            {
                "archived" => rejected with { IsArchived = true, ArchivedAt = Past.AddYears(3) },
                "expired" => rejected with { LeaseExpiryTime = Past },
                "null-lease" => rejected with { LeaseExpiryTime = null },
                "terminal" => rejected with { State = 2, CompletedAt = Past.AddYears(6) },
                _ => rejected with { LeasedBy = "different-worker" }
            };
            await InsertAsync(cs, schema, rejected, token);
            before = await ReadAsync(cs, schema, rejected.Id, token);
            result = await store.MarkProviderInvocationCompletedAsync(rejected.Id, Worker, "new-operation", token);
            after = await ReadAsync(cs, schema, rejected.Id, token);
            Witness(scenario, result.ToString(), before, after);
            Require(!result && after == before, "completion rejects archived, expired/null lease, terminal and foreign-owner rows");
        }
    }

    private static async Task AssertExtensionAsync(string cs, string schema,
        NpgsqlAsyncTaskRuntimeStore store, CancellationToken token)
    {
        var seed = Seed("live-extension");
        await InsertAsync(cs, schema, seed, token);
        var before = await ReadAsync(cs, schema, seed.Id, token);
        var began = Wire(DateTime.UtcNow);
        var result = await store.ExtendLeaseAsync(seed.Id, Worker, TimeSpan.FromMinutes(3), token);
        var ended = DateTime.UtcNow;
        var after = await ReadAsync(cs, schema, seed.Id, token);
        Timestamp(after.UpdatedAt, began, ended);
        Witness("live-extension", result.ToString(), before, after);
        Require(result && after == (before with
        {
            LeaseExpiryTime = after.UpdatedAt.AddMinutes(3), UpdatedAt = after.UpdatedAt,
            Version = before.Version + 1
        }), "extension is now plus duration, with exactly one version increment");
    }

    private static async Task AssertConcurrentAsync(string cs, string schema,
        NpgsqlAsyncTaskRuntimeStore store, bool start, CancellationToken token)
    {
        var scenario = start ? "concurrent-start" : "concurrent-claim";
        var seed = Seed(scenario) with { State = start ? 1 : 0 };
        await InsertAsync(cs, schema, seed, token);
        var before = await ReadAsync(cs, schema, seed.Id, token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        NpgsqlConnection? blocker = null;
        NpgsqlTransaction? transaction = null;
        var lockReleased = false;
        Task<string>? first = null;
        Task<string>? second = null;
        Exception? primary = null;
        var failures = new List<Exception>();
        try
        {
            blocker = new NpgsqlConnection(Unpooled(cs));
            await blocker.OpenAsync(token);
            transaction = await blocker.BeginTransactionAsync(token);
            await using (var command = new NpgsqlCommand($"LOCK TABLE \"{schema}\".\"AsyncTasks\" IN ACCESS EXCLUSIVE MODE", blocker, transaction))
                await command.ExecuteNonQueryAsync(token);
            var began = Wire(DateTime.UtcNow);
            first = Task.Run(() => InvokeAsync("claim-a"), caller.Token);
            second = Task.Run(() => InvokeAsync("claim-b"), caller.Token);
            await WaitForTwoWaitersAsync(cs, schema, blocker.ProcessID, token);
            await transaction.RollbackAsync(token);
            lockReleased = true;
            var results = await Task.WhenAll(first, second).WaitAsync(token);
            var ended = DateTime.UtcNow;
            var after = await ReadAsync(cs, schema, seed.Id, token);
            Timestamp(after.UpdatedAt, began, ended);
            if (start)
            {
                Witness(scenario, string.Join(',', results), before, after);
                Require(results.Count(value => value == bool.TrueString) == 1 &&
                    results.Count(value => value == bool.FalseString) == 1 && after == (before with
                    { ProviderInvocationStartedAt = after.UpdatedAt, UpdatedAt = after.UpdatedAt }),
                    "two observed concurrent starts have one winner and no version change");
            }
            else
            {
                var winner = results[0] == nameof(AsyncTaskRuntimeClaimStatus.Claimed) ? "claim-a" : "claim-b";
                Witness(scenario, string.Join(',', results), before, after);
                Require(results.Count(value => value == nameof(AsyncTaskRuntimeClaimStatus.Claimed)) == 1 &&
                    results.Count(value => value == nameof(AsyncTaskRuntimeClaimStatus.AlreadyClaimed)) == 1 && after == (before with
                    {
                        State = 1, LeasedBy = winner, LeaseExpiryTime = after.UpdatedAt.AddMinutes(2),
                        UpdatedAt = after.UpdatedAt, Version = before.Version + 1
                    }), "two observed concurrent claims have one winner and one version increment");
            }
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            caller.Cancel();
            await AttemptAsync(cleanup => DrainAsync(first, second, cleanup), failures);
            if (transaction is not null && !lockReleased)
                await AttemptAsync(cleanup => transaction.RollbackAsync(cleanup), failures);
            if (blocker is not null)
                await AttemptAsync(_ => blocker.DisposeAsync().AsTask(), failures);
            await AttemptAsync(cleanup => DrainAsync(first, second, cleanup), failures);
            if (transaction is not null)
                await AttemptAsync(_ => transaction.DisposeAsync().AsTask(), failures);
        }
        Rethrow(primary, failures);
        async Task<string> InvokeAsync(string worker) => start
            ? (await store.MarkProviderInvocationStartedAsync(seed.Id, Worker, caller.Token)).ToString()
            : (await store.TryClaimTaskAsync(seed.Id, worker, TimeSpan.FromMinutes(2), caller.Token)).ToString();
    }

    private static async Task WaitForTwoWaitersAsync(string cs, string schema, int blocker, CancellationToken token)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
        observation.CancelAfter(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(Unpooled(cs));
        await connection.OpenAsync(observation.Token);
        await using var command = new NpgsqlCommand("""
            SELECT count(DISTINCT a.pid)::integer FROM pg_stat_activity a
            JOIN pg_locks l ON l.pid=a.pid
            JOIN pg_class c ON c.oid=l.relation JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE a.datname=current_database() AND a.application_name=@application AND a.state='active'
              AND n.nspname=@schema AND c.relname='AsyncTasks' AND l.mode='RowExclusiveLock' AND NOT l.granted
              AND @blocker=ANY(pg_blocking_pids(a.pid))
              AND EXISTS (SELECT 1 FROM pg_locks b WHERE b.pid=@blocker AND b.relation=l.relation
                          AND b.mode='AccessExclusiveLock' AND b.granted)
            """, connection);
        command.Parameters.AddWithValue("application", new NpgsqlConnectionStringBuilder(cs).ApplicationName!);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("blocker", blocker);
        while (await command.ExecuteScalarAsync(observation.Token) is not 2)
            await Task.Delay(10, observation.Token);
    }

    private static Row Seed(string scenario)
    {
        var row = new Row
        {
            Id = "guard-" + scenario, Type = "image_generation", State = 1,
            Payload = "{\"prompt\":\"owned guard input\"}", Progress = 37, ProgressMessage = "preserve progress 雪",
            Result = "{\"owned\":true}", Error = "preserve original error", CreatedAt = Past.AddYears(1),
            UpdatedAt = Past.AddYears(2), VirtualKeyId = 137, Metadata = "{\"source\":\"guard fixture\"}",
            LeasedBy = Worker, LeaseExpiryTime = Future, ProviderOperationId = "original-operation",
            RetryDispatchId = "original-dispatch", Version = 11, RetryCount = 2, MaxRetries = 9,
            IsRetryable = true, NextRetryAt = Future.AddDays(1)
        };
        if (scenario is "start-archived" or "extend-archived" or "claim-archived-evidence")
            row = row with { IsArchived = true, ArchivedAt = Past.AddYears(3) };
        if (scenario is "start-expired" or "claim-expired-completion-only" or "claim-archived-evidence")
            row = row with { LeaseExpiryTime = Past };
        if (scenario is "start-repeated" or "claim-pending-evidence" or "claim-archived-evidence")
            row = row with { ProviderInvocationStartedAt = Past.AddYears(4) };
        if (scenario == "claim-expired-completion-only")
            row = row with { ProviderInvocationCompletedAt = Past.AddYears(5) };
        if (scenario == "claim-pending-evidence") row = row with { State = 0 };
        if (scenario == "extend-terminal") row = row with { State = 2, CompletedAt = Past.AddYears(6) };
        return row;
    }

    private static async Task CreateSchemaAsync(string cs, string schema, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(Unpooled(cs));
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand($"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."AsyncTasks" (
                "Id" varchar(50) PRIMARY KEY, "Type" varchar(100) NOT NULL, "State" integer NOT NULL,
                "Payload" text, "Progress" integer NOT NULL, "ProgressMessage" varchar(500),
                "Result" text, "Error" text, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL,
                "CompletedAt" timestamptz, "VirtualKeyId" integer NOT NULL, "Metadata" text,
                "IsArchived" boolean NOT NULL, "ArchivedAt" timestamptz, "LeasedBy" varchar(100),
                "LeaseExpiryTime" timestamptz, "ProviderInvocationStartedAt" timestamptz,
                "ProviderInvocationCompletedAt" timestamptz, "ProviderOperationId" varchar(200),
                "RetryDispatchId" varchar(64), "Version" integer NOT NULL, "RetryCount" integer NOT NULL,
                "MaxRetries" integer NOT NULL, "IsRetryable" boolean NOT NULL, "NextRetryAt" timestamptz);
            """, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertAsync(string cs, string schema, Row row, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(Unpooled(cs));
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{schema}"."AsyncTasks" ({Columns}) VALUES (
                @id,@type,@state,@payload,@progress,@message,@result,@error,@created,@updated,@completed,
                @key,@metadata,@archived,@archivedAt,@owner,@expiry,@started,@providerCompleted,
                @operation,@dispatch,@version,@retry,@max,@retryable,@next)
            """, connection);
        (string Name, NpgsqlDbType Type, object? Value)[] values =
        [
            ("id", NpgsqlDbType.Varchar, row.Id), ("type", NpgsqlDbType.Varchar, row.Type),
            ("state", NpgsqlDbType.Integer, row.State), ("payload", NpgsqlDbType.Text, row.Payload),
            ("progress", NpgsqlDbType.Integer, row.Progress), ("message", NpgsqlDbType.Varchar, row.ProgressMessage),
            ("result", NpgsqlDbType.Text, row.Result), ("error", NpgsqlDbType.Text, row.Error),
            ("created", NpgsqlDbType.TimestampTz, row.CreatedAt), ("updated", NpgsqlDbType.TimestampTz, row.UpdatedAt),
            ("completed", NpgsqlDbType.TimestampTz, row.CompletedAt), ("key", NpgsqlDbType.Integer, row.VirtualKeyId),
            ("metadata", NpgsqlDbType.Text, row.Metadata), ("archived", NpgsqlDbType.Boolean, row.IsArchived),
            ("archivedAt", NpgsqlDbType.TimestampTz, row.ArchivedAt), ("owner", NpgsqlDbType.Varchar, row.LeasedBy),
            ("expiry", NpgsqlDbType.TimestampTz, row.LeaseExpiryTime), ("started", NpgsqlDbType.TimestampTz, row.ProviderInvocationStartedAt),
            ("providerCompleted", NpgsqlDbType.TimestampTz, row.ProviderInvocationCompletedAt),
            ("operation", NpgsqlDbType.Varchar, row.ProviderOperationId), ("dispatch", NpgsqlDbType.Varchar, row.RetryDispatchId),
            ("version", NpgsqlDbType.Integer, row.Version), ("retry", NpgsqlDbType.Integer, row.RetryCount),
            ("max", NpgsqlDbType.Integer, row.MaxRetries), ("retryable", NpgsqlDbType.Boolean, row.IsRetryable),
            ("next", NpgsqlDbType.TimestampTz, row.NextRetryAt)
        ];
        Require(values.Length == 26, "all durable seed parameters");
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter(value.Name, value.Type) { Value = value.Value ?? DBNull.Value });
        Require(await command.ExecuteNonQueryAsync(token) == 1, "one independently seeded row");
    }

    private static async Task<Row> ReadAsync(string cs, string schema, string id, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(Unpooled(cs));
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM \"{schema}\".\"AsyncTasks\" WHERE \"Id\"=@id", connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, id);
        await using var reader = await command.ExecuteReaderAsync(token);
        Require(reader.FieldCount == 26 && await reader.ReadAsync(token), "independent complete durable row");
        var row = new Row
        {
            Id = reader.GetString(0), Type = reader.GetString(1), State = reader.GetInt32(2),
            Payload = Text(3), Progress = reader.GetInt32(4), ProgressMessage = Text(5), Result = Text(6), Error = Text(7),
            CreatedAt = Time(8)!.Value, UpdatedAt = Time(9)!.Value, CompletedAt = Time(10), VirtualKeyId = reader.GetInt32(11),
            Metadata = Text(12), IsArchived = reader.GetBoolean(13), ArchivedAt = Time(14), LeasedBy = Text(15),
            LeaseExpiryTime = Time(16), ProviderInvocationStartedAt = Time(17), ProviderInvocationCompletedAt = Time(18),
            ProviderOperationId = Text(19), RetryDispatchId = Text(20), Version = reader.GetInt32(21),
            RetryCount = reader.GetInt32(22), MaxRetries = reader.GetInt32(23), IsRetryable = reader.GetBoolean(24), NextRetryAt = Time(25)
        };
        Require(!await reader.ReadAsync(token), "exactly one independent row");
        return row;
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        DateTime? Time(int index)
        {
            if (reader.IsDBNull(index)) return null;
            var value = reader.GetDateTime(index);
            Require(value.Kind == DateTimeKind.Utc, "all persisted timestamps are UTC");
            return value;
        }
    }

    private static void Witness(string scenario, string result, Row before, Row after)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("scenario", scenario); writer.WriteString("result", result);
            writer.WriteNumber("fieldCount", 26); writer.WritePropertyName("before"); WriteRow(writer, before);
            writer.WritePropertyName("after"); WriteRow(writer, after); writer.WriteEndObject();
        }
        Console.WriteLine("ASYNC_TASK_GUARD_WITNESS=" + Encoding.UTF8.GetString(stream.ToArray()));
    }

    // Explicit serialization and immutable record equality keep every field visible
    // to the native compiler without reflection or a second runtime serializer graph.
    private static void WriteRow(Utf8JsonWriter writer, Row row)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", row.Id); writer.WriteString("Type", row.Type); writer.WriteNumber("State", row.State);
        writer.WriteString("Payload", row.Payload); writer.WriteNumber("Progress", row.Progress); writer.WriteString("ProgressMessage", row.ProgressMessage);
        writer.WriteString("Result", row.Result); writer.WriteString("Error", row.Error); writer.WriteString("CreatedAt", row.CreatedAt);
        writer.WriteString("UpdatedAt", row.UpdatedAt); Time("CompletedAt", row.CompletedAt); writer.WriteNumber("VirtualKeyId", row.VirtualKeyId);
        writer.WriteString("Metadata", row.Metadata); writer.WriteBoolean("IsArchived", row.IsArchived); Time("ArchivedAt", row.ArchivedAt);
        writer.WriteString("LeasedBy", row.LeasedBy); Time("LeaseExpiryTime", row.LeaseExpiryTime); Time("ProviderInvocationStartedAt", row.ProviderInvocationStartedAt);
        Time("ProviderInvocationCompletedAt", row.ProviderInvocationCompletedAt); writer.WriteString("ProviderOperationId", row.ProviderOperationId);
        writer.WriteString("RetryDispatchId", row.RetryDispatchId); writer.WriteNumber("Version", row.Version); writer.WriteNumber("RetryCount", row.RetryCount);
        writer.WriteNumber("MaxRetries", row.MaxRetries); writer.WriteBoolean("IsRetryable", row.IsRetryable); Time("NextRetryAt", row.NextRetryAt);
        writer.WriteEndObject();
        void Time(string name, DateTime? value) { if (value is { } instant) writer.WriteString(name, instant); else writer.WriteNull(name); }
    }

    private static async Task AssertAbsentAsync(string cs, string schema, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(Unpooled(cs));
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT to_regnamespace(@schema) IS NULL AND NOT EXISTS
                (SELECT 1 FROM pg_stat_activity WHERE application_name=@application)
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("application", new NpgsqlConnectionStringBuilder(cs).ApplicationName!);
        while (await command.ExecuteScalarAsync(token) is not true)
            await Task.Delay(10, token);
    }

    private static string Unpooled(string cs) => new NpgsqlConnectionStringBuilder(cs)
    {
        Pooling = false, ApplicationName = "native_task_guard_oracle", Timeout = 5,
        CommandTimeout = 10, CancellationTimeout = 1000
    }.ConnectionString;
    private static DateTime Wire(DateTime value) => new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);
    private static void Timestamp(DateTime value, DateTime began, DateTime ended) =>
        Require(value.Kind == DateTimeKind.Utc && value >= began && value <= ended, "captured client UTC timestamp bracket");
    private static async Task DrainAsync(Task? first, Task? second, CancellationToken token)
    {
        var errors = new List<Exception>();
        foreach (var task in new[] { first, second })
        {
            if (task is null) continue;
            try { await task.WaitAsync(token); }
            catch (OperationCanceledException) when (task.IsCanceled) { }
            catch (Exception exception) { errors.Add(exception); }
        }
        if (errors.Count > 0) throw new AggregateException("Owned contenders did not both drain.", errors);
    }
    private static async Task AttemptAsync(Func<CancellationToken, Task> action, List<Exception> failures)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await action(cleanup.Token).WaitAsync(cleanup.Token); }
        catch (Exception exception) { failures.Add(exception); }
    }
    private static void Rethrow(Exception? primary, List<Exception> failures)
    {
        if (primary is not null && failures.Count > 0)
            throw new AggregateException("Async-task guard work and independent cleanup failed.", new[] { primary }.Concat(failures));
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (failures.Count > 0) throw new AggregateException("Async-task guard independent cleanup failed.", failures);
    }
    private static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException("Native async-task guard contract failed: " + contract);
    }

    private sealed record Row
    {
        public string Id { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public int State { get; init; }
        public string? Payload { get; init; }
        public int Progress { get; init; }
        public string? ProgressMessage { get; init; }
        public string? Result { get; init; }
        public string? Error { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public int VirtualKeyId { get; init; }
        public string? Metadata { get; init; }
        public bool IsArchived { get; init; }
        public DateTime? ArchivedAt { get; init; }
        public string? LeasedBy { get; init; }
        public DateTime? LeaseExpiryTime { get; init; }
        public DateTime? ProviderInvocationStartedAt { get; init; }
        public DateTime? ProviderInvocationCompletedAt { get; init; }
        public string? ProviderOperationId { get; init; }
        public string? RetryDispatchId { get; init; }
        public int Version { get; init; }
        public int RetryCount { get; init; }
        public int MaxRetries { get; init; }
        public bool IsRetryable { get; init; }
        public DateTime? NextRetryAt { get; init; }
    }
}
