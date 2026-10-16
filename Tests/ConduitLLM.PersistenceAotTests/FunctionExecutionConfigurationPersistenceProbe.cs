using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

internal static class FunctionExecutionConfigurationPersistenceProbe
{
    // Runs after the discovery fixture in the same caller-owned disposable schema.
    // The only new table is FunctionCredentials; no runtime Functions implementation
    // or credential decryption is imported by this persistence probe.
    internal static async Task RunAsync(NpgsqlDataSource source, string connectionString)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Require(new NpgsqlConnectionStringBuilder(source.ConnectionString).MaxPoolSize == 1, "single-slot fixture");
        await using (var connection = await source.OpenConnectionAsync(deadline.Token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "FunctionCredentials" (
                    "Id" integer PRIMARY KEY, "ProviderType" integer NOT NULL,
                    "FunctionConfigurationId" integer NULL, "ApiKey" varchar(2000) NULL,
                    "BaseUrl" varchar(500) NULL, "Organization" varchar(200) NULL,
                    "FunctionAccountGroup" smallint NOT NULL, "IsPrimary" boolean NOT NULL,
                    "IsEnabled" boolean NOT NULL, "KeyName" varchar(200) NULL,
                    "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
                INSERT INTO "FunctionCredentials" VALUES
                    (100, @exa, NULL, 'global plaintext', NULL, NULL, 0, TRUE, TRUE, 'B global', @created, @updated),
                    (101, @exa, 11, @opaque, 'https://example.test/scoped', 'O''Hare 雪', 32, TRUE, TRUE, 'A scoped', @created, @updated),
                    (102, @exa, 12, 'enc:v1:invalid', '', '', -7, FALSE, FALSE, '0 secondary', @created, @updated),
                    (103, @exa, NULL, NULL, NULL, NULL, 0, FALSE, TRUE, NULL, @created, @updated),
                    (104, 197, NULL, NULL, NULL, NULL, 32767, FALSE, TRUE, NULL, @created, @updated);
                """;
            command.Parameters.AddWithValue("exa", (int)FunctionProviderType.Exa);
            command.Parameters.AddWithValue("opaque", "enc:v1:opaque=='雪'");
            var created = new DateTime(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc);
            command.Parameters.AddWithValue("created", created);
            command.Parameters.AddWithValue("updated", created.AddMinutes(1));
            await command.ExecuteNonQueryAsync(deadline.Token);
        }

        var store = new NpgsqlFunctionExecutionConfigurationStore(source);
        var disabled = await store.GetConfigurationAsync(12, deadline.Token);
        Require(disabled is { IsEnabled: false, ConfigurationName: "Disabled" } &&
            disabled.CostMappings.Count == 0 && disabled.Executions.Count == 0, "disabled scalar configuration");
        Require(await store.GetConfigurationAsync(-1, deadline.Token) is null, "missing configuration");
        var unknownConfiguration = await store.GetConfigurationAsync(13, deadline.Token);
        Require(unknownConfiguration is not null && (int)unknownConfiguration.ProviderType == 197 &&
            (int)unknownConfiguration.Purpose == -9 && (int)unknownConfiguration.DefaultExecutionMode == 123,
            "unknown configuration enums");
        var configurations = await store.GetConfigurationsAsync(new[] { 11, 12, 11, 13, -1 }, deadline.Token);
        Require(configurations.Select(configuration => configuration.Id).Order().SequenceEqual(new[] { 11, 12, 13 }),
            "distinct existing IDs including disabled");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Require((await store.GetConfigurationsAsync(null, cancelled.Token)).Count == 0 &&
                (await store.GetConfigurationsAsync(Array.Empty<int>(), cancelled.Token)).Count == 0,
                "cancelled empty ID input");
            try
            {
                await store.GetConfigurationsAsync(new[] { 11 }, cancelled.Token);
                throw new InvalidOperationException("Native execution configuration read ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { /* Expected owned cancellation; every other failure propagates. */ }
        }

        var credentials = await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, deadline.Token);
        Require(credentials.Select(credential => credential.Id).SequenceEqual(new[] { 101, 100, 102, 103 }),
            "primary/key-name/null ordering");
        Require(credentials.Single(credential => credential.Id == 100).ApiKey == "global plaintext",
            "legacy plaintext credential");
        var scoped = credentials.Single(credential => credential.Id == 101);
        Require(scoped.ProviderType == FunctionProviderType.Exa && scoped.FunctionConfigurationId == 11 &&
            scoped.ApiKey == "enc:v1:opaque=='雪'" && scoped.BaseUrl == "https://example.test/scoped" &&
            scoped.Organization == "O'Hare 雪" && scoped.FunctionAccountGroup == 32 && scoped.IsPrimary &&
            scoped.IsEnabled && scoped.KeyName == "A scoped" && scoped.CreatedAt.Kind == DateTimeKind.Utc &&
            scoped.UpdatedAt == scoped.CreatedAt.AddMinutes(1), "all credential scalars and opaque ciphertext");
        var other = credentials.Single(credential => credential.Id == 102);
        Require(other.FunctionConfigurationId == 12 && !other.IsPrimary && !other.IsEnabled && other.ApiKey == "enc:v1:invalid" &&
            other.BaseUrl == "" && other.Organization == "" && other.FunctionAccountGroup == -7,
            "disabled other-configuration credentials and empty values");
        scoped.ApiKey = "detached mutation";
        Require((await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, deadline.Token))
            .Single(credential => credential.Id == 101).ApiKey == "enc:v1:opaque=='雪'", "detached credentials");
        var unknownCredential = (await store.ListCredentialsForProviderAsync((FunctionProviderType)197, deadline.Token)).Single();
        Require(unknownCredential.Id == 104 && (int)unknownCredential.ProviderType == 197 &&
            unknownCredential.FunctionAccountGroup == short.MaxValue && unknownCredential.FunctionConfigurationId is null &&
            unknownCredential.ApiKey is null && unknownCredential.BaseUrl is null && unknownCredential.Organization is null &&
            unknownCredential.KeyName is null, "unknown provider and nullable credential scalars");
        Require((await store.ListCredentialsForProviderAsync(FunctionProviderType.Tavily, deadline.Token)).Count == 0,
            "missing credential provider");

        await using (await source.OpenConnectionAsync(deadline.Token))
        {
            using var cancelled = new CancellationTokenSource();
            var read = store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, cancelled.Token);
            try
            {
                Require(!read.IsCompleted, "read waiting on occupied pool slot");
                cancelled.Cancel();
                try
                {
                    await read.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("Native credential pool wait ignored cancellation.");
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
        Require((await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, deadline.Token)).Count == 4,
            "single-slot reuse after blocked acquisition cancellation");
        foreach (var method in new[]
        {
            nameof(NpgsqlFunctionExecutionConfigurationStore.GetConfigurationAsync),
            nameof(NpgsqlFunctionExecutionConfigurationStore.GetConfigurationsAsync),
            nameof(NpgsqlFunctionExecutionConfigurationStore.ListCredentialsForProviderAsync)
        })
            await AssertSqlCancellationAsync(source, store, connectionString, method, deadline.Token);
        Console.WriteLine("Function execution configuration persistence passed: scalar/disabled/distinct-ID reads, ordered opaque credentials, detached values, pool cancellation and observed SQL-lock cancellation of all three reads with single-slot reuse.");
    }

    private static async Task AssertSqlCancellationAsync(NpgsqlDataSource source,
        NpgsqlFunctionExecutionConfigurationStore store, string connectionString, string method, CancellationToken token)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        work.CancelAfter(TimeSpan.FromSeconds(20));
        var table = method == nameof(NpgsqlFunctionExecutionConfigurationStore.ListCredentialsForProviderAsync)
            ? "FunctionCredentials" : "FunctionConfigurations";
        int readerPid;
        string schema, applicationName;
        await using (var warm = await source.OpenConnectionAsync(work.Token))
        await using (var command = warm.CreateCommand())
        {
            readerPid = warm.ProcessID;
            command.CommandText = "SELECT current_schema(), current_setting('application_name')";
            await using var reader = await command.ExecuteReaderAsync(work.Token);
            Require(await reader.ReadAsync(work.Token), "SQL cancellation reader identity");
            schema = reader.GetString(0);
            applicationName = reader.GetString(1);
        }
        Require(schema.StartsWith(PersistenceProbeLifecycle.SchemaPrefix, StringComparison.Ordinal) &&
            schema.Length == PersistenceProbeLifecycle.SchemaPrefix.Length + 32 &&
            Guid.TryParseExact(schema.AsSpan(PersistenceProbeLifecycle.SchemaPrefix.Length), "N", out _),
            "SQL cancellation uses the caller-owned GUID schema");
        var relation = $"\"{schema}\".\"{table}\"";
        var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false, ApplicationName = "native_function_configuration_blocker",
            Timeout = 5, CommandTimeout = 5, CancellationTimeout = 1000
        }.ConnectionString);
        NpgsqlTransaction? transaction = null;
        using var cancellation = new CancellationTokenSource();
        Task? pending = null;
        Exception? failure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            await blocker.OpenAsync(work.Token);
            transaction = await blocker.BeginTransactionAsync(work.Token);
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"LOCK TABLE {relation} IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync(work.Token);
            }
            pending = method switch
            {
                nameof(NpgsqlFunctionExecutionConfigurationStore.GetConfigurationAsync) => store.GetConfigurationAsync(11, cancellation.Token),
                nameof(NpgsqlFunctionExecutionConfigurationStore.GetConfigurationsAsync) => store.GetConfigurationsAsync([11, 12, 11, 13, -1], cancellation.Token),
                nameof(NpgsqlFunctionExecutionConfigurationStore.ListCredentialsForProviderAsync) => store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, cancellation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(method))
            };
            await WaitForSqlLockAsync(connectionString, relation, readerPid, applicationName, blocker.ProcessID, pending, work.Token);
            cancellation.Cancel();
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("Function configuration SQL read ignored observed cancellation.");
            }
            catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
            {
                Require(exception.CancellationToken == cancellation.Token, "SQL cancellation preserves the caller token");
            }
            // The lock remains held: obtaining the sole pooled slot for SELECT 1
            // proves the cancelled command has actually released that slot.
            await using var recovered = await source.OpenConnectionAsync(work.Token);
            await using var recovery = recovered.CreateCommand();
            recovery.CommandText = "SELECT 1";
            Require(await recovery.ExecuteScalarAsync(work.Token) is 1, "single slot released before table unlock");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            cancellation.Cancel();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            // An expired work deadline must not suppress lock release.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(cleanup.Token); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
                try { await transaction.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            // Disposing the independent connection releases its lock even if rollback failed.
            try { await blocker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            if (pending is not null)
            {
                // A first reap failure must not leave an operation behind after unlock.
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
        }
        if (failure is not null)
        {
            if (cleanupFailures.Count != 0)
                throw new AggregateException("Function configuration SQL cancellation and cleanup failed.", new[] { failure }.Concat(cleanupFailures));
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (cleanupFailures.Count != 0)
            throw new AggregateException("Function configuration SQL cancellation cleanup failed.", cleanupFailures);
        using var recoveredReads = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Require(await store.GetConfigurationAsync(12, recoveredReads.Token) is { IsEnabled: false, ConfigurationName: "Disabled" },
            "scalar recovery after SQL cancellation");
        Require((await store.GetConfigurationsAsync([11, 12, 11, 13, -1], recoveredReads.Token))
            .Select(configuration => configuration.Id).Order().SequenceEqual(new[] { 11, 12, 13 }), "ID-list recovery after SQL cancellation");
        Require((await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, recoveredReads.Token))
            .Select(credential => credential.Id).SequenceEqual(new[] { 101, 100, 102, 103 }), "credential recovery after SQL cancellation");
    }

    private static async Task WaitForSqlLockAsync(string connectionString, string relation,
        int readerPid, string applicationName, int blockerPid, Task pending, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false, ApplicationName = "native_function_configuration_observer",
            Timeout = 5, CommandTimeout = 5, CancellationTimeout = 1000
        }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity a
                JOIN pg_locks waiting ON waiting.pid = a.pid
                WHERE a.pid = @reader AND a.application_name = @application
                    AND a.datid = (SELECT oid FROM pg_database WHERE datname = current_database())
                    AND a.state = 'active' AND a.wait_event_type = 'Lock' AND waiting.locktype = 'relation'
                    AND waiting.relation = @relation::regclass AND waiting.mode = 'AccessShareLock' AND NOT waiting.granted
                    AND @blocker = ANY(pg_blocking_pids(a.pid))
                    AND EXISTS (SELECT 1 FROM pg_locks held WHERE held.pid = @blocker
                        AND held.relation = waiting.relation AND held.mode = 'AccessExclusiveLock' AND held.granted))
            """;
        command.Parameters.AddWithValue("reader", readerPid);
        command.Parameters.AddWithValue("application", applicationName);
        command.Parameters.AddWithValue("relation", relation);
        command.Parameters.AddWithValue("blocker", blockerPid);
        while (true)
        {
            if (pending.IsCompleted)
            {
                await pending;
                throw new InvalidOperationException("Function configuration read completed before its exact SQL lock wait was observed.");
            }
            if (await command.ExecuteScalarAsync(token) is true) return;
            await Task.Delay(25, token);
        }
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException($"Native function execution configuration contract failed: {contract}.");
    }
}
