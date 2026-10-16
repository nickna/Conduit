using AwesomeAssertions;

using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class ModelProviderMappingRuntimePersistenceParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothPagesPreserveNormalizationOrderingCountsFiltersAndCompleteGraphsWithOnePoolSlot(bool typedNpgsql)
    {
        await InSingleSlotSchemaAsync(async (schema, source, ids, token) =>
        {
            var selection = await SeedSelectionMatrixAsync(schema, ids, token);
            var snapshots = await SeedSnapshotMatrixAsync(schema, token);
            var store = CreateMatrixStore(source, typedNpgsql);
            var graphRows = new[] { snapshots.Populated, snapshots.Shared, snapshots.Nulls };
            // PostgreSQL uses the database's collation for mixed-case aliases.
            // Read only scalar IDs through an independent unpooled connection;
            // a CLR ordinal sort would impose a different ordering contract.
            var expectedIds = await ReadOrderedSingleSlotIdsAsync(schema, null, token);
            expectedIds.Should().BeEquivalentTo(selection.Mappings.Select(row => row.Id)
                .Concat(graphRows.Select(row => row.Id)).Append(ids.PrimaryMappingId).Append(ids.SecondaryMappingId));
            var providerIds = selection.Mappings.OrderBy(row => row.ModelAlias, StringComparer.Ordinal)
                .ThenBy(row => row.Id).Select(row => row.Id).Append(ids.SecondaryMappingId).ToArray();
            var observedGraphs = new HashSet<int>();
            foreach (var (page, size, normalizedPage, normalizedSize) in new[]
                { (-3, 0, 1, 20), (0, -8, 1, 20), (0, 1, 1, 1), (1, 500, 1, 100),
                  (2, 100, 2, 100), (2, 1, 2, 1), (9, 100, 9, 100) })
            {
                var all = await store.GetPaginatedAsync(page, size, token);
                all.TotalCount.Should().Be(106);
                all.Items.Select(row => row.Id).Should()
                    .Equal(expectedIds.Skip((normalizedPage - 1) * normalizedSize).Take(normalizedSize));
                foreach (var row in all.Items)
                    if (graphRows.SingleOrDefault(expected => expected.Id == row.Id) is { } expected)
                    {
                        AssertGraphSnapshot(row, expected);
                        observedGraphs.Add(row.Id);
                    }
                var filtered = await store.GetByProviderPaginatedAsync(selection.ProviderId, page, size, token);
                filtered.TotalCount.Should().Be(102);
                filtered.Items.Select(row => row.Id).Should()
                    .Equal(providerIds.Skip((normalizedPage - 1) * normalizedSize).Take(normalizedSize));
                filtered.Items.Should().OnlyContain(row => row.ProviderId == selection.ProviderId);
                await AssertSingleSlotReusableAsync(source, token);
            }
            observedGraphs.Should().BeEquivalentTo(graphRows.Select(row => row.Id));
            foreach (var provider in graphRows.GroupBy(row => row.ProviderId))
            {
                var filtered = await store.GetByProviderPaginatedAsync(provider.Key, 0, 500, token);
                filtered.TotalCount.Should().Be(provider.Count());
                filtered.Items.Select(row => row.Id).Should()
                    .Equal(await ReadOrderedSingleSlotIdsAsync(schema, provider.Key, token));
                foreach (var row in filtered.Items) AssertGraphSnapshot(row, provider.Single(expected => expected.Id == row.Id));
            }
            var missing = await store.GetByProviderPaginatedAsync(-1, 0, 500, token);
            missing.TotalCount.Should().Be(0);
            missing.Items.Should().BeEmpty();
            await AssertSingleSlotReusableAsync(source, token);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BothPagesCancelBeforeExecutionInOccupiedPoolAndBothObservedSqlPhasesThenReuseOneSlot(
        bool typedNpgsql, bool providerPage)
    {
        await InSingleSlotSchemaAsync(async (schema, source, ids, token) =>
        {
            var store = CreateMatrixStore(source, typedNpgsql);
            var providerId = (await store.GetByIdAsync(ids.SecondaryMappingId, token))!.ProviderId;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    ReadSingleSlotPageAsync(store, providerId, providerPage, 1, 10, cancelled.Token));
                Assert.Equal(cancelled.Token, cancellation.CancellationToken);
            }
            await AssertSingleSlotPageRecoveryAsync(store, providerId, providerPage, ids, token);
            NpgsqlConnection? held = null;
            Task? pendingPoolRead = null;
            using (var caller = new CancellationTokenSource())
            {
                await IntegrationProofCleanup.RunAsync(async () =>
                {
                    held = await source.OpenConnectionAsync(token);
                    pendingPoolRead = ReadSingleSlotPageAsync(store, providerId, providerPage, 1, 10, caller.Token);
                    pendingPoolRead.IsCompleted.Should().BeFalse("the test owns the only pool slot before dispatch");
                    caller.Cancel();
                    var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        pendingPoolRead.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(caller.Token, cancellation.CancellationToken);
                }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
                    [async cleanup =>
                        {
                            caller.Cancel();
                            if (pendingPoolRead is not null) await ReapSingleSlotReadAsync(pendingPoolRead, cleanup);
                        },
                     async cleanup =>
                        {
                            if (held is not null) await held.DisposeAsync().AsTask().WaitAsync(cleanup);
                        },
                     async cleanup =>
                        {
                            if (pendingPoolRead is not null) await ReapSingleSlotReadAsync(pendingPoolRead, cleanup);
                        }],
                    TimeSpan.FromSeconds(5), "Occupied single-slot page could not be joined."),
                    "Occupied single-slot page cancellation and cleanup failed.");
            }
            await AssertSingleSlotReusableAsync(source, token);
            await AssertSingleSlotPageRecoveryAsync(store, providerId, providerPage, ids, token);
            foreach (var table in new[] { "ModelProviderMappings", "Providers" })
            {
                await CancelSingleSlotSqlAsync(schema, source, store, ids, providerId, providerPage, table, token);
                await AssertSingleSlotPageRecoveryAsync(store, providerId, providerPage, ids, token);
            }
        });
    }

    private async Task<int[]> ReadOrderedSingleSlotIdsAsync(string schema, int? providerId, CancellationToken token)
    {
        await using var observer = await OpenSingleSlotObserverAsync(schema, "ordering", token);
        await using var command = observer.CreateCommand();
        command.CommandText = $"SELECT \"Id\" FROM \"{schema}\".\"ModelProviderMappings\" " +
            (providerId.HasValue ? "WHERE \"ProviderId\" = @provider " : string.Empty) +
            "ORDER BY \"ModelAlias\", \"Id\"";
        if (providerId.HasValue) command.Parameters.AddWithValue("provider", providerId.Value);
        var result = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(reader.GetInt32(0));
        return result.ToArray();
    }

    private static async Task AssertSingleSlotPageRecoveryAsync(IModelProviderMappingRuntimeStore store,
        int providerId, bool providerPage, SeedIds ids, CancellationToken token)
    {
        var page = await ReadSingleSlotPageAsync(store, providerId, providerPage, 1, 10, token);
        page.TotalCount.Should().Be(providerPage ? 1 : 2);
        page.Items.Select(row => row.Id).Should().Equal(providerPage
            ? [ids.SecondaryMappingId] : [ids.PrimaryMappingId, ids.SecondaryMappingId]);
    }

    private async Task CancelSingleSlotSqlAsync(string schema, NpgsqlDataSource source,
        IModelProviderMappingRuntimeStore store, SeedIds ids, int providerId, bool providerPage,
        string table, CancellationToken token)
    {
        var readerPid = await AssertSingleSlotReusableAsync(source, token);
        using var caller = new CancellationTokenSource();
        NpgsqlConnection? blocker = null;
        NpgsqlConnection? observer = null;
        NpgsqlTransaction? transaction = null;
        Task? pending = null;
        var released = false;
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            blocker = await OpenSingleSlotObserverAsync(schema, "blocker", token);
            observer = await OpenSingleSlotObserverAsync(schema, "observer", token);
            transaction = await blocker.BeginTransactionAsync(token);
            await LockSingleSlotTableAsync(blocker, transaction, schema, table, token);
            pending = ReadSingleSlotPageAsync(store, providerId, providerPage, 1, 10, caller.Token);
            await WaitForSingleSlotRelationWaitAsync(observer, schema, table, readerPid, blocker.ProcessID, pending, token);
            caller.Cancel();
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(caller.Token, cancellation.CancellationToken);
            // Lock remains held: SELECT1 proves the page command was reaped and
            // the same source's only slot is reusable before table release.
            using (var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await AssertSingleSlotReusableAsync(source, recovery.Token);
            await transaction.RollbackAsync(token);
            released = true;
            await AssertSingleSlotPageRecoveryAsync(store, providerId, providerPage, ids, token);
        }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
            [async cleanup =>
                {
                    caller.Cancel();
                    if (pending is not null) await ReapSingleSlotReadAsync(pending, cleanup);
                },
             async cleanup =>
                {
                    if (transaction is not null && !released) await transaction.RollbackAsync(cleanup);
                },
             async cleanup =>
                {
                    if (blocker is not null) await blocker.DisposeAsync().AsTask().WaitAsync(cleanup);
                },
             async cleanup =>
                {
                    if (pending is not null) await ReapSingleSlotReadAsync(pending, cleanup);
                },
             async cleanup =>
                {
                    if (transaction is not null) await transaction.DisposeAsync().AsTask().WaitAsync(cleanup);
                },
             async cleanup =>
                {
                    if (observer is not null) await observer.DisposeAsync().AsTask().WaitAsync(cleanup);
                }], TimeSpan.FromSeconds(5), "Observed single-slot page SQL owners could not all be drained."),
            "Observed single-slot page SQL cancellation and cleanup failed.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedPagesCompleteAfterObservedCountWithOnePoolSlot(bool providerPage)
    {
        await InSingleSlotSchemaAsync(async (schema, source, ids, token) =>
        {
            var store = new NpgsqlModelProviderMappingRuntimeStore(source);
            var readerPid = await AssertSingleSlotReusableAsync(source, token);
            var providerId = (await store.GetByIdAsync(ids.SecondaryMappingId, token))!.ProviderId;
            using var caller = new CancellationTokenSource();
            NpgsqlConnection? blocker = null;
            NpgsqlConnection? observer = null;
            NpgsqlTransaction? transaction = null;
            Task<ModelProviderMappingRuntimePage>? pending = null;
            var released = false;
            await IntegrationProofCleanup.RunAsync(async () =>
            {
                observer = await OpenSingleSlotObserverAsync(schema, "observer", token);
                blocker = await OpenSingleSlotObserverAsync(schema, "blocker", token);
                transaction = await blocker.BeginTransactionAsync(token);
                await LockSingleSlotTableAsync(blocker, transaction, schema, "ModelProviderMappings", token);
                pending = ReadSingleSlotPageAsync(store, providerId, providerPage, 1, 10, caller.Token);
                await WaitForSingleSlotRelationWaitAsync(observer, schema, "ModelProviderMappings",
                    readerPid, blocker.ProcessID, pending, token);
                _output.WriteLine($"single-slot-count-lock-observed schema={schema} readerPid={readerPid} blockerPid={blocker.ProcessID} providerPage={providerPage}");
                await transaction.RollbackAsync(token);
                released = true;
                ModelProviderMappingRuntimePage page;
                try { page = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    // This query inspects the exact physical slot after its count
                    // finished. It distinguishes the old nested pool acquisition
                    // from a slow or still-blocked PostgreSQL count statement.
                    await using var command = observer.CreateCommand();
                    command.CommandText = """
                        SELECT state = 'idle' AND wait_event_type = 'Client'
                            AND query LIKE 'SELECT count(*) FROM "ModelProviderMappings"%'
                        FROM pg_catalog.pg_stat_activity
                        WHERE pid = @pid AND application_name = @application
                        """;
                    command.Parameters.AddWithValue("pid", readerPid);
                    command.Parameters.AddWithValue("application", schema);
                    var countIdle = await command.ExecuteScalarAsync(token);
                    countIdle.Should().Be(true);
                    pending.IsCompleted.Should().BeFalse();
                    throw new Xunit.Sdk.XunitException(
                        $"Single-slot pagination stalled after observed count wait was released: " +
                        $"providerPage={providerPage}, waiterPid={readerPid}, blockerPid={blocker.ProcessID}, " +
                        "soleBackendIdleAfterCount=true, pagePending=true. Owned cancellation/recovery/cleanup follows.");
                }
                page.TotalCount.Should().Be(providerPage ? 1 : 2);
                page.Items.Select(item => item.Id).Should().Equal(providerPage
                    ? [ids.SecondaryMappingId] : [ids.PrimaryMappingId, ids.SecondaryMappingId]);
            }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
                [async cleanup =>
                    {
                        caller.Cancel();
                        if (pending is not null) await ReapSingleSlotReadAsync(pending, cleanup);
                        _output.WriteLine("single-slot-page-producer-joined-after-cancellation");
                    },
                 async cleanup =>
                    {
                        if (transaction is not null && !released) await transaction.RollbackAsync(cleanup);
                    },
                 async cleanup =>
                    {
                        if (blocker is not null) await blocker.DisposeAsync().AsTask().WaitAsync(cleanup);
                    },
                 async cleanup =>
                    {
                        if (pending is not null) await ReapSingleSlotReadAsync(pending, cleanup);
                    },
                 async cleanup =>
                    {
                        if (transaction is not null) await transaction.DisposeAsync().AsTask().WaitAsync(cleanup);
                    },
                 async cleanup =>
                    {
                        var recoveryPid = await AssertSingleSlotReusableAsync(source, cleanup);
                        _output.WriteLine($"single-slot-select1-recovery readerPid={recoveryPid}");
                    },
                 async cleanup =>
                    {
                        if (observer is not null) await observer.DisposeAsync().AsTask().WaitAsync(cleanup);
                    }],
                TimeSpan.FromSeconds(5), "Single-slot page producer cleanup and slot recovery failed."),
                "Single-slot pagination and producer cleanup failed.");
        });
    }

    private async Task InSingleSlotSchemaAsync(
        Func<string, NpgsqlDataSource, SeedIds, CancellationToken, Task> proof)
    {
        var schema = $"mr_single_{Guid.NewGuid():N}";
        NpgsqlDataSource? source = null;
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            var ids = await CreateAndSeedSchemaAsync(schema, setup.Token);
            source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
            {
                ApplicationName = schema, MinPoolSize = 0, MaxPoolSize = 1,
                Timeout = 30, CommandTimeout = 30, CancellationTimeout = 1000
            }.ConnectionString);
            await proof(schema, source, ids, setup.Token);
        }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
            [async cleanup =>
                {
                    if (source is not null) await source.DisposeAsync().AsTask().WaitAsync(cleanup);
                },
             async cleanup =>
                {
                    await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
                        { Pooling = false, Timeout = 5, CommandTimeout = 5 }.ConnectionString);
                    await connection.OpenAsync(cleanup);
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
                    await command.ExecuteNonQueryAsync(cleanup);
                },
             async cleanup =>
                {
                    await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
                        { Pooling = false, Timeout = 5, CommandTimeout = 5 }.ConnectionString);
                    await connection.OpenAsync(cleanup);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM pg_catalog.pg_namespace WHERE nspname = @schema";
                    command.Parameters.AddWithValue("schema", schema);
                    (await command.ExecuteScalarAsync(cleanup)).Should().Be(0L);
                    _output.WriteLine($"single-slot-owned-schema-absent schema={schema}");
                }], TimeSpan.FromSeconds(5), "Single-slot data source/schema owners could not all be cleaned up."),
            "Single-slot pagination proof and owned cleanup failed.");
    }

    private static Task<ModelProviderMappingRuntimePage> ReadSingleSlotPageAsync(
        IModelProviderMappingRuntimeStore store, int providerId, bool providerPage, int page, int size, CancellationToken token) =>
        providerPage ? store.GetByProviderPaginatedAsync(providerId, page, size, token)
            : store.GetPaginatedAsync(page, size, token);

    private static async Task<int> AssertSingleSlotReusableAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        (await command.ExecuteScalarAsync(token)).Should().Be(1);
        return connection.ProcessID;
    }

    private async Task<NpgsqlConnection> OpenSingleSlotObserverAsync(
        string schema, string role, CancellationToken token)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
            { Pooling = false, ApplicationName = $"mr_{role}_{Guid.NewGuid():N}", Timeout = 5, CommandTimeout = 5 }.ConnectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task LockSingleSlotTableAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string schema, string table, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"LOCK TABLE \"{schema}\".\"{table}\" IN ACCESS EXCLUSIVE MODE";
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task ReapSingleSlotReadAsync(Task pending, CancellationToken token)
    {
        try { await pending.WaitAsync(token); }
        catch (OperationCanceledException) when (pending.IsCanceled) { }
    }

    private static async Task WaitForSingleSlotRelationWaitAsync(NpgsqlConnection observer, string schema,
        string table, int readerPid, int blockerPid, Task pending, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT activity.pid FROM pg_catalog.pg_stat_activity activity
            JOIN pg_catalog.pg_locks waiting ON waiting.pid = activity.pid
            JOIN pg_catalog.pg_locks blocking ON blocking.relation = waiting.relation
                AND blocking.pid = @blocker AND blocking.granted AND blocking.mode = 'AccessExclusiveLock'
            WHERE activity.pid = @reader AND activity.application_name = @application
                AND activity.datname = current_database() AND activity.wait_event_type = 'Lock'
                AND @blocker = ANY(pg_catalog.pg_blocking_pids(activity.pid))
                AND waiting.locktype = 'relation' AND NOT waiting.granted AND waiting.mode = 'AccessShareLock'
                AND waiting.relation = pg_catalog.to_regclass(@relation)
            """;
        command.Parameters.AddWithValue("reader", readerPid);
        command.Parameters.AddWithValue("blocker", blockerPid);
        command.Parameters.AddWithValue("application", schema);
        command.Parameters.AddWithValue("relation", $"\"{schema}\".\"{table}\"");
        while (true)
        {
            if (pending.IsCompleted)
            {
                await pending;
                throw new InvalidOperationException("Single-slot page completed before its exact SQL relation wait was observed.");
            }
            if (await command.ExecuteScalarAsync(deadline.Token) is int observed)
            {
                observed.Should().Be(readerPid);
                return;
            }
            await Task.Delay(25, deadline.Token);
        }
    }
}
