using System.Diagnostics;
using ConduitLLM.Core.Services;
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
async Task Measure(string name, bool library)
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
}
await Measure("legacy SQL dedicated session", false);
await Measure("library dedicated session with loss monitoring", true);
Console.WriteLine("Package compatibility, hold/release, interop, cancellation, loss and repeated disposal passed");
