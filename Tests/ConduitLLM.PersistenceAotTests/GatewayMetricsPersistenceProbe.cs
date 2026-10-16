using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.Extensions.Logging;

using Npgsql;

internal static class GatewayMetricsPersistenceProbe
{
    private const string SchemaResource = "ConduitLLM.PersistenceAotTests.GatewayMetricsSchema.sql";
    private static readonly DateTime Instant = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
    private static DateTime From => Instant.AddMinutes(-5);
    private enum Read { Usage, Cost, Active, Queue, Generation, Spend }
    private sealed record SqlTarget(Read Operation, string Table, bool Second = false);

    // A separate full production-model schema keeps the earlier native fixtures out of
    // the aggregate/empty-state oracles. The managed suite checks this resource for drift.
    internal static async Task RunAsync(string connectionString)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var schema = PersistenceProbeLifecycle.SchemaPrefix + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = schema, ApplicationName = "native_metrics_" + Guid.NewGuid().ToString("N"),
            MaxPoolSize = 1, MinPoolSize = 0, Multiplexing = false, LogParameters = false,
            Timeout = 20, CommandTimeout = 15, CancellationTimeout = 1000
        };
        var failures = new List<Exception>();
        Exception? primary = null;
        NpgsqlDataSource? source = null;
        string? schemaSha = null;
        try
        {
            schemaSha = await CreateSchemaAsync(settings.ConnectionString, schema, deadline.Token);
            source = NpgsqlDataSource.Create(settings.ConnectionString);
            var store = new NpgsqlGatewayMetricsStore(source);
            await AssertEmptyAsync(store, deadline.Token);
            await SeedAsync(source, deadline.Token);
            foreach (var operation in Enum.GetValues<Read>())
                await AssertReadAsync(store, operation, deadline.Token);
            await AssertBoundariesAsync(store, deadline.Token);
            await AssertArgumentsAsync(source, store, deadline.Token);
            foreach (var operation in Enum.GetValues<Read>())
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await ExpectCancellationAsync(Invoke(store, operation, cancelled.Token), cancelled);
                await PoolReusableAsync(source, deadline.Token);
                await AssertOccupiedAsync(source, store, operation, deadline.Token);
                await AssertReadAsync(store, operation, deadline.Token);
            }
            var targets = new[]
            {
                new SqlTarget(Read.Usage, "RequestLogs"), new SqlTarget(Read.Cost, "RequestLogs"),
                new SqlTarget(Read.Active, "VirtualKeys"), new SqlTarget(Read.Active, "ModelProviderMappings", true),
                new SqlTarget(Read.Queue, "AsyncTasks"), new SqlTarget(Read.Queue, "AsyncTasks", true),
                new SqlTarget(Read.Generation, "AsyncTasks"), new SqlTarget(Read.Spend, "VirtualKeySpendHistory")
            };
            foreach (var target in targets)
                await AssertSqlCancellationAsync(settings.ConnectionString, schema, target, deadline.Token);
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            if (source is not null)
                await AttemptAsync(() => source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
            await AttemptAsync(() => PersistenceProbeLifecycle.CleanupOwnedSchemaAsync(connectionString, schema), failures);
            await AttemptAsync(() => AssertSchemaAbsentAsync(connectionString, schema), failures);
        }
        Rethrow(primary, failures, "metrics fixture and schema cleanup");
        Console.WriteLine($"Native Gateway metrics persistence passed: methods=6; sqlCancellationPhases=8; preCancellationCases=6; poolCancellationCases=6; invalidInputCases=14; schemaSha256={schemaSha}; all output fields, empty states, groups, time boundaries, exact decimals, caller tokens and single-slot recovery; owned schema removed.");
    }

    private static async Task<string> CreateSchemaAsync(string connectionString, string schema, CancellationToken token)
    {
        await using var resource = typeof(GatewayMetricsPersistenceProbe).Assembly.GetManifestResourceStream(SchemaResource)
            ?? throw new InvalidOperationException("The production metrics schema resource is missing.");
        using var bytes = new MemoryStream();
        await resource.CopyToAsync(bytes, token);
        var schemaBytes = bytes.ToArray();
        var sql = new UTF8Encoding(false, true).GetString(schemaBytes);
        Require(sql.Length > 0 && !sql.Contains('\0'), "bounded production schema resource");
        await using var connection = new NpgsqlConnection(Unpooled(connectionString, "native_metrics_setup"));
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA \"{schema}\"";
        await command.ExecuteNonQueryAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        command.Transaction = transaction;
        command.CommandText = $"SET LOCAL search_path TO \"{schema}\"";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = sql; // Execute the exact resource; do not rewrite model SQL.
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return Convert.ToHexStringLower(SHA256.HashData(schemaBytes));
    }

    private static async Task AssertSchemaAbsentAsync(string connectionString, string schema)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(Unpooled(connectionString, "native_metrics_absence"));
        await connection.OpenAsync(cleanup.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)";
        command.Parameters.AddWithValue("schema", schema);
        Require(await command.ExecuteScalarAsync(cleanup.Token) is true, "owned metrics schema absent");
    }

    private static async Task AssertEmptyAsync(IGatewayMetricsStore store, CancellationToken token)
    {
        Require((await store.GetModelUsageAsync(From, token)).Count == 0, "empty usage");
        Require((await store.GetProviderCostsAsync(From, token)).Count == 0, "empty costs");
        var active = await store.GetActiveEntitiesAsync(Instant, token);
        Require(active.ActiveVirtualKeyCount == 0 && active.MappingsByProvider.Count == 0, "empty active entities");
        var queue = await store.GetTaskQueueMetricsAsync(token);
        Require(queue.QueueDepths.Count == 0 && queue.OldestPendingTasks.Count == 0, "empty queue");
        Require((await store.GetGenerationTaskMetricsAsync("image_generation", From, token)).Count == 0, "empty generation");
        Require((await store.GetTopVirtualKeySpendAsync(From, Instant, 100, token)).Count == 0, "empty spend");
    }

    private static async Task AssertReadAsync(IGatewayMetricsStore store, Read operation, CancellationToken token)
    {
        switch (operation)
        {
            case Read.Usage:
                var usage = await store.GetModelUsageAsync(From, token);
                Equal(usage.OrderBy(row => row.Model, StringComparer.Ordinal).ThenBy(row => row.Provider, StringComparer.Ordinal),
                    new[]
                    {
                        new GatewayModelUsageMetric("model-雪", "unknown", 15.5), new GatewayModelUsageMetric("model-雪", "", 0),
                        new GatewayModelUsageMetric("model-b", "Providerβ", 80.5), new GatewayModelUsageMetric("future", "Providerβ", 120.25),
                        new GatewayModelUsageMetric("negative", "negative-provider", -5.25), new GatewayModelUsageMetric("model-case", "Providerβ", 1.75),
                        new GatewayModelUsageMetric("model-case", "providerβ", 2.75)
                    }.OrderBy(row => row.Model, StringComparer.Ordinal).ThenBy(row => row.Provider, StringComparer.Ordinal), "usage fields/groups");
                break;
            case Read.Cost:
                Equal((await store.GetProviderCostsAsync(From, token)).OrderBy(row => row.Provider, StringComparer.Ordinal),
                    new[] { new GatewayProviderCostMetric("unknown", 1m), new GatewayProviderCostMetric("Providerβ", 5.50000001m) }
                        .OrderBy(row => row.Provider, StringComparer.Ordinal), "provider cost fields/exact decimals/billed boundaries");
                break;
            case Read.Active:
                var active = await store.GetActiveEntitiesAsync(Instant, token);
                Require(active.ActiveVirtualKeyCount == 3, "enabled and expiry-exclusive key count");
                Equal(active.MappingsByProvider.OrderBy(row => row.ProviderId),
                    new[] { new GatewayProviderMappingMetric(201, 2), new GatewayProviderMappingMetric(202, 1) }, "enabled provider/mapping counts");
                break;
            case Read.Queue:
                var queue = await store.GetTaskQueueMetricsAsync(token);
                Equal(queue.QueueDepths.OrderBy(row => row.TaskType, StringComparer.Ordinal).ThenBy(row => row.State),
                    new[]
                    {
                        new GatewayTaskQueueMetric("image_generation", 0, 3), new GatewayTaskQueueMetric("image_generation", 1, 2),
                        new GatewayTaskQueueMetric("video_generation", 0, 1), new GatewayTaskQueueMetric("video_generation", 1, 1),
                        new GatewayTaskQueueMetric("通知", 0, 1), new GatewayTaskQueueMetric("processing_only", 1, 1)
                    }.OrderBy(row => row.TaskType, StringComparer.Ordinal).ThenBy(row => row.State), "queue type/state/count/archive filter");
                Equal(queue.OldestPendingTasks.OrderBy(row => row.TaskType, StringComparer.Ordinal),
                    new[]
                    {
                        new GatewayPendingTaskMetric("image_generation", From.AddTicks(-10)),
                        new GatewayPendingTaskMetric("video_generation", Instant.AddMinutes(-10)), new GatewayPendingTaskMetric("通知", Instant.AddMinutes(2))
                    }.OrderBy(row => row.TaskType, StringComparer.Ordinal), "pending-only minimum timestamps");
                Require(queue.OldestPendingTasks.All(row => row.OldestCreatedAt.Kind == DateTimeKind.Utc), "pending UTC timestamp kinds");
                break;
            case Read.Generation:
                var generation = (await store.GetGenerationTaskMetricsAsync("image_generation", From, token)).OrderBy(row => row.State).ToArray();
                var expected = new[]
                {
                    new GatewayGenerationTaskMetric(0, 3, null), new GatewayGenerationTaskMetric(1, 3, 0), new GatewayGenerationTaskMetric(2, 4, 8d / 3d),
                    new GatewayGenerationTaskMetric(3, 1, -2.5), new GatewayGenerationTaskMetric(4, 1, null), new GatewayGenerationTaskMetric(5, 1, null), new GatewayGenerationTaskMetric(6, 1, null), new GatewayGenerationTaskMetric(8, 1, 0.5)
                };
                Require(generation.Length == expected.Length, "generation state groups");
                for (var index = 0; index < expected.Length; index++)
                {
                    var row = generation[index]; var wanted = expected[index];
                    Require(row.State == wanted.State && row.Count == wanted.Count &&
                        row.AverageDurationSeconds.HasValue == wanted.AverageDurationSeconds.HasValue &&
                        (!row.AverageDurationSeconds.HasValue || Math.Abs(row.AverageDurationSeconds.Value - wanted.AverageDurationSeconds!.Value) < 1e-9),
                        "generation state/count/nullable fractional average including archived and negative durations");
                }
                break;
            case Read.Spend:
                var spend = await store.GetTopVirtualKeySpendAsync(From, Instant, 100, token);
                Equal(spend.OrderBy(row => row.VirtualKeyId), new[]
                {
                    new GatewayVirtualKeySpendMetric(101, 2m), new GatewayVirtualKeySpendMetric(102, 5m), new GatewayVirtualKeySpendMetric(103, 0m),
                    new GatewayVirtualKeySpendMetric(104, -2.00000001m), new GatewayVirtualKeySpendMetric(105, 3m), new GatewayVirtualKeySpendMetric(106, 3m)
                }, "spend IDs/exact decimal sums/from-inclusive/before-exclusive");
                Equal(spend.Select(row => row.TotalSpend), new[] { 5m, 3m, 3m, 2m, 0m, -2.00000001m }, "descending spend totals; ties unordered");
                break;
        }
    }

    private static async Task AssertBoundariesAsync(IGatewayMetricsStore store, CancellationToken token)
    {
        Equal(await store.GetGenerationTaskMetricsAsync("video_generation", From, token),
            new[] { new GatewayGenerationTaskMetric(1, 1, 5) }, "literal generation type");
        foreach (var value in new[] { "missing", " image_generation ", "IMAGE_GENERATION", "generation_雪" })
            Require((await store.GetGenerationTaskMetricsAsync(value, From, token)).Count == 0, "nonblank task type is not normalized");
        Require((await store.GetGenerationTaskMetricsAsync("image_generation", Instant.AddHours(1), token)).Count == 0, "future generation boundary");
        Require((await store.GetModelUsageAsync(Instant.AddHours(1), token)).Count == 0, "future usage boundary");
        Require((await store.GetProviderCostsAsync(Instant.AddHours(1), token)).Count == 0, "future billed boundary");
        Require((await store.GetTopVirtualKeySpendAsync(Instant, Instant, 10, token)).Count == 0 &&
            (await store.GetTopVirtualKeySpendAsync(Instant, From, 10, token)).Count == 0, "empty and reversed spend ranges");
        Equal(await store.GetTopVirtualKeySpendAsync(From, Instant, 1, token), new[] { new GatewayVirtualKeySpendMetric(102, 5m) }, "positive limit one");
        var two = await store.GetTopVirtualKeySpendAsync(From, Instant, 2, token);
        Require(two.Count == 2 && two[0] == new GatewayVirtualKeySpendMetric(102, 5m) &&
            two[1].TotalSpend == 3m && two[1].VirtualKeyId is 105 or 106, "cutoff tie accepts either key");
        Equal((await store.GetTopVirtualKeySpendAsync(From, Instant, 3, token)).OrderBy(row => row.VirtualKeyId),
            new[] { new GatewayVirtualKeySpendMetric(102, 5m), new GatewayVirtualKeySpendMetric(105, 3m), new GatewayVirtualKeySpendMetric(106, 3m) }, "limit includes both ties");
        Equal((await store.GetTopVirtualKeySpendAsync(From, Instant, int.MaxValue, token)).OrderBy(row => row.VirtualKeyId),
            (await store.GetTopVirtualKeySpendAsync(From, Instant, 100, token)).OrderBy(row => row.VirtualKeyId), "maximum positive limit");
    }

    private static async Task AssertArgumentsAsync(NpgsqlDataSource source, IGatewayMetricsStore store, CancellationToken token)
    {
        NpgsqlConnection? held = null;
        var pending = new List<Task>();
        using var caller = new CancellationTokenSource();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Exception? primary = null; var failures = new List<Exception>();
        try
        {
            held = await source.OpenConnectionAsync(token);
            var control = Invoke(store, Read.Generation, caller.Token); pending.Add(control);
            Require(!control.IsCompleted, "valid guard control waits on the sole slot");
            foreach (var callerToken in new[] { caller.Token, cancelled.Token })
            {
                foreach (var value in new[] { null, "", " \t\r\n", "\u00a0\u2003\u202f" })
                {
                    var task = store.GetGenerationTaskMetricsAsync(value!, From, callerToken); pending.Add(task);
                    await ExpectArgumentAsync(task, value is null ? typeof(ArgumentNullException) : typeof(ArgumentException), "taskType");
                }
                foreach (var limit in new[] { 0, -1, int.MinValue })
                {
                    var task = store.GetTopVirtualKeySpendAsync(From, Instant, limit, callerToken); pending.Add(task);
                    await ExpectArgumentAsync(task, typeof(ArgumentOutOfRangeException), "limit", limit);
                }
            }
            Require(!control.IsCompleted, "invalid inputs do not consume or release the valid control's occupied slot");
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            caller.Cancel();
            for (var index = 0; index < pending.Count; index++) await DrainAsync(pending[index], failures, allowArgument: index != 0);
            if (held is not null) await AttemptAsync(() => held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
            for (var index = 0; index < pending.Count; index++) await DrainAsync(pending[index], failures, allowArgument: index != 0);
        }
        Rethrow(primary, failures, "input validation cleanup");
        await PoolReusableAsync(source, token);
    }

    private static async Task AssertOccupiedAsync(NpgsqlDataSource source, IGatewayMetricsStore store, Read operation, CancellationToken token)
    {
        NpgsqlConnection? held = null; Task? pending = null;
        using var caller = new CancellationTokenSource();
        Exception? primary = null; var failures = new List<Exception>();
        try
        {
            held = await source.OpenConnectionAsync(token);
            pending = Invoke(store, operation, caller.Token);
            Require(!pending.IsCompleted, "metrics read waits on the held sole slot");
            caller.Cancel();
            await ExpectCancellationAsync(pending, caller);
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            caller.Cancel();
            if (pending is not null) await DrainAsync(pending, failures);
            if (held is not null) await AttemptAsync(() => held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
            if (pending is not null) await DrainAsync(pending, failures);
        }
        Rethrow(primary, failures, "occupied pool cancellation cleanup");
        await PoolReusableAsync(source, token);
    }

    private static Task Invoke(IGatewayMetricsStore store, Read operation, CancellationToken token) => operation switch
    {
        Read.Usage => store.GetModelUsageAsync(From, token), Read.Cost => store.GetProviderCostsAsync(From, token),
        Read.Active => store.GetActiveEntitiesAsync(Instant, token), Read.Queue => store.GetTaskQueueMetricsAsync(token),
        Read.Generation => store.GetGenerationTaskMetricsAsync("image_generation", From, token),
        Read.Spend => store.GetTopVirtualKeySpendAsync(From, Instant, 100, token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static async Task AssertSqlCancellationAsync(string connectionString, string schema, SqlTarget target, CancellationToken token)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token); work.CancelAfter(TimeSpan.FromSeconds(25));
        var application = "native_metrics_sql_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = application };
        using var gate = new CommandGate(target);
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
        builder.UseLoggerFactory(gate).EnableParameterLogging(false);
        var source = builder.Build();
        var store = new NpgsqlGatewayMetricsStore(source);
        var readerPid = 0;
        var blocker = new NpgsqlConnection(Unpooled(connectionString, application + "_blocker"));
        NpgsqlTransaction? transaction = null; Task? pending = null;
        using var caller = new CancellationTokenSource();
        Exception? primary = null; var failures = new List<Exception>();
        try
        {
            readerPid = await PoolReusableAsync(source, work.Token);
            await blocker.OpenAsync(work.Token);
            transaction = await blocker.BeginTransactionAsync(work.Token);
            gate.Arm();
            pending = Task.Run(() => Invoke(store, target.Operation, caller.Token));
            var entered = await gate.Entered.WaitAsync(work.Token);
            Require(entered.Ordinal == (target.Second ? 2 : 1) && entered.CompletedBefore == (target.Second ? 1 : 0),
                "target SQL statement reached after the expected completed statements");
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"LOCK TABLE \"{schema}\".\"{target.Table}\" IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync(work.Token);
            }
            gate.Release();
            await WaitForSqlAsync(connectionString, application, readerPid, blocker.ProcessID, schema, target.Table, pending, work.Token);
            caller.Cancel();
            await ExpectCancellationAsync(pending, caller);
            Require(await PoolReusableAsync(source, work.Token) == readerPid, "same sole slot recovered while relation remains locked");
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            gate.Release(); caller.Cancel();
            if (pending is not null) await DrainAsync(pending, failures);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (transaction is not null)
            {
                await AttemptAsync(() => transaction.RollbackAsync(cleanup.Token).WaitAsync(TimeSpan.FromSeconds(5)), failures);
                await AttemptAsync(() => transaction.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
            }
            await AttemptAsync(() => blocker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
            if (pending is not null) await DrainAsync(pending, failures);
            if (primary is null && failures.Count == 0)
                await AttemptAsync(async () =>
                {
                    await PoolReusableAsync(source, work.Token);
                    await AssertReadAsync(store, target.Operation, work.Token);
                }, failures);
            await AttemptAsync(() => source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
        }
        Rethrow(primary, failures, "observed SQL cancellation cleanup");
    }

    private static async Task WaitForSqlAsync(string connectionString, string application, int reader, int blocker,
        string schema, string table, Task pending, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(Unpooled(connectionString, "native_metrics_observer"));
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity a JOIN pg_locks waiting ON waiting.pid = a.pid
                JOIN pg_class relation ON relation.oid = waiting.relation
                JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE a.pid = @reader AND a.application_name = @application AND a.datname = current_database()
                    AND a.state = 'active' AND a.wait_event_type = 'Lock'
                    AND waiting.locktype = 'relation' AND waiting.mode = 'AccessShareLock' AND NOT waiting.granted
                    AND namespace.nspname = @schema AND relation.relname = @table
                    AND waiting.relation = to_regclass(@relation) AND @blocker = ANY(pg_blocking_pids(a.pid))
                    AND EXISTS (SELECT 1 FROM pg_locks held WHERE held.pid = @blocker
                        AND held.relation = waiting.relation AND held.mode = 'AccessExclusiveLock' AND held.granted))
            """;
        command.Parameters.AddWithValue("reader", reader); command.Parameters.AddWithValue("application", application);
        command.Parameters.AddWithValue("blocker", blocker); command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table); command.Parameters.AddWithValue("relation", $"\"{schema}\".\"{table}\"");
        while (true)
        {
            if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Metrics SQL completed before its exact relation wait was observed."); }
            if (await command.ExecuteScalarAsync(token) is true) return;
            await Task.Delay(25, token);
        }
    }

    private static async Task<int> PoolReusableAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT 1";
        Require(await command.ExecuteScalarAsync(token) is 1, "pool SELECT 1");
        return connection.ProcessID;
    }

    private static async Task ExpectCancellationAsync(Task pending, CancellationTokenSource caller)
    {
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Metrics read ignored caller cancellation."); }
        catch (OperationCanceledException exception) when (caller.IsCancellationRequested)
        { Require(exception.CancellationToken == caller.Token, "caller cancellation token identity"); }
    }

    private static async Task ExpectArgumentAsync(Task pending, Type type, string parameter, int? value = null)
    {
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("Metrics input guard did not reject invalid input."); }
        catch (ArgumentException exception)
        {
            Require(exception.GetType() == type && exception.ParamName == parameter, "exact argument exception and parameter");
            if (value.HasValue) Require(exception is ArgumentOutOfRangeException range && Equals(range.ActualValue, value.Value), "invalid limit value");
        }
    }

    private static async Task DrainAsync(Task task, List<Exception> failures, bool allowArgument = false)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        catch (ArgumentException) when (allowArgument) { } // Already checked invalid-input tasks are also joined.
        catch (Exception exception) { failures.Add(exception); }
    }

    private static async Task AttemptAsync(Func<Task> cleanup, List<Exception> failures)
    { try { await cleanup(); } catch (Exception exception) { failures.Add(exception); } }

    private static void Rethrow(Exception? primary, List<Exception> failures, string stage)
    {
        if (primary is not null && failures.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary is not null) failures.Insert(0, primary);
        if (failures.Count != 0) throw new AggregateException("Native Gateway " + stage + " failed.", failures);
    }

    private static string Unpooled(string connectionString, string application) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        { Pooling = false, ApplicationName = application, Timeout = 10, CommandTimeout = 15, CancellationTimeout = 1000 }.ConnectionString;

    private static void Equal<T>(IEnumerable<T> actual, IEnumerable<T> expected, string contract) =>
        Require(actual.SequenceEqual(expected), contract);

    private static void Require(bool condition, string contract)
    { if (!condition) throw new InvalidOperationException("Native Gateway metrics contract failed: " + contract + "."); }

    private sealed record GateEntry(int Ordinal, int CompletedBefore);
    private sealed class CommandGate(SqlTarget target) : ILoggerFactory, ILogger
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource<GateEntry> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed, _claimed, _started, _completed;
        internal Task<GateEntry> Entered => _entered.Task;
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _release.Set();
        public ILogger CreateLogger(string categoryName) => categoryName == "Npgsql.Command" ? this :
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Volatile.Read(ref _armed) == 0 || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToArray();
            Require(!values.Any(field => field.Key == "Parameters"), "SQL logger never receives parameter values");
            var sql = values.FirstOrDefault(field => field.Key == "CommandText").Value as string;
            if (sql is null) return;
            if (eventId.Id == NpgsqlEventId.CommandExecutionCompleted) { Interlocked.Increment(ref _completed); return; }
            if (eventId.Id != NpgsqlEventId.ExecutingCommand) return;
            var ordinal = Interlocked.Increment(ref _started);
            if (!sql.Contains('"' + target.Table + '"', StringComparison.Ordinal)) return;
            if (target.Operation == Read.Queue && sql.Contains("MIN(", StringComparison.OrdinalIgnoreCase) != target.Second) return;
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return;
            _entered.TrySetResult(new GateEntry(ordinal, Volatile.Read(ref _completed)));
            if (!_release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Native metrics before-send gate was not released.");
        }
        public void Dispose() { Release(); _release.Dispose(); }
    }

    private static async Task SeedAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "VirtualKeyGroups" ("Id", "GroupName", "Balance", "LifetimeCreditsAdded", "LifetimeSpent", "CreatedAt", "UpdatedAt")
                VALUES (1, 'native metrics group', 0, 0, 0, @now, @now);
            INSERT INTO "VirtualKeys" ("Id", "KeyName", "KeyHash", "IsEnabled", "VirtualKeyGroupId", "ExpiresAt", "CreatedAt", "UpdatedAt")
                SELECT id, 'native metrics key ' || id, 'native-metrics-' || id, id NOT IN (105,106), 1,
                    CASE WHEN id IN (102,106) THEN @future WHEN id=103 THEN @now WHEN id=104 THEN @prior ELSE NULL END,
                    @now, @now FROM generate_series(101,107) AS id;
            INSERT INTO "Providers" ("Id", "ProviderType", "ProviderName", "IsEnabled", "TrustProviderReportedCosts", "ProviderCostMarkupMultiplier", "CreatedAt", "UpdatedAt")
                VALUES (201,0,'native metrics A',TRUE,FALSE,1,@now,@now),
                    (202,0,'native metrics B',TRUE,FALSE,1,@now,@now), (203,0,'native metrics disabled',FALSE,FALSE,1,@now,@now);
            INSERT INTO "ModelAuthors" ("Id", "Name") VALUES (401,'native metrics author');
            INSERT INTO "ModelSeries" ("Id", "AuthorId", "Name", "TokenizerType", "Parameters") VALUES (501,401,'native metrics series',0,'{}');
            INSERT INTO "Models" ("Id", "Name", "ModelSeriesId", "SupportsVision", "SupportsImageGeneration", "SupportsVideoGeneration",
                "SupportsEmbeddings", "SupportsSpeechToText", "SupportsTextToSpeech", "SupportsRerank", "SupportsChat", "SupportsFunctionCalling",
                "SupportsStreaming", "CapabilitySource", "TokenizerType", "IsActive", "CreatedAt", "UpdatedAt")
                VALUES (601,'native metrics inactive model',501,FALSE,FALSE,FALSE,FALSE,FALSE,FALSE,FALSE,FALSE,FALSE,FALSE,0,0,FALSE,@now,@now);
            INSERT INTO "ModelIdentifiers" ("Id", "ModelId", "IsEnabled", "Identifier", "Provider", "IsPrimary")
                SELECT 701+i,601,i<>1,'native-metrics-identifier-' || i,0,FALSE FROM generate_series(0,4) AS i;
            INSERT INTO "ModelProviderMappings" ("Id", "ModelAlias", "ProviderModelId", "ProviderId", "IsEnabled", "RoutingPriority", "RoutingWeight",
                "CreatedAt", "UpdatedAt", "ModelProviderTypeAssociationId")
                SELECT 301+i,'native-metrics-alias-' || i,'native-metrics-model-' || i,
                    CASE WHEN i<3 THEN 201 WHEN i=3 THEN 202 ELSE 203 END,i<>2,100,1,@now,@now,701+i FROM generate_series(0,4) AS i;
            INSERT INTO "RequestLogs" ("VirtualKeyId", "ModelName", "ProviderType", "ResponseTimeMs", "Cost", "Timestamp", "BilledAtUtc",
                "RequestType", "InputTokens", "OutputTokens", "PromptCachingEligible", "PromptCachingPolicyApplied", "CachedReadSavings", "CacheWritePremium", "RoutingAffinityUsed", "RoutingFailoverCount")
                SELECT 101,model,provider,latency,cost,stamp,billed,'chat',0,0,FALSE,FALSE,0,0,FALSE,0 FROM (VALUES
                    ('model-雪',NULL,10.5::float8,0.12345678::numeric,@from,NULL::timestamptz),
                    ('model-雪','unknown',20.5,0.87654322,@recent,@recent), ('model-雪','',0,0,@recent,NULL),
                    ('model-b','Providerβ',-1.25,2.00000001,@beforeFrom,@from),
                    ('model-b','Providerβ',80.5,9,@now,@beforeFrom), ('future','Providerβ',120.25,3.5,@future,@future),
                    ('excluded','OpenAI',999,7,@beforeFrom,NULL), ('negative','negative-provider',-5.25,-1,@now,NULL),
                    ('model-case','Providerβ',1.75,0,@now,NULL), ('model-case','providerβ',2.75,0,@now,NULL)
                ) AS requests(model,provider,latency,cost,stamp,billed);
            INSERT INTO "AsyncTasks" ("Id", "Type", "State", "CreatedAt", "UpdatedAt", "CompletedAt", "IsArchived", "VirtualKeyId",
                "Progress", "Version", "RetryCount", "MaxRetries", "IsRetryable")
                SELECT id,type,state,created,created,CASE WHEN duration IS NULL THEN NULL ELSE created + duration * interval '1 second' END,
                    archived,101,0,0,0,3,TRUE FROM (VALUES
                    ('pending-boundary','image_generation',0,@from,NULL::float8,FALSE),
                    ('pending-before','image_generation',0,@beforeFrom,NULL,FALSE), ('pending-future','image_generation',0,@future,NULL,FALSE),
                    ('processing-null','image_generation',1,@now,NULL,FALSE), ('processing-zero','image_generation',1,@now,0,FALSE),
                    ('completed-short','image_generation',2,@recent,1.25,FALSE), ('completed-long','image_generation',2,@twoAgo,2.75,FALSE),
                    ('completed-null','image_generation',2,@now,NULL,FALSE), ('archived-completed','image_generation',2,@now,4,TRUE),
                    ('failed-negative','image_generation',3,@now,-2.5,FALSE), ('cancelled','image_generation',4,@now,NULL,FALSE),
                    ('timed-out','image_generation',5,@now,NULL,FALSE), ('indeterminate','image_generation',6,@now,NULL,FALSE),
                    ('unknown-state','image_generation',8,@now,0.5,FALSE), ('archived-pending','image_generation',0,@now,NULL,TRUE),
                    ('archived-processing','image_generation',1,@now,NULL,TRUE), ('video-pending','video_generation',0,@tenAgo,NULL,FALSE),
                    ('video-processing','video_generation',1,@now,5,FALSE), ('unicode-pending','通知',0,@twoFuture,NULL,FALSE),
                    ('processing-only','processing_only',1,@now,NULL,FALSE)
                ) AS tasks(id,type,state,created,duration,archived);
            INSERT INTO "VirtualKeySpendHistory" ("VirtualKeyId", "Amount", "Date", "Timestamp")
                SELECT key,amount,@irrelevantDate,stamp FROM (VALUES
                    (101,1.12345678::numeric,@from), (101,0.87654322,@recent), (102,5.50000001,@recent), (102,-0.50000001,@recent),
                    (103,0,@recent), (104,-2.00000001,@recent), (105,3,@recent), (106,3,@recent),
                    (101,99,@beforeFrom), (101,100,@now), (101,100,@future)
                ) AS spend(key,amount,stamp);
            """;
        command.Parameters.AddWithValue("now", Instant); command.Parameters.AddWithValue("from", From);
        command.Parameters.AddWithValue("beforeFrom", From.AddTicks(-10)); command.Parameters.AddWithValue("prior", Instant.AddTicks(-10));
        command.Parameters.AddWithValue("recent", Instant.AddMinutes(-1)); command.Parameters.AddWithValue("future", Instant.AddMinutes(1));
        command.Parameters.AddWithValue("twoAgo", Instant.AddMinutes(-2)); command.Parameters.AddWithValue("tenAgo", Instant.AddMinutes(-10));
        command.Parameters.AddWithValue("twoFuture", Instant.AddMinutes(2)); command.Parameters.AddWithValue("irrelevantDate", Instant.AddYears(-1));
        await command.ExecuteNonQueryAsync(token);
    }
}
