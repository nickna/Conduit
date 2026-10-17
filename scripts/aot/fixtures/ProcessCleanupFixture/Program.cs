using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

// This actor is only a synchronized private fixture. It never signals another PID.
if (!OperatingSystem.IsLinux() || args.Length != 2 || args[0] is not ("root" or "child" or "grandchild" or "foreign"))
    return 2;
var role = args[0];
var directory = Path.GetFullPath(args[1]);
if (!directory.StartsWith("/tmp/issue1674-", StringComparison.Ordinal) || !Directory.Exists(directory))
    return 3;
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
var socketPath = Path.Combine(directory, role + ".sock");
listener.Bind(new UnixDomainSocketEndPoint(socketPath));
listener.Listen(1);
using var ownProcess = Process.GetCurrentProcess();
var initial = ReadIdentity(ownProcess.Id);
var sentry = Environment.GetEnvironmentVariable("ISSUE1674_PRIVATE_SENTINEL") ?? "missing";
Console.Out.WriteLine("private fixture stdout " + sentry);
Console.Error.WriteLine("private fixture stderr " + sentry);
Process? descendant = null;
try
{
    if (role == "root") descendant = Start("child", directory);
    Publish(Path.Combine(directory, role + ".json"), new
    {
        kind = "issue1674-actor-ready", role, identity = initial,
        benchmarkKeyPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONDUIT_NATIVE_BENCHMARK_API_KEY")),
        privateOverridePresent = sentry != "missing", stopwatchTicks = Stopwatch.GetTimestamp(), stopwatchFrequency = Stopwatch.Frequency
    });
    while (true)
    {
        using var client = await listener.AcceptAsync(lifetime.Token);
        using var stream = new NetworkStream(client, ownsSocket: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 256, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false, true), 256, leaveOpen: true) { AutoFlush = true };
        var text = await reader.ReadLineAsync(lifetime.Token);
        if (text is null || text.Length > 32) return 4;
        if (text == "ping")
            await writer.WriteLineAsync("ready");
        else if (text == "fork" && role == "child" && descendant is null)
        {
            descendant = Start("grandchild", directory);
            await writer.WriteLineAsync("forked");
        }
        else if (text == "exit")
        {
            await writer.WriteLineAsync("exiting");
            return 0;
        }
        else return 5;
    }
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
{
    return 6;
}
finally
{
    // Deliberately leave the inherited child alive: that is the tested timeline.
    // The independent fixture controller cooperatively exits every known actor.
    descendant?.Dispose();
    try { File.Delete(socketPath); } catch { }
}

static Process Start(string childRole, string directory)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        CreateNoWindow = true
    };
    info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add(childRole);
    info.ArgumentList.Add(directory);
    return Process.Start(info) ?? throw new InvalidOperationException("Fixture child did not start.");
}

static object ReadIdentity(int id)
{
    var stat = File.ReadAllText($"/proc/{id}/stat");
    var open = stat.IndexOf(" (", StringComparison.Ordinal);
    var close = stat.LastIndexOf(") ", StringComparison.Ordinal);
    if (open <= 0 || close <= open || !int.TryParse(stat.AsSpan(0, open), out var statId) || statId != id)
        throw new InvalidOperationException("Invalid fixture stat identity.");
    var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (fields.Length <= 19 || fields[0].Length != 1 || "RSDZTtXxKWPI".IndexOf(fields[0][0]) < 0
        || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
        || parent < 0 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start == 0)
        throw new InvalidOperationException("Invalid fixture stat fields.");
    var stdout = new FileInfo($"/proc/{id}/fd/1").LinkTarget;
    var stderr = new FileInfo($"/proc/{id}/fd/2").LinkTarget;
    if (stdout is null || stderr is null || !stdout.StartsWith("pipe:[", StringComparison.Ordinal) || !stderr.StartsWith("pipe:[", StringComparison.Ordinal))
        throw new InvalidOperationException("Fixture output is not held pipes.");
    return new { id, parentId = parent, startToken = start.ToString(CultureInfo.InvariantCulture), state = fields[0], stdout, stderr };
}

static void Publish(string path, object value)
{
    var temporary = path + ".new";
    var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        output.Write(bytes);
        output.Flush(true);
    }
    File.Move(temporary, path, overwrite: false);
}
