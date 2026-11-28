using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConduitLLM.Core.Services;

namespace ConduitLLM.Benchmarks.Streaming;

/// <summary>Measures the actual collector with source-compatible baseline/candidate call adapters.</summary>
public static class CollectorMicroHarness
{
    private static readonly PropertyInfo? FirstTokenProperty = typeof(StreamingMetricsCollector)
        .GetProperty("HasRecordedFirstToken", BindingFlags.Public | BindingFlags.Instance);
    private static readonly Func<StreamingMetricsCollector, bool> ReadFirstToken = FirstTokenProperty?.GetMethod
        ?.CreateDelegate<Func<StreamingMetricsCollector, bool>>()
        ?? (static collector => collector.GetMetrics().TimeToFirstTokenMs.HasValue);
    private static int _consume;

    /// <summary>Writes one new JSON report; the external driver owns independent process repetitions.</summary>
    public static Task<int> RunAsync(string[] args)
    {
        try { return Task.FromResult(Run(args)); }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return Task.FromResult(1);
        }
    }

    private static int Run(string[] args)
    {
        string? output = null;
        string? source = null;
        var samples = 5;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Every option requires a value.");
            switch (args[index])
            {
                case "--output": output = Path.GetFullPath(args[index + 1]); break;
                case "--source-sha": source = args[index + 1]; break;
                case "--samples": samples = int.Parse(args[index + 1], CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown option: {args[index]}");
            }
        }
        if (output is null || source is null || source.Length != 40 || source.Any(value => !char.IsAsciiHexDigit(value)) || samples is < 1 or > 50)
            throw new ArgumentException("Require --output NEW.json --source-sha FULL_SHA [--samples 5], samples1..50.");
        if (File.Exists(output)) throw new IOException("Refuse to overwrite a prior report.");

        var report = new CollectorMicroReport
        {
            DeclaredSourceRevision = source,
            Framework = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessId = Environment.ProcessId,
            IsServerGc = GCSettings.IsServerGC,
            GcConfiguration = GC.GetConfigurationVariables().OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal),
            ProductionAssemblySha256 = Hash(typeof(StreamingMetricsCollector).Assembly.Location),
            HarnessAssemblySha256 = Hash(typeof(CollectorMicroHarness).Assembly.Location),
            CurrentCallerReader = FirstTokenProperty is null ? "GetMetrics.TimeToFirstTokenMs" : "HasRecordedFirstToken",
            InnerSamplesPerCase = samples,
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime.TotalMilliseconds;
        var wallStart = Stopwatch.GetTimestamp();
        var modes = new[] { "snapshot-each-content-chunk", "current-caller-first-token-check", "record-plus-final-snapshot-control" };
        var sizeIndex = 0;
        foreach (var chunks in new[] { 10, 100, 1000, 10000 })
        {
            // Both source arms use the same deterministic order; alternate mode order by size.
            foreach (var mode in sizeIndex++ % 2 == 0 ? modes : modes.Reverse())
                AddCase(chunks, mode);
        }
        AddCase(100_000, "record-plus-final-snapshot-control");
        report.InvocationWallMilliseconds = Stopwatch.GetElapsedTime(wallStart).TotalMilliseconds;
        process.Refresh();
        report.InvocationCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
        report.LifetimePeakWorkingSetBytes = process.PeakWorkingSet64;
        report.Consume = _consume;
        report.Passed = true;
        report.CompletedAtUtc = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, report, CollectorMicroJsonContext.Default.CollectorMicroReport);
        Console.WriteLine($"PASS collector micro: {report.Rows.Count} cases, {samples} inner samples; {report.CurrentCallerReader}; {output}");
        return 0;

        void AddCase(int chunks, string mode)
        {
            _ = Measure(process, chunks, mode);
            var measured = new List<CollectorMicroSample>();
            for (var index = 0; index < samples; index++) measured.Add(Measure(process, chunks, mode));
            report.Rows.Add(new(chunks, mode, measured));
        }
    }

    private static CollectorMicroSample Measure(Process process, int chunks, string mode)
    {
        var thread = Environment.CurrentManagedThreadId;
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var collector = new StreamingMetricsCollector("collector-fixture", "model", "provider");
        switch (mode)
        {
            case "snapshot-each-content-chunk":
                for (var index = 0; index < chunks; index++)
                {
                    if (collector.GetMetrics().TimeToFirstTokenMs is null) collector.RecordFirstToken();
                    else collector.RecordToken();
                }
                break;
            case "current-caller-first-token-check":
                for (var index = 0; index < chunks; index++)
                {
                    if (!ReadFirstToken(collector)) collector.RecordFirstToken();
                    else collector.RecordToken();
                }
                break;
            case "record-plus-final-snapshot-control":
                collector.RecordFirstToken();
                for (var index = 1; index < chunks; index++) collector.RecordToken();
                break;
            default: throw new ArgumentException("Unknown collector mode.");
        }
        var final = collector.GetMetrics();
        var ended = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        process.Refresh();
        var cpu = process.TotalProcessorTime.TotalMilliseconds - cpuBefore;
        if (Environment.CurrentManagedThreadId != thread || allocated < 0 || cpu < 0 || final.TokensGenerated != chunks ||
            final.TimeToFirstTokenMs is null || final.AvgInterTokenLatencyMs is null)
            throw new InvalidOperationException("Collector thread/counter/state validation failed.");
        Volatile.Write(ref _consume, final.TokensGenerated);
        return new(started, ended, Stopwatch.GetElapsedTime(started, ended).TotalMicroseconds, cpu,
            allocated, final.TokensGenerated, final.AvgInterTokenLatencyMs.Value, thread);
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed record CollectorMicroSample(long StartedTicks, long EndedTicks, double Microseconds,
    double ProcessCpuMilliseconds, long ThreadLocalAllocatedBytes, int ContentChunksRecorded,
    double AverageInterTokenLatencyMilliseconds, int ManagedThreadId);
internal sealed record CollectorMicroRow(int ContentChunks, string Mode, List<CollectorMicroSample> Samples);
internal sealed class CollectorMicroReport
{
    public int SchemaVersion { get; set; } = 1;
    public bool Passed { get; set; }
    public string DeclaredSourceRevision { get; set; } = "";
    public string Framework { get; set; } = "";
    public string OS { get; set; } = "";
    public string Architecture { get; set; } = "";
    public int ProcessId { get; set; }
    public bool IsServerGc { get; set; }
    public Dictionary<string, string> GcConfiguration { get; set; } = [];
    public string ProductionAssemblySha256 { get; set; } = "";
    public string HarnessAssemblySha256 { get; set; } = "";
    public string CurrentCallerReader { get; set; } = "";
    public long StopwatchFrequency { get; set; } = Stopwatch.Frequency;
    public int WarmupStreamsPerCase { get; set; } = 1;
    public int InnerSamplesPerCase { get; set; }
    public List<CollectorMicroRow> Rows { get; set; } = [];
    public double InvocationWallMilliseconds { get; set; }
    public double InvocationCpuMilliseconds { get; set; }
    public long LifetimePeakWorkingSetBytes { get; set; }
    public int Consume { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public string TimingScope { get; set; } = "Real production collector construction, record loop and final GetMetrics. No SSE/HTTP, ShouldEmitMetrics, pacing or periodic emission. First-token reader delegate is discovered once outside timing; current-caller mode includes identical delegate-dispatch overhead in both arms.";
    public string AllocationScope { get; set; } = "Synchronous verified same-thread GC.GetAllocatedBytesForCurrentThread, including collector and metric objects; excludes report objects/counter reads and other threads. No GC policy change or approximate counter.";
    public string CpuScope { get; set; } = "Process CPU sample includes counter-read/runtime background overhead and may be zero at OS timer resolution. Invocation CPU includes all warmups, samples and report aggregation before serialization. No idle subtraction or per-request CPU attribution.";
    public string ControlScope { get; set; } = "Snapshot-each mode preserves the old public call pattern; current-caller mode uses the actual direct property when present, otherwise the old snapshot getter. Record-plus-final is a mechanistic control, not the deployed endpoint. All sizes/modes record exact chunk counts.";
    public string Limitations { get; set; } = "Burst microbenchmark. External driver must use independent fresh-process repetitions and bind source/SDK/binary identities. Declared source revision alone does not verify provenance. Same-runtime baseline/candidate comparison; no HTTP throughput, p99, production capacity or automatic acceptance claim. Peak working set is process lifetime, not pure collector retained bytes.";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CollectorMicroReport))]
internal partial class CollectorMicroJsonContext : JsonSerializerContext;
