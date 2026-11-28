using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using Microsoft.AspNetCore.Http;

namespace ConduitLLM.Benchmarks.Streaming;

/// <summary>Bounded writer-only diagnostics; this is not network or Gateway capacity evidence.</summary>
public static class WriterMicroHarness
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output")
        {
            Console.Error.WriteLine("writer-micro --output <fresh-directory>");
            return 1;
        }
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Use a fresh output directory; retain previous evidence.");
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        using var process = Process.GetCurrentProcess();
        foreach (var enhanced in new[] { false, true })
        {
            foreach (var characters in new[] { 32, 1024 })
            {
                foreach (var frames in new[] { 10, 100, 1000, 10000 })
                {
                    // Identical warmup counts per scenario; not a guarantee of steady-state JIT.
                    await MeasureAsync(enhanced, characters, 100, process);
                    for (var run = 1; run <= 5; run++)
                    {
                        var observation = await MeasureAsync(enhanced, characters, frames, process);
                        rows.Add(new { run, enhanced, characters, frames, observation });
                    }
                }
            }
        }
        var core = typeof(StreamingMetricsCollector).Assembly;
        var gateway = typeof(EnhancedSSEResponseWriter).Assembly;
        var report = new
        {
            schemaVersion = 1,
            scope = "Writer-only fresh in-memory stream per run; no provider, network, queue or billing qualification",
            runtime = Environment.Version.ToString(),
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os = RuntimeInformation.OSDescription,
            serverGc = System.Runtime.GCSettings.IsServerGC,
            gcConfiguration = GC.GetConfigurationVariables().ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture)),
            gateway = new { path = gateway.Location, sha256 = Hash(File.ReadAllBytes(gateway.Location)) },
            core = new { path = core.Location, sha256 = Hash(File.ReadAllBytes(core.Location)) },
            rows,
            performanceAcceptanceGranted = false,
            limitations = new[]
            {
                "Current-thread allocation is reported only if the whole measurement remains on that thread.",
                "Process CPU/allocation include background and runtime work; no idle subtraction.",
                "A preallocated in-memory sink counts/copies writes; hashing/validation occurs outside timing.",
                "Microbenchmarks do not establish first-token network latency, backpressure or production capacity.",
                "Each source must be published separately; do not mix this with frozen phase-0 or phase-2 datasets."
            }
        };
        await File.WriteAllTextAsync(Path.Join(output, "writer-micro.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() }));
        return 0;
    }

    private static async Task<object> MeasureAsync(bool enhanced, int characters, int frames, Process process)
    {
        var context = new DefaultHttpContext();
        var options = GatewayJsonOptions.Create();
        var chunk = new ChatCompletionChunk
        {
            Id = "chatcmpl_phase3", Created = 1760000000, Model = "phase3-model",
            Choices = [new StreamingChoice { Index = 0, Delta = new DeltaContent { Content = new string('x', characters) + " café 雪" } }]
        };
        var json = JsonSerializer.Serialize(chunk, (JsonTypeInfo<ChatCompletionChunk>)options.GetTypeInfo(typeof(ChatCompletionChunk)));
        var contentBytes = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
        var namedBytes = Encoding.UTF8.GetBytes($"event: reasoning{Environment.NewLine}data: {{\"content\":\"reason\"}}{Environment.NewLine}{Environment.NewLine}");
        using var sink = new CountingStream(checked(contentBytes.Length * frames + namedBytes.Length + 64));
        context.Response.Body = sink;
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < frames; index++) expected.AppendData(contentBytes);
        // The named-event escaping is determined by the same fixed explicit options.
        if (enhanced) expected.AppendData(namedBytes);
        expected.AppendData("data: [DONE]\n\n"u8);

        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var threadBefore = Environment.CurrentManagedThreadId;
        var threadAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var writer = new EnhancedSSEResponseWriter(context.Response, options, enhanced);
        for (var index = 0; index < frames; index++)
            await writer.WriteContentEventAsync(chunk);
        await writer.WriteReasoningEventAsync("reason");
        await writer.WriteDoneEventAsync();
        await writer.WriteDoneEventAsync();
        watch.Stop();
        var sameThread = threadBefore == Environment.CurrentManagedThreadId;
        var threadAllocated = sameThread ? GC.GetAllocatedBytesForCurrentThread() - threadAllocatedBefore : (long?)null;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var hash = sink.GetHash();
        var expectedHash = Convert.ToHexStringLower(expected.GetHashAndReset());
        if (hash != expectedHash || writer.BytesWritten != sink.Bytes || writer.EventsWritten != frames + (enhanced ? 2 : 1) || sink.Flushes != frames + (enhanced ? 3 : 2))
            throw new InvalidOperationException("Writer wire bytes, event/byte counts, flush or DONE semantics differ.");
        return new
        {
            elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            processCpuMilliseconds = cpu.TotalMilliseconds,
            processAllocatedBytes = allocated,
            currentThreadAllocatedBytes = threadAllocated,
            sameThread,
            sink.Bytes, sink.Writes, sink.Flushes,
            writer.EventsWritten,
            wireSha256 = hash,
            expectedWireSha256 = expectedHash,
            validated = true
        };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class CountingStream(int capacity) : Stream
    {
        private readonly MemoryStream _bytes = new(capacity);
        public long Bytes { get; private set; }
        public int Writes { get; private set; }
        public int Flushes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public string GetHash() => Convert.ToHexStringLower(SHA256.HashData(_bytes.GetBuffer().AsSpan(0, checked((int)Bytes))));
        public override void Flush() => Flushes++;
        public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { _bytes.Write(buffer); Bytes += buffer.Length; Writes++; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bytes.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
