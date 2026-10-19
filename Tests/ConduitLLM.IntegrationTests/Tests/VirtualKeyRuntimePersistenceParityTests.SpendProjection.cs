using System.Collections.Concurrent;
using System.Diagnostics;

using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class VirtualKeyRuntimePersistenceParityTests
{
    [Fact]
    public async Task GetSpendSnapshotAsync_ChangedBalanceMembershipAndMissingRows_RemainFreshAsync()
    {
        var schema = $"vk_spend_projection_{Guid.NewGuid():N}";
        try
        {
            var ids = await CreateAndSeedSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            IVirtualKeySpendSnapshotStore projection = new NpgsqlVirtualKeyRuntimeStore(source);

            async Task AssertProjectionAsync(int keyId, VirtualKeySpendSnapshot? expected)
            {
                using var observed = new SpendProjectionCommands();
                Assert.Equal(expected, await projection.GetSpendSnapshotAsync(keyId));
                var sql = Assert.Single(observed.Statements);
                var select = sql[..sql.IndexOf("FROM", StringComparison.Ordinal)].Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal("SELECT key.\"VirtualKeyGroupId\", key_group.\"Balance\"", string.Join(' ', select));
                Assert.Contains("INNER JOIN \"VirtualKeyGroups\"", sql);
                Assert.DoesNotContain("KeyHash", sql);
                Assert.DoesNotContain("Metadata", sql);
                Assert.DoesNotContain("RowVersion", sql);
            }

            await AssertProjectionAsync(ids.KeyId, new(ids.GroupId, 100m));
            await AssertProjectionAsync(int.MinValue, null);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE "VirtualKeyGroups" SET "Balance" = 1.23456789 WHERE "Id" = @group;
                    UPDATE "VirtualKeys" SET "IsEnabled" = false, "ExpiresAt" = now() - interval '1 day'
                        WHERE "Id" = @key;
                    """;
                command.Parameters.AddWithValue("group", ids.GroupId);
                command.Parameters.AddWithValue("key", ids.KeyId);
                await command.ExecuteNonQueryAsync();
            }
            // Settlement must still find disabled/expired keys; authentication is separate.
            await AssertProjectionAsync(ids.KeyId, new(ids.GroupId, 1.23456789m));

            int movedGroup;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO "VirtualKeyGroups" ("GroupName", "Balance", "LifetimeCreditsAdded",
                        "LifetimeSpent", "CreatedAt", "UpdatedAt")
                    VALUES ('moved group', 27.12345678, 30, 2.87654322, now(), now()) RETURNING "Id";
                    """;
                movedGroup = Convert.ToInt32(await command.ExecuteScalarAsync());
                command.CommandText = "UPDATE \"VirtualKeys\" SET \"VirtualKeyGroupId\" = @group WHERE \"Id\" = @key";
                command.Parameters.AddWithValue("group", movedGroup);
                command.Parameters.AddWithValue("key", ids.KeyId);
                await command.ExecuteNonQueryAsync();
            }
            await AssertProjectionAsync(ids.KeyId, new(movedGroup, 27.12345678m));

            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                // Owned test schema only: an orphan proves the same INNER JOIN
                // missing-group semantics as the complete runtime-key read.
                command.CommandText = """
                    ALTER TABLE "VirtualKeys" DROP CONSTRAINT "VirtualKeys_VirtualKeyGroupId_fkey";
                    UPDATE "VirtualKeys" SET "VirtualKeyGroupId" = @missing WHERE "Id" = @key;
                    """;
                command.Parameters.AddWithValue("missing", int.MaxValue);
                command.Parameters.AddWithValue("key", ids.KeyId);
                await command.ExecuteNonQueryAsync();
            }
            await AssertProjectionAsync(ids.KeyId, null);
            Assert.Null(await new NpgsqlVirtualKeyRuntimeStore(source).GetByIdAsync(ids.KeyId));
            await using (var command = source.CreateCommand("DELETE FROM \"VirtualKeys\" WHERE \"Id\" = @id"))
            {
                command.Parameters.AddWithValue("id", ids.KeyId);
                await command.ExecuteNonQueryAsync();
            }
            await AssertProjectionAsync(ids.KeyId, null);
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task GetSpendSnapshotAsync_CancellationDuringPoolAndSqlWaits_ReusesConnectionAsync()
    {
        var schema = $"vk_spend_cancel_{Guid.NewGuid():N}";
        try
        {
            var ids = await CreateAndSeedSchemaAsync(schema);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var applicationName = schema;
            var connectionString = new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
                { ApplicationName = applicationName, MaxPoolSize = 1 }.ConnectionString;
            await using var source = NpgsqlDataSource.Create(connectionString);
            IVirtualKeySpendSnapshotStore projection = new NpgsqlVirtualKeyRuntimeStore(source);

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => projection.GetSpendSnapshotAsync(ids.KeyId, cancelled.Token));
            }
            await using (var held = await source.OpenConnectionAsync(deadline.Token))
            {
                using var cancelled = new CancellationTokenSource();
                var pending = projection.GetSpendSnapshotAsync(ids.KeyId, cancelled.Token);
                try
                {
                    Assert.False(pending.IsCompleted);
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally { await DrainRuntimeCancellationAsync(pending, cancelled); }
            }
            Assert.Equal(new VirtualKeySpendSnapshot(ids.GroupId, 100m), await projection.GetSpendSnapshotAsync(ids.KeyId, deadline.Token));

            await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                { Pooling = false, ApplicationName = applicationName + "_blocker" }.ConnectionString);
            await blocker.OpenAsync(deadline.Token);
            await using var transaction = await blocker.BeginTransactionAsync(deadline.Token);
            try
            {
                await using (var command = blocker.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $"LOCK TABLE \"{schema}\".\"VirtualKeys\" IN ACCESS EXCLUSIVE MODE";
                    await command.ExecuteNonQueryAsync(deadline.Token);
                }
                using var cancelled = new CancellationTokenSource();
                var pending = projection.GetSpendSnapshotAsync(ids.KeyId, cancelled.Token);
                try
                {
                    await WaitForRuntimeLockWaitAsync(connectionString, applicationName, blocker.ProcessID, pending, deadline.Token);
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally { await DrainRuntimeCancellationAsync(pending, cancelled); }
            }
            finally { await transaction.RollbackAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.Equal(new VirtualKeySpendSnapshot(ids.GroupId, 100m), await projection.GetSpendSnapshotAsync(ids.KeyId, deadline.Token));
        }
        finally { await DropSchemaAsync(schema); }
    }

    private sealed class SpendProjectionCommands : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Activity _parent;
        public ConcurrentQueue<string> Statements { get; } = new();

        public SpendProjectionCommands()
        {
            var traceId = ActivityTraceId.CreateRandom();
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId == traceId && activity.GetTagItem("db.query.text") is string sql)
                        Statements.Enqueue(sql);
                }
            };
            ActivitySource.AddActivityListener(_listener);
            _parent = new Activity("fresh spend projection contract").SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded).Start();
        }

        public void Dispose() { _parent.Dispose(); _listener.Dispose(); }
    }
}
