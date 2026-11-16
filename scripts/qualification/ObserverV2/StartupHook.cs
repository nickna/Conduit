using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// The runtime requires this exact public, global type and method name.
public static class StartupHook
{
    private static ObserverServer? _server;
    public static void Initialize()
    {
        var configuredPort = Environment.GetEnvironmentVariable("CONDUIT_OBSERVER_PORT");
        if (string.IsNullOrWhiteSpace(configuredPort)) return;
        if (!int.TryParse(configuredPort, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("CONDUIT_OBSERVER_PORT must be 1..65535.");
        var token = Environment.GetEnvironmentVariable("CONDUIT_OBSERVER_TOKEN");
        if (string.IsNullOrEmpty(token) || token.Length < 16)
            throw new InvalidOperationException("CONDUIT_OBSERVER_TOKEN must contain at least 16 characters.");
        var intervalText = Environment.GetEnvironmentVariable("CONDUIT_OBSERVER_SAMPLE_MS");
        var interval = string.IsNullOrEmpty(intervalText) ? 100 : int.Parse(intervalText);
        if (interval != 0 && interval is < 10 or > 60000)
            throw new InvalidOperationException("Sampling interval must be 0 or 10..60000 ms.");
        _server = new ObserverServer(port, token, interval);
        _server.Start();
    }
}

internal sealed class ObserverServer
{
    // Public configuration API sampled once; this records policy, not live active-heap count.
    private static readonly Dictionary<string, string> GcConfiguration = GC.GetConfigurationVariables()
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal);
    private readonly TcpListener _listener;
    private readonly byte[] _token;
    private readonly int _port;
    private readonly int _interval;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly object _processLock = new();
    private readonly object _windowLock = new();
    private readonly Timer _timer;
    private ActiveWindow? _active;
    private long _requests, _captureCount, _captureTicks, _handlerTicks, _handlerAllocated, _sampleTicks, _sampleCount;

    internal ObserverServer(int port, string token, int interval)
    {
        _port = port;
        _token = Encoding.UTF8.GetBytes(token);
        _interval = interval;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _timer = new Timer(_ => SampleWorkingSet(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal void Start()
    {
        _listener.Start(16);
        new Thread(AcceptLoop) { IsBackground = true, Name = "Conduit observer loopback control" }.Start();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    private void Handle(TcpClient client)
    {
        var started = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                using var stream = client.GetStream();
                var request = ReadHeader(stream);
                if (request is null) { Reply(stream, 400, new("error", Error: "Invalid HTTP header.")); return; }
                var lines = request.Split("\r\n", StringSplitOptions.None);
                var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                { Reply(stream, 400, new("error", Error: "Invalid request line.")); return; }
                var supplied = lines.Skip(1).Select(l => l.Split(':', 2))
                    .Where(p => p.Length == 2 && p[0].Equals("X-Conduit-Observer-Token", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p[1].Trim()).ToArray();
                if (supplied.Length != 1 || !CryptographicOperations.FixedTimeEquals(_token, Encoding.UTF8.GetBytes(supplied[0])))
                { Reply(stream, 401, new("error", Error: "Unauthorized.")); return; }
                if (!first[1].StartsWith('/') || first[1].StartsWith("//", StringComparison.Ordinal))
                { Reply(stream, 400, new("error", Error: "Origin-form target required.")); return; }
                var target = new Uri("http://127.0.0.1" + first[1]);
                var query = Query(target.Query);
                if (first[0] == "GET" && target.AbsolutePath == "/health")
                { Reply(stream, 200, new("ok", Observer: Counters())); return; }
                if (first[0] == "GET" && target.AbsolutePath == "/snapshot")
                {
                    var collect = query.GetValueOrDefault("collect") == "true";
                    if (collect)
                    {
                        lock (_windowLock)
                        {
                            if (_active is not null) { Reply(stream, 409, new("error", Error: "Induced GC is prohibited during an active window.")); return; }
                            var gcStart = Stopwatch.GetTimestamp();
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                            Reply(stream, 200, new("ok", Capture(true, Stopwatch.GetTimestamp() - gcStart), Observer: Counters()));
                        }
                    }
                    else Reply(stream, 200, new("ok", Capture(), WindowId: Volatile.Read(ref _active)?.Id, Observer: Counters()));
                    return;
                }
                if (first[0] == "POST" && target.AbsolutePath == "/begin")
                {
                    var id = query.GetValueOrDefault("id");
                    if (!ValidId(id)) { Reply(stream, 400, new("error", Error: "id must be 1..128 ASCII letters, digits, dots, underscores or hyphens.")); return; }
                    lock (_windowLock)
                    {
                        if (_active is not null) { Reply(stream, 409, new("error", Error: "A window is already active.")); return; }
                        var snapshot = Capture();
                        _active = new ActiveWindow(id!, snapshot);
                        if (_interval > 0) _timer.Change(_interval, _interval);
                        Reply(stream, 200, new("ok", snapshot, WindowId: id, Observer: Counters()));
                    }
                    return;
                }
                if (first[0] == "POST" && target.AbsolutePath == "/end")
                {
                    lock (_windowLock)
                    {
                        if (_active is null || query.GetValueOrDefault("id") != _active.Id)
                        { Reply(stream, 409, new("error", Error: "Matching active window required.")); return; }
                        _timer.Change(Timeout.Infinite, Timeout.Infinite);
                        var end = Capture();
                        var start = _active.Start;
                        var window = new WindowMeasurement(_active.Id, start, end,
                            Math.Max(_active.PeakWorkingSetBytes, end.WorkingSetBytes), _active.PeriodicSamples, _interval,
                            new WindowDeltas(end.StopwatchTimestamp - start.StopwatchTimestamp,
                                (end.StopwatchTimestamp - start.StopwatchTimestamp) / (double)Stopwatch.Frequency,
                                end.TotalCpuMilliseconds - start.TotalCpuMilliseconds,
                                end.TotalAllocatedBytes - start.TotalAllocatedBytes,
                                end.Gen0Collections - start.Gen0Collections, end.Gen1Collections - start.Gen1Collections,
                                end.Gen2Collections - start.Gen2Collections, end.TotalGcPauseMilliseconds - start.TotalGcPauseMilliseconds));
                        _active = null;
                        Reply(stream, 200, new("ok", end, window, Observer: Counters()));
                    }
                    return;
                }
                Reply(stream, 404, new("error", Error: "Route not found."));
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (Exception ex) { Console.Error.WriteLine("[Conduit observer] Control handler failed: " + ex.GetType().Name); }
        finally
        {
            Interlocked.Increment(ref _requests);
            Interlocked.Add(ref _handlerTicks, Stopwatch.GetTimestamp() - started);
            Interlocked.Add(ref _handlerAllocated, GC.GetAllocatedBytesForCurrentThread() - allocated);
            client.Dispose();
        }
    }

    private ProcessSnapshot Capture(bool induced = false, long inducedGcTicks = 0)
    {
        var started = Stopwatch.GetTimestamp();
        var utc = DateTimeOffset.UtcNow;
        double cpu;
        long working, peak;
        lock (_processLock)
        {
            _process.Refresh();
            cpu = _process.TotalProcessorTime.TotalMilliseconds;
            working = _process.WorkingSet64;
            peak = _process.PeakWorkingSet64;
        }
        var info = GC.GetGCMemoryInfo();
        var generations = new List<GenerationMemory>();
        var generationInfo = info.GenerationInfo;
        for (var i = 0; i < generationInfo.Length; i++)
        {
            var g = generationInfo[i];
            generations.Add(new(i, i == 3 ? "LOH" : i == 4 ? "POH" : "Gen" + i,
                g.SizeBeforeBytes, g.FragmentationBeforeBytes, g.SizeAfterBytes, g.FragmentationAfterBytes));
        }
        var snapshot = new ProcessSnapshot
        {
            ObserverVersion = 2, IsServerGc = GCSettings.IsServerGC, GcConfiguration = GcConfiguration,
            ProcessId = Environment.ProcessId, EntryAssembly = Assembly.GetEntryAssembly()?.GetName().Name,
            Source = "in-process-startup-hook", Utc = utc, StopwatchTimestamp = started, StopwatchFrequency = Stopwatch.Frequency,
            Runtime = RuntimeInformation.FrameworkDescription, RuntimeVersion = Environment.Version.ToString(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Os = RuntimeInformation.OSDescription,
            TotalCpuMilliseconds = cpu, TotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true),
            Gen0Collections = GC.CollectionCount(0), Gen1Collections = GC.CollectionCount(1), Gen2Collections = GC.CollectionCount(2),
            TotalGcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds,
            WorkingSetBytes = working, ProcessLifetimePeakWorkingSetBytes = peak,
            GcHeapSizeBytes = info.HeapSizeBytes, GcFragmentedBytes = info.FragmentedBytes,
            GcTotalCommittedBytes = info.TotalCommittedBytes, GcMemoryLoadBytes = info.MemoryLoadBytes,
            GcTotalAvailableMemoryBytes = info.TotalAvailableMemoryBytes, GcIndex = info.Index,
            GcGeneration = info.Generation, GcCompacted = info.Compacted, GcConcurrent = info.Concurrent,
            Generations = generations.ToArray(), InducedFullGc = induced, InducedGcElapsedTicks = inducedGcTicks,
            CaptureCompletedTimestamp = Stopwatch.GetTimestamp()
        };
        Interlocked.Increment(ref _captureCount);
        Interlocked.Add(ref _captureTicks, snapshot.CaptureCompletedTimestamp - started);
        return snapshot;
    }

    private void SampleWorkingSet()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            lock (_windowLock)
            {
                if (_active is null) return;
                lock (_processLock)
                {
                    _process.Refresh();
                    _active.PeakWorkingSetBytes = Math.Max(_active.PeakWorkingSetBytes, _process.WorkingSet64);
                }
                _active.PeriodicSamples++;
                Interlocked.Increment(ref _sampleCount);
            }
        }
        finally { Interlocked.Add(ref _sampleTicks, Stopwatch.GetTimestamp() - started); }
    }

    private ObserverCounters Counters() => new(Environment.ProcessId, _port, _interval,
        Interlocked.Read(ref _requests), Interlocked.Read(ref _captureCount), Interlocked.Read(ref _captureTicks),
        Interlocked.Read(ref _handlerTicks), Interlocked.Read(ref _handlerAllocated),
        Interlocked.Read(ref _sampleTicks), Interlocked.Read(ref _sampleCount), Stopwatch.Frequency,
        "Process-wide counters include application, runtime, background services and observer. Handler allocations count only synchronous handler-thread allocations; scheduling/sampling and other observer allocations are not fully attributed. Wall-time counters are not CPU time. Snapshot fields are sampled sequentially. GC heap/generation sizes describe the latest GC, not instantaneous live bytes. Periodic peak can miss between-sample spikes. No per-request pure allocation attribution; no active HTTP request instrumentation.");

    private static string? ReadHeader(NetworkStream stream)
    {
        var bytes = new List<byte>(512);
        var tail = 0;
        while (bytes.Count < 8192)
        {
            var b = stream.ReadByte();
            if (b < 0) return null;
            bytes.Add((byte)b);
            tail = (tail << 8) | b;
            if (tail == 0x0d0a0d0a) return Encoding.ASCII.GetString(bytes.ToArray());
        }
        return null;
    }

    private static Dictionary<string, string> Query(string text)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            query[Uri.UnescapeDataString(parts[0])] = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        return query;
    }

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 128 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static void Reply(NetworkStream stream, int status, ObserverResponse response)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(response, ObserverJsonContext.Default.ObserverResponse);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        stream.Write(header);
        stream.Write(body);
    }
}

internal sealed class ActiveWindow(string id, ProcessSnapshot start)
{
    public string Id { get; } = id;
    public ProcessSnapshot Start { get; } = start;
    public long PeakWorkingSetBytes { get; set; } = start.WorkingSetBytes;
    public long PeriodicSamples { get; set; }
}

internal sealed record ObserverResponse(string Status, ProcessSnapshot? Snapshot = null, WindowMeasurement? Window = null,
    string? WindowId = null, ObserverCounters? Observer = null, string? Error = null);
internal sealed record GenerationMemory(int Index, string Name, long SizeBeforeBytes, long FragmentationBeforeBytes, long SizeAfterBytes, long FragmentationAfterBytes);
internal sealed record WindowDeltas(long ElapsedStopwatchTicks, double ElapsedSeconds, double CpuMilliseconds, long AllocatedBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections, double GcPauseMilliseconds);
internal sealed record WindowMeasurement(string Id, ProcessSnapshot Start, ProcessSnapshot End, long SampledPeakWorkingSetBytes,
    long PeriodicWorkingSetSamples, int SamplingIntervalMs, WindowDeltas Deltas);
internal sealed record ObserverCounters(int ProcessId, int LoopbackPort, int SamplingIntervalMs, long CompletedControlRequests,
    long CompletedSnapshots, long SnapshotCaptureElapsedStopwatchTicks, long ControlHandlerElapsedStopwatchTicks,
    long ControlHandlerCurrentThreadAllocatedBytes, long SamplerElapsedStopwatchTicks, long PeriodicWorkingSetSamples,
    long StopwatchFrequency, string Limitations);

internal sealed class ProcessSnapshot
{
    public int ObserverVersion { get; init; }
    public bool IsServerGc { get; init; }
    public Dictionary<string, string> GcConfiguration { get; init; } = new(StringComparer.Ordinal);
    public int ProcessId { get; init; }
    public string? EntryAssembly { get; init; }
    public string Source { get; init; } = "";
    public DateTimeOffset Utc { get; init; }
    public long StopwatchTimestamp { get; init; }
    public long CaptureCompletedTimestamp { get; init; }
    public long StopwatchFrequency { get; init; }
    public string Runtime { get; init; } = "";
    public string RuntimeVersion { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Os { get; init; } = "";
    public double TotalCpuMilliseconds { get; init; }
    public long TotalAllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public double TotalGcPauseMilliseconds { get; init; }
    public long WorkingSetBytes { get; init; }
    public long ProcessLifetimePeakWorkingSetBytes { get; init; }
    public long GcHeapSizeBytes { get; init; }
    public long GcFragmentedBytes { get; init; }
    public long GcTotalCommittedBytes { get; init; }
    public long GcMemoryLoadBytes { get; init; }
    public long GcTotalAvailableMemoryBytes { get; init; }
    public long GcIndex { get; init; }
    public int GcGeneration { get; init; }
    public bool GcCompacted { get; init; }
    public bool GcConcurrent { get; init; }
    public GenerationMemory[] Generations { get; init; } = [];
    public bool InducedFullGc { get; init; }
    public long InducedGcElapsedTicks { get; init; }
    public int? ActiveHttpRequests => null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ObserverResponse))]
internal partial class ObserverJsonContext : JsonSerializerContext { }
