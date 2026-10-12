using AwesomeAssertions;

using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;

using Npgsql;
using NpgsqlTypes;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// Runs the Gateway virtual-key runtime contract against EF and typed Npgsql.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class VirtualKeyRuntimePersistenceParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;

    public VirtualKeyRuntimePersistenceParityTests(PostgresLockTestContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EfAndTypedNpgsqlImplementTheSameRuntimeContract()
    {
        var efSchema = $"vk_runtime_ef_{Guid.NewGuid():N}";
        var npgsqlSchema = $"vk_runtime_np_{Guid.NewGuid():N}";

        try
        {
            var efIds = await CreateAndSeedSchemaAsync(efSchema);
            var npgsqlIds = await CreateAndSeedSchemaAsync(npgsqlSchema);

            await ExerciseContractAsync(
                new EfVirtualKeyRuntimeStore(new TestDbContextFactory(WithSearchPath(efSchema))),
                efIds);

            await using var dataSource = NpgsqlDataSource.Create(WithSearchPath(npgsqlSchema));
            await ExerciseContractAsync(new NpgsqlVirtualKeyRuntimeStore(dataSource), npgsqlIds);
        }
        finally
        {
            await DropSchemaAsync(efSchema);
            await DropSchemaAsync(npgsqlSchema);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeReadsPreserveCompleteDisabledAndNullSnapshotsAndTouchIsolation(bool typedNpgsql)
    {
        var schema = $"vk_read_matrix_{Guid.NewGuid():N}";
        try
        {
            var ids = await CreateAndSeedSchemaAsync(schema);
            var expected = await SeedRuntimeReadMatrixAsync(schema, ids);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var store = CreateRuntimeMatrixStore(WithSearchPath(schema), source, typedNpgsql);

            foreach (var invalidHash in new[] { null, "", " \t\r\n" })
                await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetByHashAsync(invalidHash!));
            (await store.GetByHashAsync("absent:runtime-key")).Should().BeNull();
            (await store.GetByIdAsync(int.MinValue)).Should().BeNull();
            foreach (var row in expected)
            {
                AssertRuntimeMatrixSnapshot(await store.GetByHashAsync(row.KeyHash), row);
                AssertRuntimeMatrixSnapshot(await store.GetByIdAsync(row.Id), row);
            }
            (await store.GetKeyHashesByGroupIdAsync(ids.GroupId)).Should()
                .Equal("sha256:test-key", "aaa:disabled"); // ID order differs from hash order.
            (await store.GetKeyHashesByGroupIdAsync(expected[2].VirtualKeyGroupId)).Should()
                .Equal("sha256:foreign");
            (await store.GetKeyHashesByGroupIdAsync(int.MinValue)).Should().BeEmpty();

            var detached = (await store.GetByHashAsync(expected[0].KeyHash))!;
            detached.RowVersion![0] = 255;
            detached.Group.RowVersion![0] = 254;
            var fresh = (await store.GetByIdAsync(detached.Id))!;
            fresh.Should().NotBeSameAs(detached);
            fresh.Group.Should().NotBeSameAs(detached.Group);
            AssertRuntimeMatrixSnapshot(fresh, expected[0]);
            AssertRuntimeMatrixSnapshot(await store.GetByHashAsync(detached.KeyHash), expected[0]);
            AssertRuntimeMatrixSnapshot(await store.GetByIdAsync(expected[1].Id), expected[1]);

            // Touch accepts an older caller timestamp and includes disabled keys.
            var touchedAt = RuntimeMatrixInstant.AddDays(-1);
            (await store.TouchAsync(expected[1].Id, touchedAt)).Should().BeTrue();
            (await store.TouchAsync(int.MinValue, touchedAt)).Should().BeFalse();
            AssertRuntimeMatrixSnapshot(await store.GetByIdAsync(expected[1].Id), expected[1], touchedAt);
            AssertRuntimeMatrixSnapshot(await store.GetByHashAsync(expected[1].KeyHash), expected[1], touchedAt);
            AssertRuntimeMatrixSnapshot(await store.GetByIdAsync(expected[0].Id), expected[0]);
            AssertRuntimeMatrixSnapshot(await store.GetByIdAsync(expected[2].Id), expected[2]);
            (await store.GetKeyHashesByGroupIdAsync(ids.GroupId)).Should()
                .Equal("sha256:test-key", "aaa:disabled");
            (await ReadRuntimeMatrixLedgerCountAsync(WithSearchPath(schema), CancellationToken.None)).Should().Be(0);
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllRuntimeMethodsCancelBeforeExecutionAndDuringPoolAndObservedSqlWaitsAndReuseSlots(bool typedNpgsql)
    {
        var schema = $"vk_cancellation_{Guid.NewGuid():N}";
        try
        {
            var ids = await CreateAndSeedSchemaAsync(schema);
            await SeedRuntimeReadMatrixAsync(schema, ids);
            foreach (var operation in Enum.GetValues<RuntimeCancellationOperation>())
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var applicationName = $"{schema}_{operation}";
                var connectionString = new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
                    { ApplicationName = applicationName, MaxPoolSize = 1 }.ConnectionString;
                await using var source = NpgsqlDataSource.Create(connectionString);
                var store = CreateRuntimeMatrixStore(connectionString, source, typedNpgsql);
                var baseline = (await store.GetByIdAsync(ids.KeyId, deadline.Token))!;
                var ledgerCount = await ReadRuntimeMatrixLedgerCountAsync(connectionString, deadline.Token);

                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        InvokeRuntimeCancellationOperationAsync(store, ids, operation, cancelled.Token));
                    if (operation == RuntimeCancellationOperation.Hash)
                    {
                        foreach (var invalidHash in new[] { null, "", " \t\r\n" })
                            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetByHashAsync(invalidHash!, cancelled.Token));
                        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AdjustBalanceAsync(null!, cancelled.Token));
                        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AdjustBalanceAsync(
                            new(0, 1m, null, null, VirtualKeyBalanceReferenceType.System), cancelled.Token));
                        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AdjustBalanceAsync(
                            new(ids.GroupId, 1m, null, null, VirtualKeyBalanceReferenceType.System, IdempotencyKey: " \t"), cancelled.Token));
                        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AdjustBalanceAsync(
                            new(ids.GroupId, 0.000000001m, null, null, VirtualKeyBalanceReferenceType.System, IdempotencyKey: "too-precise"), cancelled.Token));
                    }
                }
                await AssertRuntimeCancellationHadNoEffectsAsync(store, connectionString, baseline, ledgerCount, deadline.Token);

                await using (var held = typedNpgsql
                    ? await source.OpenConnectionAsync(deadline.Token)
                    : new NpgsqlConnection(connectionString))
                {
                    if (!typedNpgsql) await held.OpenAsync(deadline.Token);
                    using var cancelled = new CancellationTokenSource();
                    var pending = InvokeRuntimeCancellationOperationAsync(store, ids, operation, cancelled.Token);
                    try
                    {
                        pending.IsCompleted.Should().BeFalse();
                        cancelled.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                    finally { await DrainRuntimeCancellationAsync(pending, cancelled); }
                }
                await AssertRuntimeCancellationHadNoEffectsAsync(store, connectionString, baseline, ledgerCount, deadline.Token);
                await AssertRuntimeOperationRecoversAsync(store, ids, operation, baseline, deadline.Token);
                baseline = (await store.GetByIdAsync(ids.KeyId, deadline.Token))!;
                ledgerCount = await ReadRuntimeMatrixLedgerCountAsync(connectionString, deadline.Token);

                await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                    { Pooling = false, ApplicationName = $"{applicationName}_blocker" }.ConnectionString);
                await blocker.OpenAsync(deadline.Token);
                await using var transaction = await blocker.BeginTransactionAsync(deadline.Token);
                try
                {
                    await using (var command = blocker.CreateCommand())
                    {
                        command.Transaction = transaction;
                        // Block ledger insertion after the balance update so cancellation must roll it back.
                        var table = operation == RuntimeCancellationOperation.Adjust ? "VirtualKeyGroupTransactions" : "VirtualKeys";
                        command.CommandText = $"LOCK TABLE \"{schema}\".\"{table}\" IN ACCESS EXCLUSIVE MODE";
                        await command.ExecuteNonQueryAsync(deadline.Token);
                    }
                    using var cancelled = new CancellationTokenSource();
                    var pending = InvokeRuntimeCancellationOperationAsync(store, ids, operation, cancelled.Token);
                    try
                    {
                        await WaitForRuntimeLockWaitAsync(connectionString, applicationName, blocker.ProcessID, pending, deadline.Token);
                        cancelled.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                    finally { await DrainRuntimeCancellationAsync(pending, cancelled); }
                }
                finally
                {
                    await transaction.RollbackAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                }
                await AssertRuntimeCancellationHadNoEffectsAsync(store, connectionString, baseline, ledgerCount, deadline.Token);
                await AssertRuntimeOperationRecoversAsync(store, ids, operation, baseline, deadline.Token);
            }
        }
        finally { await DropSchemaAsync(schema); }
    }

    // PostgreSQL timestamps retain microseconds; integer ticks pin the exact oracle.
    private static readonly DateTime RuntimeMatrixInstant =
        new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);

    private enum RuntimeCancellationOperation { Hash, Id, Hashes, Touch, Adjust }

    private static IVirtualKeyRuntimeStore CreateRuntimeMatrixStore(
        string connectionString, NpgsqlDataSource source, bool typedNpgsql) => typedNpgsql
            ? new NpgsqlVirtualKeyRuntimeStore(source)
            : new EfVirtualKeyRuntimeStore(new TestDbContextFactory(connectionString));

    private static Task InvokeRuntimeCancellationOperationAsync(
        IVirtualKeyRuntimeStore store, SeedIds ids, RuntimeCancellationOperation operation, CancellationToken token) => operation switch
    {
        RuntimeCancellationOperation.Hash => store.GetByHashAsync("sha256:test-key", token),
        RuntimeCancellationOperation.Id => store.GetByIdAsync(ids.KeyId, token),
        RuntimeCancellationOperation.Hashes => store.GetKeyHashesByGroupIdAsync(ids.GroupId, token),
        RuntimeCancellationOperation.Touch => store.TouchAsync(ids.KeyId, RuntimeMatrixInstant.AddHours(3), token),
        RuntimeCancellationOperation.Adjust => store.AdjustBalanceAsync(
            new(ids.GroupId, -0.00000001m, "cancellation recovery", "Test", VirtualKeyBalanceReferenceType.System), token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static async Task AssertRuntimeOperationRecoversAsync(
        IVirtualKeyRuntimeStore store, SeedIds ids, RuntimeCancellationOperation operation,
        VirtualKeyRuntimeRecord baseline, CancellationToken token)
    {
        switch (operation)
        {
            case RuntimeCancellationOperation.Hash:
                AssertRuntimeMatrixSnapshot(await store.GetByHashAsync(baseline.KeyHash, token), baseline);
                break;
            case RuntimeCancellationOperation.Id:
                AssertRuntimeMatrixSnapshot(await store.GetByIdAsync(ids.KeyId, token), baseline);
                break;
            case RuntimeCancellationOperation.Hashes:
                (await store.GetKeyHashesByGroupIdAsync(ids.GroupId, token)).Should().Equal("sha256:test-key", "aaa:disabled");
                break;
            case RuntimeCancellationOperation.Touch:
                var touchedAt = RuntimeMatrixInstant.AddHours(2);
                (await store.TouchAsync(ids.KeyId, touchedAt, token)).Should().BeTrue();
                AssertRuntimeMatrixSnapshot(await store.GetByIdAsync(ids.KeyId, token), baseline, touchedAt);
                break;
            case RuntimeCancellationOperation.Adjust:
                (await store.AdjustBalanceAsync(new(ids.GroupId, -0.00000001m, "cancellation recovery", "Test",
                    VirtualKeyBalanceReferenceType.System), token)).Should().Be(new VirtualKeyBalanceAdjustmentResult(
                        baseline.Group.Balance - 0.00000001m, baseline.Group.LifetimeSpent + 0.00000001m, true));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static async Task AssertRuntimeCancellationHadNoEffectsAsync(
        IVirtualKeyRuntimeStore store, string connectionString, VirtualKeyRuntimeRecord baseline,
        long ledgerCount, CancellationToken token)
    {
        AssertRuntimeMatrixSnapshot(await store.GetByIdAsync(baseline.Id, token), baseline);
        (await ReadRuntimeMatrixLedgerCountAsync(connectionString, token)).Should().Be(ledgerCount);
    }

    private static async Task DrainRuntimeCancellationAsync(Task pending, CancellationTokenSource cancelled)
    {
        cancelled.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    }

    private static async Task WaitForRuntimeLockWaitAsync(
        string connectionString, string applicationName, int blockerPid, Task pending, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = $"{applicationName}_observer" }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_stat_activity
                WHERE application_name = @application AND datname = current_database()
                    AND wait_event_type = 'Lock' AND @blocker = ANY(pg_catalog.pg_blocking_pids(pid)))
            """;
        command.Parameters.AddWithValue("application", applicationName);
        command.Parameters.AddWithValue("blocker", blockerPid);
        while (true)
        {
            if (pending.IsCompleted)
            {
                await pending;
                throw new InvalidOperationException("Virtual-key operation completed before its lock wait was observed.");
            }
            if (await command.ExecuteScalarAsync(token) is true) return;
            await Task.Delay(25, token);
        }
    }

    private static async Task<long> ReadRuntimeMatrixLedgerCountAsync(string connectionString, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM \"VirtualKeyGroupTransactions\"";
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    private static void AssertRuntimeMatrixSnapshot(
        VirtualKeyRuntimeRecord? actual, VirtualKeyRuntimeRecord expected, DateTime? expectedUpdatedAt = null)
    {
        actual.Should().NotBeNull();
        actual!.Should().BeEquivalentTo(expected, options => options
            .Excluding(key => key.ModelRateLimits).Excluding(key => key.UpdatedAt));
        actual.UpdatedAt.Should().Be(expectedUpdatedAt ?? expected.UpdatedAt);
        actual.Group.UpdatedAt.Should().Be(expected.Group.UpdatedAt);
        actual.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        actual.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
        actual.Group.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        actual.Group.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
        if (actual.ExpiresAt.HasValue) actual.ExpiresAt.Value.Kind.Should().Be(DateTimeKind.Utc);
        if (expected.ModelRateLimits is null) actual.ModelRateLimits.Should().BeNull();
        else
        {
            using var actualJson = JsonDocument.Parse(actual.ModelRateLimits!);
            using var expectedJson = JsonDocument.Parse(expected.ModelRateLimits);
            JsonElement.DeepEquals(actualJson.RootElement, expectedJson.RootElement).Should().BeTrue();
        }
    }

    private async Task<VirtualKeyRuntimeRecord[]> SeedRuntimeReadMatrixAsync(string schema, SeedIds ids)
    {
        await using var connection = new NpgsqlConnection(WithSearchPath(schema));
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE "VirtualKeyGroups" SET "ExternalGroupId" = 'external-runtime',
                    "Balance" = 123.45678901, "LifetimeCreditsAdded" = 124.00000001,
                    "LifetimeSpent" = 0.54321100, "CreatedAt" = @created, "UpdatedAt" = @updated,
                    "MediaRetentionPolicyId" = 73, "RateLimitRpm" = 1000, "RateLimitRpd" = 10000,
                    "RateLimitTpm" = 90000, "MaxParallelRequests" = 20
                WHERE "Id" = @groupId;
                """;
            command.Parameters.AddWithValue("created", RuntimeMatrixInstant);
            command.Parameters.AddWithValue("updated", RuntimeMatrixInstant.AddMinutes(1));
            command.Parameters.AddWithValue("groupId", ids.GroupId);
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE "VirtualKeys" SET "Description" = 'populated runtime description',
                    "ExpiresAt" = @expires, "CreatedAt" = @created, "UpdatedAt" = @updated,
                    "RateLimitRpd" = 1234, "RateLimitPriority" = -17
                WHERE "Id" = @keyId;
                """;
            command.Parameters.AddWithValue("expires", RuntimeMatrixInstant.AddDays(1));
            command.Parameters.AddWithValue("created", RuntimeMatrixInstant.AddSeconds(3));
            command.Parameters.AddWithValue("updated", RuntimeMatrixInstant.AddMinutes(2));
            command.Parameters.AddWithValue("keyId", ids.KeyId);
            await command.ExecuteNonQueryAsync();
        }
        int foreignGroupId;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO "VirtualKeyGroups" ("GroupName", "Balance", "LifetimeCreditsAdded",
                    "LifetimeSpent", "CreatedAt", "UpdatedAt")
                VALUES ('null runtime group', -0.00000001, 25.00000001, 25.00000002, @created, @updated)
                RETURNING "Id";
                """;
            command.Parameters.AddWithValue("created", RuntimeMatrixInstant.AddSeconds(4));
            command.Parameters.AddWithValue("updated", RuntimeMatrixInstant.AddMinutes(3));
            foreignGroupId = Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        var disabledId = await InsertRuntimeMatrixKeyAsync(connection, ids.GroupId, "disabled runtime key", "aaa:disabled", false);
        var foreignId = await InsertRuntimeMatrixKeyAsync(connection, foreignGroupId, "foreign runtime key", "sha256:foreign", true);
        var populatedGroup = new VirtualKeyGroupRuntimeRecord
        {
            Id = ids.GroupId, ExternalGroupId = "external-runtime", GroupName = "runtime group",
            Balance = 123.45678901m, LifetimeCreditsAdded = 124.00000001m, LifetimeSpent = 0.54321100m,
            CreatedAt = RuntimeMatrixInstant, UpdatedAt = RuntimeMatrixInstant.AddMinutes(1), MediaRetentionPolicyId = 73,
            RateLimitRpm = 1000, RateLimitRpd = 10000, RateLimitTpm = 90000, MaxParallelRequests = 20, RowVersion = [9, 8]
        };
        var nullGroup = new VirtualKeyGroupRuntimeRecord
        {
            Id = foreignGroupId, GroupName = "null runtime group", Balance = -0.00000001m,
            LifetimeCreditsAdded = 25.00000001m, LifetimeSpent = 25.00000002m,
            CreatedAt = RuntimeMatrixInstant.AddSeconds(4), UpdatedAt = RuntimeMatrixInstant.AddMinutes(3)
        };
        return
        [
            new VirtualKeyRuntimeRecord
            {
                Id = ids.KeyId, KeyName = "runtime key", KeyHash = "sha256:test-key",
                Description = "populated runtime description", IsEnabled = true, VirtualKeyGroupId = ids.GroupId,
                ExpiresAt = RuntimeMatrixInstant.AddDays(1), CreatedAt = RuntimeMatrixInstant.AddSeconds(3),
                UpdatedAt = RuntimeMatrixInstant.AddMinutes(2), Metadata = "{\"tenant\":\"acme\"}",
                AllowedModels = "gpt-5,claude-sonnet", RateLimitRpm = 120, RateLimitRpd = 1234,
                RateLimitTpm = 50000, MaxParallelRequests = 4, RateLimitPriority = -17,
                ModelRateLimits = """{"gpt-5":{"rpm":5}}""", RowVersion = [1, 2, 3], Group = populatedGroup
            },
            new VirtualKeyRuntimeRecord
            {
                Id = disabledId, KeyName = "disabled runtime key", KeyHash = "aaa:disabled", IsEnabled = false,
                VirtualKeyGroupId = ids.GroupId, CreatedAt = RuntimeMatrixInstant.AddSeconds(5),
                UpdatedAt = RuntimeMatrixInstant.AddMinutes(4), Group = populatedGroup
            },
            new VirtualKeyRuntimeRecord
            {
                Id = foreignId, KeyName = "foreign runtime key", KeyHash = "sha256:foreign", IsEnabled = true,
                VirtualKeyGroupId = foreignGroupId, CreatedAt = RuntimeMatrixInstant.AddSeconds(5),
                UpdatedAt = RuntimeMatrixInstant.AddMinutes(4), Group = nullGroup
            }
        ];
    }

    private static async Task<int> InsertRuntimeMatrixKeyAsync(
        NpgsqlConnection connection, int groupId, string keyName, string keyHash, bool enabled)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "VirtualKeys" ("KeyName", "KeyHash", "IsEnabled", "VirtualKeyGroupId", "CreatedAt", "UpdatedAt")
            VALUES (@name, @hash, @enabled, @groupId, @created, @updated) RETURNING "Id";
            """;
        command.Parameters.AddWithValue("name", keyName);
        command.Parameters.AddWithValue("hash", keyHash);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("groupId", groupId);
        command.Parameters.AddWithValue("created", RuntimeMatrixInstant.AddSeconds(5));
        command.Parameters.AddWithValue("updated", RuntimeMatrixInstant.AddMinutes(4));
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task ExerciseContractAsync(
        IVirtualKeyRuntimeStore store,
        SeedIds ids)
    {
        (await store.GetByHashAsync("missing")).Should().BeNull();
        (await store.GetByIdAsync(-1)).Should().BeNull();

        var key = await store.GetByHashAsync("sha256:test-key");
        key.Should().NotBeNull();
        key!.Id.Should().Be(ids.KeyId);
        key.KeyName.Should().Be("runtime key");
        key.Description.Should().BeNull();
        key.IsEnabled.Should().BeTrue();
        key.AllowedModels.Should().Be("gpt-5,claude-sonnet");
        key.Metadata.Should().Be("{\"tenant\":\"acme\"}");
        using (var modelLimits = JsonDocument.Parse(key.ModelRateLimits!))
        {
            modelLimits.RootElement.GetProperty("gpt-5").GetProperty("rpm").GetInt32()
                .Should().Be(5);
        }
        key.RateLimitRpm.Should().Be(120);
        key.RateLimitRpd.Should().BeNull();
        key.RateLimitTpm.Should().Be(50_000);
        key.MaxParallelRequests.Should().Be(4);
        key.RateLimitPriority.Should().Be(2);
        key.RowVersion.Should().Equal([1, 2, 3]);
        key.Group.Id.Should().Be(ids.GroupId);
        key.Group.GroupName.Should().Be("runtime group");
        key.Group.ExternalGroupId.Should().BeNull();
        key.Group.Balance.Should().Be(100m);
        key.Group.LifetimeCreditsAdded.Should().Be(100m);
        key.Group.RateLimitRpm.Should().Be(1_000);
        key.Group.RateLimitRpd.Should().Be(10_000);
        key.Group.RateLimitTpm.Should().BeNull();
        key.Group.MaxParallelRequests.Should().Be(20);

        (await store.GetByIdAsync(ids.KeyId))!.KeyHash.Should().Be("sha256:test-key");
        (await store.GetKeyHashesByGroupIdAsync(ids.GroupId)).Should()
            .Equal("sha256:test-key");
        (await store.GetKeyHashesByGroupIdAsync(-1)).Should().BeEmpty();
        var touchedAt = DateTime.UtcNow.AddMinutes(1);
        (await store.TouchAsync(ids.KeyId, touchedAt)).Should().BeTrue();
        (await store.TouchAsync(-1, touchedAt)).Should().BeFalse();
        (await store.GetByIdAsync(ids.KeyId))!.UpdatedAt.Should().BeCloseTo(
            touchedAt,
            TimeSpan.FromMilliseconds(1));

        var debit = await store.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
            ids.GroupId,
            -2.5m,
            "request charge",
            "Gateway",
            VirtualKeyBalanceReferenceType.VirtualKey,
            ids.KeyId.ToString(),
            BillingWindowStartUtc: new DateTime(2026, 8, 27, 20, 0, 0, DateTimeKind.Utc)));
        debit.Should().Be(new VirtualKeyBalanceAdjustmentResult(97.5m, 2.5m, true));

        var idempotent = new VirtualKeyBalanceAdjustment(
            ids.GroupId,
            -1.25m,
            "idempotent charge",
            "Gateway",
            VirtualKeyBalanceReferenceType.VirtualKey,
            ids.KeyId.ToString(),
            "spend:request-1",
            new DateTime(2026, 8, 27, 21, 0, 0, DateTimeKind.Utc));
        (await store.AdjustBalanceAsync(idempotent)).Should()
            .Be(new VirtualKeyBalanceAdjustmentResult(96.25m, 3.75m, true));
        (await store.AdjustBalanceAsync(idempotent)).Should()
            .Be(new VirtualKeyBalanceAdjustmentResult(96.25m, 3.75m, false));

        var conflict = idempotent with { Amount = -9m };
        await Assert.ThrowsAsync<VirtualKeyBalanceConflictException>(
            () => store.AdjustBalanceAsync(conflict));

        var concurrent = Enumerable.Range(0, 8)
            .Select(index => store.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
                ids.GroupId,
                -0.5m,
                $"concurrent {index}",
                "Gateway",
                VirtualKeyBalanceReferenceType.VirtualKey,
                ids.KeyId.ToString())))
            .ToArray();
        var concurrentResults = await Task.WhenAll(concurrent);
        concurrentResults.Should().OnlyContain(result => result.Applied);
        concurrentResults.Select(result => result.NewBalance).Distinct().Should().HaveCount(8);

        var sameDelivery = new VirtualKeyBalanceAdjustment(
            ids.GroupId,
            -0.75m,
            "redelivered",
            "Gateway",
            VirtualKeyBalanceReferenceType.VirtualKey,
            ids.KeyId.ToString(),
            "spend:concurrent-delivery");
        var deliveryResults = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => store.AdjustBalanceAsync(sameDelivery)));
        deliveryResults.Count(result => result.Applied).Should().Be(1);

        var credit = await store.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
            ids.GroupId,
            10m,
            null,
            null,
            VirtualKeyBalanceReferenceType.Manual));
        credit.NewBalance.Should().Be(101.5m);
        credit.LifetimeSpent.Should().Be(8.5m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
                999_999,
                -1m,
                null,
                null,
                VirtualKeyBalanceReferenceType.System)));
    }

    private async Task<SeedIds> CreateAndSeedSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."VirtualKeyGroups" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "ExternalGroupId" character varying(100) NULL,
                "GroupName" character varying(100) NOT NULL,
                "Balance" numeric(19,8) NOT NULL,
                "LifetimeCreditsAdded" numeric(19,8) NOT NULL,
                "LifetimeSpent" numeric(19,8) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "MediaRetentionPolicyId" integer NULL,
                "RateLimitRpm" integer NULL,
                "RateLimitRpd" integer NULL,
                "RateLimitTpm" integer NULL,
                "MaxParallelRequests" integer NULL,
                "RowVersion" bytea NULL
            );
            CREATE TABLE "{schema}"."VirtualKeys" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "KeyName" character varying(100) NOT NULL,
                "KeyHash" character varying(128) NOT NULL,
                "Description" character varying(500) NULL,
                "IsEnabled" boolean NOT NULL,
                "VirtualKeyGroupId" integer NOT NULL REFERENCES "{schema}"."VirtualKeyGroups" ("Id") ON DELETE RESTRICT,
                "ExpiresAt" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "Metadata" text NULL,
                "AllowedModels" text NULL,
                "RateLimitRpm" integer NULL,
                "RateLimitRpd" integer NULL,
                "RateLimitTpm" integer NULL,
                "MaxParallelRequests" integer NULL,
                "RateLimitPriority" integer NULL,
                "ModelRateLimits" jsonb NULL,
                "RowVersion" bytea NULL
            );
            CREATE UNIQUE INDEX "IX_VK_KeyHash" ON "{schema}"."VirtualKeys" ("KeyHash");
            CREATE TABLE "{schema}"."VirtualKeyGroupTransactions" (
                "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "VirtualKeyGroupId" integer NOT NULL REFERENCES "{schema}"."VirtualKeyGroups" ("Id") ON DELETE CASCADE,
                "TransactionType" integer NOT NULL,
                "Amount" numeric(20,8) NOT NULL,
                "OriginalAmount" numeric(20,8) NULL,
                "OriginalAmountVerifiedBy" character varying(100) NULL,
                "OriginalAmountEvidence" character varying(500) NULL,
                "OriginalAmountVerifiedAtUtc" timestamp with time zone NULL,
                "BalanceAfter" numeric(20,8) NOT NULL,
                "ReferenceType" integer NOT NULL,
                "ReferenceId" character varying(100) NULL,
                "Description" character varying(500) NULL,
                "InitiatedBy" character varying(50) NOT NULL,
                "InitiatedByUserId" character varying(100) NULL,
                "IdempotencyKey" character varying(100) NULL,
                "BillingWindowStartUtc" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "IsDeleted" boolean NOT NULL,
                "DeletedAt" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX "IX_VKGT_IdempotencyKey"
                ON "{schema}"."VirtualKeyGroupTransactions" ("IdempotencyKey")
                WHERE "IdempotencyKey" IS NOT NULL;

            INSERT INTO "{schema}"."VirtualKeyGroups" (
                "ExternalGroupId", "GroupName", "Balance", "LifetimeCreditsAdded",
                "LifetimeSpent", "CreatedAt", "UpdatedAt", "MediaRetentionPolicyId",
                "RateLimitRpm", "RateLimitRpd", "RateLimitTpm", "MaxParallelRequests",
                "RowVersion")
            VALUES (
                NULL, 'runtime group', 100, 100, 0, now(), now(), NULL,
                1000, 10000, NULL, 20, decode('0908', 'hex'))
            RETURNING "Id";
            """;
        var groupId = Convert.ToInt32(await command.ExecuteScalarAsync());

        command.Parameters.Clear();
        command.CommandText = $"""
            INSERT INTO "{schema}"."VirtualKeys" (
                "KeyName", "KeyHash", "Description", "IsEnabled", "VirtualKeyGroupId",
                "ExpiresAt", "CreatedAt", "UpdatedAt", "Metadata", "AllowedModels",
                "RateLimitRpm", "RateLimitRpd", "RateLimitTpm", "MaxParallelRequests",
                "RateLimitPriority", "ModelRateLimits", "RowVersion")
            VALUES (
                'runtime key', 'sha256:test-key', NULL, true, @groupId,
                now() + interval '1 day', now(), now(), @metadata,
                'gpt-5,claude-sonnet', 120, NULL, 50000, 4, 2,
                @modelRateLimits, decode('010203', 'hex'))
            RETURNING "Id";
            """;
        command.Parameters.AddWithValue("groupId", groupId);
        command.Parameters.AddWithValue(
            "metadata",
            NpgsqlDbType.Text,
            """{"tenant":"acme"}""");
        command.Parameters.AddWithValue(
            "modelRateLimits",
            NpgsqlDbType.Jsonb,
            """{"gpt-5":{"rpm":5}}""");
        var keyId = Convert.ToInt32(await command.ExecuteScalarAsync());
        return new SeedIds(groupId, keyId);
    }

    private async Task DropSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    private string WithSearchPath(string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = schema
        };
        return builder.ConnectionString;
    }

    private sealed record SeedIds(int GroupId, int KeyId);

    private sealed class TestDbContextFactory(string connectionString) :
        IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(connectionString)
                .Options;

        public ConduitDbContext CreateDbContext() => new(_options);

        public Task<ConduitDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
