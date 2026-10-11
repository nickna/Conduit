using ConduitLLM.Configuration.Data;
using ConduitLLM.SchemaStartupFixtures;

using Npgsql;

namespace ConduitLLM.Tests.Configuration.Data;

[Collection("MigrationEnvironment")]
public sealed class ReleaseMigrationStartupProbeTests
{
    [SkippableFact]
    public async Task MissingStaleCurrentAndActiveRelease_AreGatedWithoutLeakingPoolOrLock()
    {
        var management = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(management), "Requires PostgreSQL database creation rights.");
        var fixture = await SchemaStartupDatabase.CreateAsync(management!, previousSchema: false);
        try
        {
            var options = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
                { Pooling = true, MaxPoolSize = 1, Timeout = 2, CommandTimeout = 2 };
            await using var source = NpgsqlDataSource.Create(options.ConnectionString);
            var probe = new ReleaseMigrationStartupProbe(source);
            Assert.False(await probe.IsReadyAsync());
            await using var control = new NpgsqlConnection(fixture.ConnectionString);
            await control.OpenAsync();
            await using (var history = new NpgsqlCommand("""
                CREATE TABLE "__EFMigrationsHistory" ("MigrationId" text PRIMARY KEY, "ProductVersion" text NOT NULL);
                INSERT INTO "__EFMigrationsHistory" VALUES ('00000000000000_Previous', 'test')
                """, control))
                await history.ExecuteNonQueryAsync();
            Assert.False(await probe.IsReadyAsync());
            await using (var current = new NpgsqlCommand("UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = @current", control))
            {
                current.Parameters.AddWithValue("current", ConduitSchemaVersion.Current);
                await current.ExecuteNonQueryAsync();
            }
            Assert.True(await probe.IsReadyAsync());
            Assert.True(await probe.IsReadyAsync()); // Same single-slot pool remains usable.
            await using (var future = new NpgsqlCommand("UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = '99991231235959_Future'", control))
                await future.ExecuteNonQueryAsync();
            Assert.False(await probe.IsReadyAsync());
            await using (var restore = new NpgsqlCommand("UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = @current", control))
            {
                restore.Parameters.AddWithValue("current", ConduitSchemaVersion.Current);
                await restore.ExecuteNonQueryAsync();
            }

            await using (var acquire = new NpgsqlCommand($"SELECT pg_advisory_lock({ReleaseMigrationLock.Id})", control))
                await acquire.ExecuteNonQueryAsync();
            try { Assert.False(await probe.IsReadyAsync()); }
            finally
            {
                await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({ReleaseMigrationLock.Id})", control);
                await release.ExecuteNonQueryAsync();
            }
            Assert.True(await probe.IsReadyAsync());
            // A completed probe must have released its transaction-level lock;
            // it cannot leave the next release job blocked on a pooled session.
            await using var exclusive = new NpgsqlCommand($"SELECT pg_try_advisory_lock({ReleaseMigrationLock.Id})", control);
            Assert.Equal(true, await exclusive.ExecuteScalarAsync());
            await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({ReleaseMigrationLock.Id})", control);
            await unlock.ExecuteNonQueryAsync();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await SchemaStartupDatabase.DropAsync(management!, fixture.Name, cleanup.Token);
        }
    }

    [SkippableFact]
    public async Task CancelledBlockedHistoryRead_ReleasesTheTransactionLockAndSinglePoolSlot()
    {
        var management = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(management), "Requires PostgreSQL database creation rights.");
        var fixture = await SchemaStartupDatabase.CreateAsync(management!, previousSchema: false);
        try
        {
            var options = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = true, MaxPoolSize = 1 };
            await using var source = NpgsqlDataSource.Create(options.ConnectionString);
            var probe = new ReleaseMigrationStartupProbe(source);
            await using var control = new NpgsqlConnection(fixture.ConnectionString);
            await control.OpenAsync();
            await using (var history = new NpgsqlCommand("""
                CREATE TABLE "__EFMigrationsHistory" ("MigrationId" text PRIMARY KEY, "ProductVersion" text NOT NULL);
                INSERT INTO "__EFMigrationsHistory" VALUES (@current, 'test')
                """, control))
            {
                history.Parameters.AddWithValue("current", ConduitSchemaVersion.Current);
                await history.ExecuteNonQueryAsync();
            }
            await using var blocker = await control.BeginTransactionAsync();
            await using (var tableLock = new NpgsqlCommand("LOCK TABLE \"__EFMigrationsHistory\" IN ACCESS EXCLUSIVE MODE", control))
                await tableLock.ExecuteNonQueryAsync();
            using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = probe.IsReadyAsync(cancelled.Token);
            var sawProbeLock = false;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                await using var exclusive = new NpgsqlCommand($"SELECT pg_try_advisory_lock({ReleaseMigrationLock.Id})", control);
                if (await exclusive.ExecuteScalarAsync() is false) { sawProbeLock = true; break; }
                await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({ReleaseMigrationLock.Id})", control);
                await release.ExecuteNonQueryAsync();
                await Task.Delay(20);
            }
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(sawProbeLock, "Cancellation must interrupt a history read after the transaction lock was acquired.");
            await blocker.RollbackAsync();
            Assert.True(await probe.IsReadyAsync());
            await using var releasedLock = new NpgsqlCommand($"SELECT pg_try_advisory_lock({ReleaseMigrationLock.Id})", control);
            Assert.Equal(true, await releasedLock.ExecuteScalarAsync());
            await using var finalRelease = new NpgsqlCommand($"SELECT pg_advisory_unlock({ReleaseMigrationLock.Id})", control);
            await finalRelease.ExecuteNonQueryAsync();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await SchemaStartupDatabase.DropAsync(management!, fixture.Name, cleanup.Token);
        }
    }
}
