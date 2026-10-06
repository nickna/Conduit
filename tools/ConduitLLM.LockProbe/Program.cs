using System.Diagnostics;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Medallion.Threading.Postgres;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("CONDUIT_LOCK_PROBE_POSTGRES")
    ?? throw new InvalidOperationException("Set CONDUIT_LOCK_PROBE_POSTGRES to an isolated test database.");
var builder = new NpgsqlConnectionStringBuilder(connectionString)
{
    ApplicationName = "conduit-lock-probe", MaxPoolSize = 32, Timeout = 3, CommandTimeout = 3,
};
connectionString = builder.ConnectionString;
var key = new PostgresAdvisoryLockKey(PostgresLockIdentity.GetLockId("probe:ownership"));
PostgresDistributedLock CreateLock(PostgresAdvisoryLockKey id) => new(id, connectionString,
    options => options.UseMultiplexing(false).KeepaliveCadence(TimeSpan.FromSeconds(1)));
void Check(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
}

PostgresDistributedLockProvider CreateProvider() => new(connectionString, NullLogger<PostgresDistributedLockProvider>.Instance);
if (args.FirstOrDefault() == "adapter-contend")
{
    await using var competitor = await CreateProvider().TryAcquireAsync("probe:adapter-process");
    Check(competitor is null, "Independent process acquired production adapter's occupied key");
    Console.WriteLine("Independent process production adapter exclusion passed");
    return;
}

// A separate process can assert exclusion without sharing in-process lock state.
if (args.FirstOrDefault() == "contend")
{
    await using var competitor = await CreateLock(key).TryAcquireAsync();
    Check(competitor is null, "Independent process acquired an occupied key");
    Console.WriteLine("Independent process exclusion passed");
    return;
}

await using var observer = new NpgsqlConnection(connectionString);
await observer.OpenAsync();
Console.WriteLine($"PostgreSQL {observer.PostgreSqlVersion}; runtime {Environment.Version}; Npgsql {typeof(NpgsqlConnection).Assembly.GetName().Version}");

var holder = await CreateLock(key).TryAcquireAsync();
Check(holder is not null, "First acquisition failed");
try
{
    await using var busy = await CreateLock(key).TryAcquireAsync();
    Check(busy is null, "Same-key exclusion failed");
    await using var distinct = await CreateLock(new(PostgresLockIdentity.GetLockId("probe:distinct"))).TryAcquireAsync();
    Check(distinct is not null, "Distinct-key independence failed");
    var wait = Stopwatch.StartNew();
    await using var timedOut = await CreateLock(key).TryAcquireAsync(TimeSpan.FromMilliseconds(150));
    Check(timedOut is null && wait.ElapsedMilliseconds >= 100 && wait.Elapsed < TimeSpan.FromSeconds(3), "Bounded waiting failed");
    using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    try
    {
        await using var unexpected = await CreateLock(key).TryAcquireAsync(TimeSpan.FromSeconds(5), canceled.Token);
        throw new InvalidOperationException("Waiting cancellation did not propagate");
    }
    catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }

    // Raw single-bigint session lock is the legacy SQL path. Both directions matter.
    await using (var legacyAttempt = new NpgsqlCommand("SELECT pg_try_advisory_lock(@id)", observer))
    {
        legacyAttempt.Parameters.AddWithValue("id", PostgresLockIdentity.GetLockId("probe:ownership"));
        Check(await legacyAttempt.ExecuteScalarAsync() is false, "Legacy acquired while library held");
    }
    await Task.Delay(350); // Former lease scaled to 100ms; healthy ownership must survive.
    await using var lateCompetitor = await CreateLock(key).TryAcquireAsync();
    Check(lateCompetitor is null, "Healthy holder expired");

    // Access explicitly activates library loss monitoring.
    var lost = holder!.HandleLostToken;
    var loss = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var registration = lost.Register(() => loss.TrySetResult());
    await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'conduit-lock-probe' AND pid <> pg_backend_pid() AND pid IN (SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND classid::bigint = @high AND objid::bigint = @low AND objsubid = 1)", observer);
    var bits = unchecked((ulong)PostgresLockIdentity.GetLockId("probe:ownership"));
    terminate.Parameters.AddWithValue("high", (long)(bits >> 32));
    terminate.Parameters.AddWithValue("low", (long)(bits & uint.MaxValue));
    var lossWatch = Stopwatch.StartNew();
    await terminate.ExecuteNonQueryAsync();
    await loss.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine($"Ownership loss detected in {lossWatch.ElapsedMilliseconds}ms");
    await using var successor = await CreateLock(key).TryAcquireAsync(TimeSpan.FromSeconds(2));
    Check(successor is not null, "Successor could not acquire after loss");
}
finally
{
    var wasLost = holder!.HandleLostToken.IsCancellationRequested;
    try { await holder.DisposeAsync(); }
    catch (InvalidOperationException) when (wasLost)
    {
        Console.WriteLine("Expected release failure after terminated session; library still closes its connection");
    }
    await holder.DisposeAsync();
}
await using (var legacy = new NpgsqlCommand("SELECT pg_advisory_lock(@id)", observer))
{
    legacy.Parameters.AddWithValue("id", PostgresLockIdentity.GetLockId("probe:ownership"));
    await legacy.ExecuteNonQueryAsync();
    try
    {
        await using var competitor = await CreateLock(key).TryAcquireAsync();
        Check(competitor is null, "Library acquired while legacy held");
    }
    finally
    {
        await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@id)", observer);
        release.Parameters.AddWithValue("id", PostgresLockIdentity.GetLockId("probe:ownership"));
        await release.ExecuteNonQueryAsync();
    }
}

// Matched warmed acquisition/release baseline, including dedicated session occupancy.
async Task<(double Median, double P95)> Measure(string name, bool library)
{
    var samples = new List<double>();
    for (var i = 0; i < 120; i++)
    {
        var watch = Stopwatch.StartNew();
        if (library)
        {
            await using var handle = await CreateLock(key).TryAcquireAsync();
            Check(handle is not null, "Measurement acquisition failed");
            _ = handle!.HandleLostToken;
        }
        else
        {
            await using var session = new NpgsqlConnection(connectionString);
            await session.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@id)", session);
            command.Parameters.AddWithValue("id", PostgresLockIdentity.GetLockId("probe:ownership"));
            Check(await command.ExecuteScalarAsync() is true, "Baseline acquisition failed");
            command.CommandText = "SELECT pg_advisory_unlock(@id)";
            await command.ExecuteNonQueryAsync();
        }
        if (i >= 20) { samples.Add(watch.Elapsed.TotalMicroseconds); }
    }
    samples.Sort();
    Console.WriteLine($"{name}: median={samples[50]:F1}us p95={samples[95]:F1}us samples={samples.Count}");
    return (samples[50], samples[95]);
}
var baseline = await Measure("legacy SQL dedicated session", false);
await Measure("library dedicated session with loss monitoring", true);
Console.WriteLine("Package compatibility, hold/release, interop, cancellation, loss and repeated disposal passed");

await using (var ownership = await CreateProvider().TryAcquireAsync("probe:adapter-process"))
{
    Check(ownership is not null, "Production adapter acquisition failed");
    var childInfo = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        CreateNoWindow = true,
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        childInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ConduitLLM.LockProbe.dll"));
    }
    childInfo.ArgumentList.Add("adapter-contend");
    using var child = Process.Start(childInfo) ?? throw new InvalidOperationException("Could not start independent contender");
    var output = child.StandardOutput.ReadToEndAsync();
    var error = child.StandardError.ReadToEndAsync();
    using var childTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { await child.WaitForExitAsync(childTimeout.Token); }
    finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    Check(child.ExitCode == 0, $"Independent contender failed: {await error}");
    Console.WriteLine(await output);
}

// A deadline cannot release a healthy holder while an uncancellable callback continues.
using (var operationCancellation = new CancellationTokenSource())
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var run = CreateProvider().RunWithOptionalLockAsync("probe:uncooperative", TimeSpan.Zero,
        async (_, _) => { started.TrySetResult(); await finish.Task; return true; }, NullLogger.Instance, operationCancellation.Token);
    try
    {
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        operationCancellation.Cancel();
        await using var competitor = await CreateProvider().TryAcquireAsync("probe:uncooperative");
        Check(competitor is null, "Canceled uncooperative work released healthy ownership early");
    }
    finally
    {
        finish.TrySetResult();
        try { await run; throw new InvalidOperationException("Uncooperative callback reported canceled work as success"); }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested) { }
    }
}

var productionSamples = new List<double>();
var measuredProvider = CreateProvider(); // Hosts register one singleton; exclude repeated configuration parsing.
var slowSamples = new List<(double Acquire, double Release)>();
for (var i = 0; i < 120; i++)
{
    var watch = Stopwatch.StartNew();
    var ownership = await measuredProvider.TryAcquireAsync("probe:adapter-measure");
    var acquiredAt = watch.Elapsed.TotalMicroseconds;
    await using (ownership)
    {
        Check(ownership is not null, "Production measurement acquisition failed");
    }
    if (i >= 20)
    {
        productionSamples.Add(watch.Elapsed.TotalMicroseconds);
        slowSamples.Add((acquiredAt, watch.Elapsed.TotalMicroseconds - acquiredAt));
    }
}
productionSamples.Sort();
Console.WriteLine($"production adapter: median={productionSamples[50]:F1}us p95={productionSamples[95]:F1}us samples=100");
foreach (var sample in slowSamples.OrderByDescending(sample => sample.Acquire + sample.Release).Take(5))
{
    Console.WriteLine($"slow sample: acquire={sample.Acquire:F1}us release={sample.Release:F1}us");
}
var latencyPassed = productionSamples[50] <= baseline.Median * 2 + 1000 && productionSamples[95] <= baseline.P95 * 2 + 1000;

var productionHolder = await CreateProvider().TryAcquireAsync("probe:adapter-loss");
Check(productionHolder is not null, "Production loss holder failed");
try
{
    var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var lossRegistration = productionHolder!.HandleLostToken.Register(() => lost.TrySetResult());
    await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'conduit-distributed-lock' AND pid IN (SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND classid::bigint = @high AND objid::bigint = @low AND objsubid = 1)", observer);
    var bits = unchecked((ulong)PostgresLockIdentity.GetLockId("probe:adapter-loss"));
    terminate.Parameters.AddWithValue("high", (long)(bits >> 32));
    terminate.Parameters.AddWithValue("low", (long)(bits & uint.MaxValue));
    var lossWatch = Stopwatch.StartNew();
    Check(await terminate.ExecuteScalarAsync() is true, "Production session termination failed");
    await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine($"Production ownership loss detected in {lossWatch.ElapsedMilliseconds}ms");
    await using var successor = await CreateProvider().TryAcquireAsync("probe:adapter-loss");
    Check(successor is not null, "Production successor could not acquire after loss");
}
finally { await productionHolder!.DisposeAsync(); await productionHolder.DisposeAsync(); }

var owners = new List<IDistributedLockOwnership>();
try
{
    await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
    {
        var ownership = await CreateProvider().TryAcquireAsync($"probe:pool:{i}");
        Check(ownership is not null, "Concurrent holder acquisition failed");
        lock (owners) { owners.Add(ownership!); } // Track successful holders even if a sibling acquisition fails.
    }));
    await using var heldCount = new NpgsqlCommand("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE l.locktype = 'advisory' AND a.application_name = 'conduit-distributed-lock'", observer);
    Check(Convert.ToInt32(await heldCount.ExecuteScalarAsync()) == 16, "16 holders did not occupy exactly 16 locking sessions");
    await using var connectionCount = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name = 'conduit-distributed-lock'", observer);
    var occupancy = Convert.ToInt32(await connectionCount.ExecuteScalarAsync());
    Check(occupancy <= 32, "Lock pool exceeded its 32-session bound");
    for (var i = 0; i < 100; i++)
    {
        await using var busy = await CreateProvider().TryAcquireAsync("probe:pool:0");
        Check(busy is null, "Repeated contention admitted a competing holder");
    }
    occupancy = Convert.ToInt32(await connectionCount.ExecuteScalarAsync());
    Check(occupancy <= 32, "Contended acquisition pool exceeded its 32-session bound");
    var shutdown = Stopwatch.StartNew();
    await Task.WhenAll(owners.Select(ownership => ownership.DisposeAsync().AsTask()));
    Check(shutdown.Elapsed < TimeSpan.FromSeconds(2), "Cooperative shutdown exceeded 2 seconds");
    Check(Convert.ToInt32(await heldCount.ExecuteScalarAsync()) == 0, "Shutdown leaked a held advisory session");
    Console.WriteLine($"16 holders: 16 locking sessions, {occupancy} pooled sessions (limit 32); 100 busy attempts; shutdown {shutdown.ElapsedMilliseconds}ms; 0 held locks after teardown");
}
finally { await Task.WhenAll(owners.Select(ownership => ownership.DisposeAsync().AsTask())); }
Console.WriteLine("Production adapter, independent process, uncooperative cancellation, resource and shutdown gates passed");
Check(latencyPassed, "Production adapter exceeded predeclared latency regression threshold");
