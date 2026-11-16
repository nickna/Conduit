using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class StartupHook
{
    private static readonly object Sync = new();
    private static Process? process;
    private static Timer? timer;
    private static Snapshot? start;
    private static long peak, samples, sampleTicks, sampleAllocated;
    private static string? output;
    private static bool finished;
    private static int interval = 100;
    private static readonly List<AllocationCounterSample> allocationSamples = [];
    public static void Initialize()
    {
        output = Environment.GetEnvironmentVariable("CONDUIT_MICRO_OBSERVER_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        if (!Path.IsPathFullyQualified(output) || File.Exists(output)) throw new InvalidOperationException("Micro observer requires a fresh absolute output path.");
        process = Process.GetCurrentProcess();
        start = Capture();
        allocationSamples.Add(new(start.Timestamp, start.AllocatedBytes));
        peak = start.WorkingSetBytes;
        interval = Environment.GetEnvironmentVariable("CONDUIT_MICRO_OBSERVER_SAMPLE_MS") == "0" ? 0 : 100;
        timer = new Timer(_ => Sample(), null, interval == 0 ? Timeout.Infinite : interval, interval == 0 ? Timeout.Infinite : interval);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Finish();
    }
    private static Snapshot Capture()
    {
        process!.Refresh();
        var gc = GC.GetGCMemoryInfo();
        return new Snapshot(Stopwatch.GetTimestamp(), process.TotalProcessorTime.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            GC.GetTotalPauseDuration().TotalMilliseconds, process.WorkingSet64, process.PeakWorkingSet64,
            gc.HeapSizeBytes, gc.FragmentedBytes, gc.TotalCommittedBytes);
    }
    private static void Sample()
    {
        var begun = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        lock (Sync)
        {
            if (finished) return;
            process!.Refresh();
            allocationSamples.Add(new(Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(precise: true)));
            peak = Math.Max(peak, process.WorkingSet64);
            samples++;
            sampleTicks += Stopwatch.GetTimestamp() - begun;
            sampleAllocated += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
    }
    private static void Finish()
    {
        try
        {
            timer!.Change(Timeout.Infinite, Timeout.Infinite);
            Report report;
            lock (Sync)
            {
                finished = true;
                var end = Capture();
                allocationSamples.Add(new(end.Timestamp, end.AllocatedBytes));
                var entry = Assembly.GetEntryAssembly()!;
                var variables = GC.GetConfigurationVariables().OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new GcVariable(pair.Key, Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "")).ToArray();
                report = new Report(Environment.ProcessId, RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSDescription,
                    GCSettings.IsServerGC, Environment.ProcessorCount,
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry.Location))),
                    start!, end, Math.Max(peak, end.WorkingSetBytes), samples, sampleTicks, sampleAllocated,
                    Stopwatch.Frequency, allocationSamples.ToArray(), variables, interval);
            }
            using var file = new FileStream(output!, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, report, MicroJson.Default.Report);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Micro observer failed: " + error.GetType().Name);
            if (Environment.ExitCode == 0) Environment.ExitCode = 91;
        }
        finally { timer?.Dispose(); process?.Dispose(); }
    }
}

internal sealed record Snapshot(long Timestamp, double CpuMilliseconds, long AllocatedBytes,
    int Gen0, int Gen1, int Gen2, double GcPauseMilliseconds, long WorkingSetBytes, long LifetimePeakWorkingSetBytes,
    long LastGcHeapBytes, long LastGcFragmentedBytes, long LastGcCommittedBytes);
internal sealed record Report(int ProcessId, string Framework, string Architecture, string OS,
    bool ServerGC, int ProcessorCount, string HarnessAssemblySha256, Snapshot Start, Snapshot End,
    long SampledPeakWorkingSetBytes, long PeriodicSamples, long ObserverSamplingElapsedTicks,
    long ObserverSamplerThreadAllocatedBytes, long StopwatchFrequency,
    AllocationCounterSample[] AllocationCounterSamples, GcVariable[] GcConfigurationVariables, int SamplingIntervalMilliseconds)
{
    public int SchemaVersion => 1;
    public string Scope => "Whole microprocess from startup-hook initial capture to ProcessExit final capture, including warmups, all reads/ownership validation, fixture/report construction, production disposal and observer. Excludes observer sidecar serialization after the final capture. Counts include fixed observer timer overhead; no subtraction. Not timed-row or pure-operation CPU/GC attribution.";
    public string Sampling => SamplingIntervalMilliseconds == 0
        ? "Metadata-only mode: start/end snapshots, no periodic sampler. Used alongside the unchanged Admin server observer to record actual GC mode without a second timer."
        : "100ms working-set/cumulative-allocation samples plus endpoints; may miss spikes or counter changes. Lifetime peak is distinct. Heap sizes describe the last GC, not instantaneous live bytes. Sampler elapsed ticks are wall time, not CPU time.";
    public string AllocationCounterSource => "GC.GetTotalAllocatedBytes(precise:true), raw start/100ms/end observations. .NET10 server-GC heap-retirement decreases were observed in a separate Gateway workload; any observed decrease invalidates allocation increments, never clamped. Nondecreasing sampled endpoints cannot rule out between-sample decreases. Collector per-thread allocation and awaited-cache process-wide per-operation deltas remain separate scopes.";
}
internal sealed record AllocationCounterSample(long Timestamp, long AllocatedBytes);
internal sealed record GcVariable(string Name, string Value);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Report))]
internal partial class MicroJson : JsonSerializerContext { }
