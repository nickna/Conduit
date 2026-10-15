using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using AwesomeAssertions;

using ConduitLLM.IntegrationTests.Infrastructure;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class NativePersistenceCleanupParityTests(PostgresLockTestContainerFixture fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProcessSuccessRequiresOwnedSchemaRemovalAndPreservesBothFailures(bool blockedDrop, bool failWork)
    {
        var sentinel = $"cleanup_sentinel_{Guid.NewGuid():N}";
        string? schema = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { Pooling = false }.ConnectionString);
        await observer.OpenAsync(deadline.Token);
        Process? process = null;
        Task<string>? error = null;
        NpgsqlTransaction? blocker = null;
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            await ExecuteAsync(observer, $"CREATE SCHEMA \"{sentinel}\"", deadline.Token);
            process = StartProbe(failWork);
            error = process.StandardError.ReadToEndAsync();
            var ready = await process.StandardOutput.ReadLineAsync(deadline.Token);
            ready.Should().StartWith("CLEANUP_FIXTURE_READY:");
            var reportedSchema = ready!["CLEANUP_FIXTURE_READY:".Length..];
            reportedSchema.Should().StartWith(PersistenceProbeLifecycle.SchemaPrefix);
            reportedSchema.Length.Should().Be(PersistenceProbeLifecycle.SchemaPrefix.Length + 32);
            Guid.TryParseExact(reportedSchema[PersistenceProbeLifecycle.SchemaPrefix.Length..], "N", out _).Should().BeTrue();
            schema = reportedSchema;
            var output = process.StandardOutput.ReadToEndAsync();
            if (blockedDrop)
            {
                blocker = await observer.BeginTransactionAsync(deadline.Token);
                await ExecuteAsync(observer, $"LOCK TABLE \"{schema}\".owned IN ACCESS SHARE MODE", deadline.Token);
            }
            var elapsed = Stopwatch.StartNew();
            await process.StandardInput.WriteLineAsync("continue");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync(deadline.Token);
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
            var stderr = await error.WaitAsync(TimeSpan.FromSeconds(5));
            var stdout = await output.WaitAsync(TimeSpan.FromSeconds(5));
            process.ExitCode.Should().Be(blockedDrop || failWork ? 1 : 0);
            if (blockedDrop)
            {
                stderr.Should().Contain($"Cleanup failed for owned schema '{schema}'");
                stdout.Should().NotContain("Cleanup fixture passed.");
            }
            else stderr.Should().NotContain("Cleanup failed");
            if (failWork) stderr.Should().Contain("Injected primary probe failure.");
            else stderr.Should().NotContain("Persistence probe failed:");
            if (!blockedDrop && !failWork) stdout.Should().Contain("Cleanup fixture passed.");
            else stdout.Should().NotContain("Cleanup fixture passed.");
            if (blocker is not null) { await blocker.DisposeAsync(); blocker = null; }
            (await SchemaExistsAsync(observer, schema, deadline.Token)).Should().Be(blockedDrop);
            (await SchemaExistsAsync(observer, sentinel, deadline.Token)).Should().BeTrue();
        }, async () =>
        {
            var cleanupFailures = new List<Exception>();
            async Task AttemptAsync(Func<Task> cleanup)
            {
                try { await cleanup(); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            await AttemptAsync(async () =>
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            });
            if (error is not null) await AttemptAsync(async () => { await error.WaitAsync(TimeSpan.FromSeconds(5)); });
            if (blocker is not null) await AttemptAsync(async () => { await blocker.DisposeAsync(); });
            await AttemptAsync(() => observer.CloseAsync());
            if (schema is not null)
                await AttemptAsync(() => PersistenceProbeLifecycle.CleanupOwnedSchemaAsync(fixture.ConnectionString, schema));
            await AttemptAsync(async () =>
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
                    { Pooling = false, Timeout = 15, CommandTimeout = 15, CancellationTimeout = 1000 }.ConnectionString);
                await connection.OpenAsync(cleanup.Token);
                await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS \"{sentinel}\" CASCADE", cleanup.Token);
            });
            await AttemptAsync(() => { process?.Dispose(); return Task.CompletedTask; });
            if (cleanupFailures.Count > 0) throw new AggregateException("Owned cleanup fixture cleanup failed.", cleanupFailures);
        }, "Owned cleanup fixture work and cleanup failed.");
    }

    [Fact]
    public async Task CleanupOpeningCancellationIsBoundedDespiteUnlimitedDriverTimeouts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        TcpClient? accepted = null;
        var schema = $"{PersistenceProbeLifecycle.SchemaPrefix}{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Database = "owned_fixture", Username = "fixture", Password = "opaque_cleanup_secret",
            Timeout = 0, CommandTimeout = 0, CancellationTimeout = 0, Pooling = false
        }.ConnectionString;
        using var acceptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = listener.AcceptTcpClientAsync(acceptDeadline.Token);
        var passed = false;
        using var diagnostics = new StringWriter();
        var elapsed = Stopwatch.StartNew();
        var run = PersistenceProbeLifecycle.RunAsync(schema, () => Task.CompletedTask,
            () => PersistenceProbeLifecycle.CleanupOwnedSchemaAsync(connectionString, schema, TimeSpan.FromMilliseconds(250)),
            () => passed = true, diagnostics);
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            accepted = await accept;
            (await run.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            passed.Should().BeFalse();
            diagnostics.ToString().Should().Contain($"Cleanup failed for owned schema '{schema}'");
            diagnostics.ToString().Should().NotContain("opaque_cleanup_secret");
        }, async () =>
        {
            accepted?.Dispose();
            listener.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }, "Cleanup opening proof and loopback release failed.");
    }

    [Fact]
    public async Task CleanupRejectsNamesOutsideTheGeneratedOwnedSchemaNamespace()
    {
        foreach (var name in new[] { "public", "conduit_aot_probe_not_a_guid", "conduit_aot_probe_" + Guid.NewGuid().ToString("N") + "\"" })
            await Assert.ThrowsAsync<ArgumentException>(() =>
                PersistenceProbeLifecycle.CleanupOwnedSchemaAsync("invalid connection string", name));
    }

    private Process StartProbe(bool failWork)
    {
        var native = Environment.GetEnvironmentVariable("CONDUIT_PERSISTENCE_PROBE_PATH");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(native) ? "dotnet" : native)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        if (string.IsNullOrWhiteSpace(native))
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Conduit.slnx"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("Could not locate the persistence probe repository.");
            var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.Ordinal) ? "Release" : "Debug";
            start.ArgumentList.Add(Path.Combine(root.FullName, "Tests", "ConduitLLM.PersistenceAotTests", "bin",
                configuration, "net10.0", "ConduitLLM.PersistenceAotTests.dll"));
        }
        else if (!File.Exists(native)) throw new FileNotFoundException("Configured published persistence probe is missing.", native);
        start.ArgumentList.Add("--cleanup-fixture");
        if (failWork) start.ArgumentList.Add("--fail-work");
        start.Environment["DATABASE_URL"] = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { Timeout = 0, CommandTimeout = 0, CancellationTimeout = 0, Pooling = false }.ConnectionString;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned cleanup probe.");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 10 };
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> SchemaExistsAsync(NpgsqlConnection connection, string schema, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regnamespace(@schema) IS NOT NULL", connection);
        command.Parameters.AddWithValue("schema", schema);
        return (bool)(await command.ExecuteScalarAsync(token))!;
    }
}
