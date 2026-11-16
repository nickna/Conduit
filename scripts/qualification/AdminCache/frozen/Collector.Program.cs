using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConduitLLM.Core.Services;

namespace Conduit.CollectorMeasurements;

internal static class Program
{
    private const string Source = "f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa";
    private static int _consume;
    internal static int Main(string[] args)
    {
        if (JsonSerializer.IsReflectionEnabledByDefault) throw new InvalidOperationException("Reflection JSON disabled required.");
        var output = Path.GetFullPath(args.Single());
        if (File.Exists(output)) throw new IOException("New report path required.");
        var report = new Report
        {
            SourceRevision = Source, Framework = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), ProcessId = Environment.ProcessId,
            ProductionAssemblySha256 = Hash(typeof(StreamingMetricsCollector).Assembly.Location),
            HarnessAssemblySha256 = Hash(typeof(Program).Assembly.Location), StartedAtUtc = DateTime.UtcNow
        };
        foreach (var chunks in new[] { 10, 100, 1000, 10000 })
        {
            foreach (var mode in new[] { "snapshot-each-content-chunk", "record-plus-final-snapshot-control" })
            {
                _ = Measure(chunks, mode); // one full-stream warmup, retained separately by protocol
                var samples = new List<Sample>();
                for (var index = 0; index < 3; index++) samples.Add(Measure(chunks, mode));
                report.Rows.Add(new(chunks, mode, samples));
            }
        }
        report.Consume = _consume;
        report.Succeeded = true;
        report.CompletedAtUtc = DateTime.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, report, ReportJson.Default.Report);
        Console.WriteLine("PASS collector scaling10/100/1000/10000; actual production collector; synchronous thread-local allocation;8rows/24measured streams");
        return 0;
    }
    private static Sample Measure(int chunks, string mode)
    {
        // Entire stream loop is synchronous on the same managed thread, so this
        // counter is valid for this narrow fixture and does not include other threads.
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var collector = new StreamingMetricsCollector("collector-fixture", "model", "provider");
        if (mode == "snapshot-each-content-chunk")
        {
            // Exact public-collector call pattern from ChatEndpoints.EmitPeriodicMetrics.
            // Excludes JSON framing, clocks/ShouldEmitMetrics, HTTP and periodic emissions.
            for (var index = 0; index < chunks; index++)
            {
                if (collector.GetMetrics().TimeToFirstTokenMs is null) collector.RecordFirstToken();
                else collector.RecordToken();
            }
        }
        else
        {
            // Mechanistic control only: same actual collector, snapshots deferred.
            // Not a deployed endpoint path and not a constant-state replacement.
            collector.RecordFirstToken();
            for (var index = 1; index < chunks; index++) collector.RecordToken();
        }
        var final = collector.GetMetrics();
        var micros = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (Environment.CurrentManagedThreadId != thread || final.TokensGenerated != chunks || final.TimeToFirstTokenMs is null)
            throw new InvalidOperationException("Collector state/thread validation failed.");
        _consume = final.TokensGenerated;
        return new(micros, allocated, final.TokensGenerated, thread);
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed record Sample(double Microseconds, long ThreadLocalAllocatedBytes, int ContentChunksRecorded, int ManagedThreadId);
internal sealed record Row(int ContentChunks, string Mode, List<Sample> Samples);
internal sealed class Report
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceRevision { get; set; } = "";
    public string Framework { get; set; } = "";
    public string OS { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string ProductionAssemblySha256 { get; set; } = "";
    public string HarnessAssemblySha256 { get; set; } = "";
    public int ProcessId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public int WarmupsPerModePerSize { get; set; } = 1;
    public int MeasuredPerModePerSize { get; set; } = 3;
    public List<Row> Rows { get; set; } = [];
    public bool Succeeded { get; set; }
    public int Consume { get; set; }
    public string TimingBoundary { get; set; } = "Actual production collector construction, record loop and final GetMetrics snapshot; no network, SSE writer, ShouldEmitMetrics, pacing or periodic emissions. Current pattern calls GetMetrics before recording every content chunk as the actual enhanced endpoint does.";
    public string AllocationBoundary { get; set; } = "Synchronous loop on verified unchanged managed thread; GC.GetAllocatedBytesForCurrentThread before/after measured stream loop, includes collector and snapshot lists; excludes other threads and report construction.";
    public string ControlScope { get; set; } = "record-plus-final-snapshot-control uses same unmodified collector but omits per-chunk snapshot calls; isolates that call-pattern cost only. It is not the production endpoint path or an implemented constant-space optimization. The collector still retains all timestamps.";
    public string Limitations { get; set; } = "Burst microbenchmark,3measured streams per size/mode/process after1warmup. Five independent processes planned. Fixed mode order, tiered JIT and GC can affect timing; mechanism/allocation evidence only, not HTTP throughput or statistical significance. No ARM64 or NativeAOT evidence.";
}

[JsonSerializable(typeof(Report))]
internal partial class ReportJson : JsonSerializerContext;
