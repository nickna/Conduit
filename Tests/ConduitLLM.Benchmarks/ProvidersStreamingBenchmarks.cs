using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using BenchmarkDotNet.Attributes;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Providers.Groq;
using ConduitLLM.Providers.Streaming;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Benchmarks;

/// <summary>
/// Isolates Groq mapping after the shared SSE parser has produced a JsonElement.
/// Each benchmark operation maps FrameCount frames; results are not HTTP capacity.
/// </summary>
[MemoryDiagnoser]
public class GroqMetadataReuseBenchmarks
{
    private JsonDocument _document = null!;
    private GroqMappingClient _client = null!;

    [Params(10, 100, 1000, 10000)]
    public int FrameCount { get; set; }

    [Params(false, true)]
    public bool HostedUsage { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _document = JsonDocument.Parse(HostedUsage ? ProviderFrames.GroqUsage : ProviderFrames.Content);
        _client = new GroqMappingClient();
        ProviderFrames.RequireEquivalent(
            _client.MapUsingOptions(_document.RootElement), _client.MapCurrent(_document.RootElement));
    }

    [GlobalCleanup]
    public void Cleanup() => _document.Dispose();

    [Benchmark(Baseline = true)]
    public long OptionsAndGeneratedContextPerFrame()
    {
        long checksum = 0;
        for (var index = 0; index < FrameCount; index++)
            checksum += ProviderFrames.Checksum(_client.MapUsingOptions(_document.RootElement));
        return checksum;
    }

    [Benchmark]
    public long StableClientMetadata()
    {
        long checksum = 0;
        for (var index = 0; index < FrameCount; index++)
            checksum += ProviderFrames.Checksum(_client.MapCurrent(_document.RootElement));
        return checksum;
    }
}

/// <summary>
/// Mechanistic parser controls, deliberately separate from the deployed provider
/// hooks and SSE framing. DirectTypedControl is not a compatible provider rewrite.
/// JsonElementToModel still parses the input into a DOM before reading the model.
/// </summary>
[MemoryDiagnoser]
public class ProviderFrameParsingBenchmarks
{
    [Params(10, 100, 1000, 10000)]
    public int FrameCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        ProviderFrames.RequireEquivalent(ParseViaRawText(), ParseViaElement());
        ProviderFrames.RequireEquivalent(ParseViaRawText(), ParseDirectly());
    }

    [Benchmark(Baseline = true)]
    public long JsonElementToRawTextToModel()
    {
        long checksum = 0;
        for (var index = 0; index < FrameCount; index++)
            checksum += ProviderFrames.Checksum(ParseViaRawText());
        return checksum;
    }

    [Benchmark]
    public long JsonElementToModel()
    {
        long checksum = 0;
        for (var index = 0; index < FrameCount; index++)
            checksum += ProviderFrames.Checksum(ParseViaElement());
        return checksum;
    }

    [Benchmark]
    public long DirectTypedControl()
    {
        long checksum = 0;
        for (var index = 0; index < FrameCount; index++)
            checksum += ProviderFrames.Checksum(ParseDirectly());
        return checksum;
    }

    private static ChatCompletionChunk? ParseViaRawText()
    {
        var element = JsonSerializer.Deserialize(ProviderFrames.Content, CoreHttpJsonContext.Default.JsonElement);
        return JsonSerializer.Deserialize(element.GetRawText(), CoreHttpJsonContext.Default.ChatCompletionChunk);
    }

    private static ChatCompletionChunk? ParseViaElement()
    {
        var element = JsonSerializer.Deserialize(ProviderFrames.Content, CoreHttpJsonContext.Default.JsonElement);
        return JsonSerializer.Deserialize(element, CoreHttpJsonContext.Default.ChatCompletionChunk);
    }

    private static ChatCompletionChunk? ParseDirectly() =>
        JsonSerializer.Deserialize(ProviderFrames.Content, CoreHttpJsonContext.Default.ChatCompletionChunk);
}

internal sealed class GroqMappingClient() : GroqClient(
    new Provider { Id = 1, ProviderType = ProviderType.Groq, ProviderName = "benchmark" },
    new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "synthetic-not-used" },
    "fixture-model", NullLogger<GroqClient>.Instance)
{
    public ChatCompletionChunk? MapCurrent(JsonElement frame) => MapStreamingChunk(frame);

    // This public overload retains the exact pre-pilot options/context construction.
    public ChatCompletionChunk? MapUsingOptions(JsonElement frame) =>
        GroqChunkConverter.Convert(frame, DefaultJsonOptions);
}

internal static class ProviderFrames
{
    internal const string Content = """
        {"id":"chatcmpl-fixture","object":"chat.completion.chunk","created":1,"model":"llama",
         "choices":[{"index":0,"delta":{"role":"assistant","content":"café 雪 \"quoted\" payload",
          "tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"key\":42}"}}]}}]}
        """;

    internal const string GroqUsage = """
        {"id":"chatcmpl-fixture","object":"chat.completion.chunk","created":1,"model":"llama","choices":[],
         "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3},
         "x_groq":{"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,
          "code_interpreter":2,"code_interpreter_duration_seconds":1.25,"browser_search":1}}}
        """;

    internal static long Checksum(ChatCompletionChunk? chunk)
    {
        if (chunk is null) throw new InvalidOperationException("Fixture chunk was lost.");
        long result = (chunk.Id?.Length ?? 0) + (chunk.Usage?.TotalTokens ?? 0);
        foreach (var choice in chunk.Choices)
        {
            result += choice.Delta.Content?.Length ?? 0;
            if (choice.Delta.ToolCalls is { } calls)
                foreach (var call in calls) result += call.Function?.Arguments?.Length ?? 0;
        }
        if (chunk.ProviderToolUsage is { } tools)
            foreach (var tool in tools.Tools) result += tool.Count;
        return result;
    }

    internal static void RequireEquivalent(ChatCompletionChunk? baseline, ChatCompletionChunk? candidate)
    {
        if (baseline is null || candidate is null ||
            JsonSerializer.Serialize(baseline, CoreHttpJsonContext.Default.ChatCompletionChunk) !=
            JsonSerializer.Serialize(candidate, CoreHttpJsonContext.Default.ChatCompletionChunk) ||
            !Tools(baseline).SequenceEqual(Tools(candidate)))
            throw new InvalidOperationException("Mapped wire/accounting fixture outputs differ.");
    }

    private static IEnumerable<(string, int, decimal?)> Tools(ChatCompletionChunk chunk) =>
        chunk.ProviderToolUsage?.Tools.Select(tool => (tool.ToolName, tool.Count, tool.DurationSeconds)) ?? [];
}

/// <summary>Bounded synchronous mapping diagnostics; the outer driver owns fresh process pairs.</summary>
public static class ProviderMicroHarness
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output")
        {
            Console.Error.WriteLine("provider-micro --output <fresh-directory>");
            return 1;
        }
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Use a fresh output directory; retain previous evidence.");
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        using var process = Process.GetCurrentProcess();
        foreach (var hostedUsage in new[] { false, true })
        {
            var fixture = hostedUsage ? ProviderFrames.GroqUsage : ProviderFrames.Content;
            using var document = JsonDocument.Parse(fixture);
            var client = new GroqMappingClient();
            var variants = new (string Name, Func<ChatCompletionChunk?> Convert)[]
            {
                ("groq-public-options", () => client.MapUsingOptions(document.RootElement)),
                ("groq-client-call-path", () => client.MapCurrent(document.RootElement))
            };
            var expected = client.MapUsingOptions(document.RootElement)!;
            ProviderFrames.RequireEquivalent(expected, client.MapCurrent(document.RootElement));
            MeasureVariants("groq", hostedUsage ? "hosted-usage" : "content", fixture,
                variants, expected, process, rows);
        }

        var parser = new ProviderFrameParsingBenchmarks();
        parser.Setup();
        var expectedParsed = JsonSerializer.Deserialize(ProviderFrames.Content, CoreHttpJsonContext.Default.ChatCompletionChunk)!;
        var parserVariants = new (string Name, Func<ChatCompletionChunk?> Convert)[]
        {
            ("dom-raw-text-model", () =>
            {
                var element = JsonSerializer.Deserialize(ProviderFrames.Content, CoreHttpJsonContext.Default.JsonElement);
                return JsonSerializer.Deserialize(element.GetRawText(), CoreHttpJsonContext.Default.ChatCompletionChunk);
            }),
            ("dom-model-control", () =>
            {
                var element = JsonSerializer.Deserialize(ProviderFrames.Content, CoreHttpJsonContext.Default.JsonElement);
                return JsonSerializer.Deserialize(element, CoreHttpJsonContext.Default.ChatCompletionChunk);
            }),
            ("direct-typed-control", () => JsonSerializer.Deserialize(ProviderFrames.Content, CoreHttpJsonContext.Default.ChatCompletionChunk))
        };
        MeasureVariants("parser-controls", "content", ProviderFrames.Content, parserVariants,
            expectedParsed, process, rows);

        var report = new
        {
            schemaVersion = 1,
            scope = "Synchronous in-process provider mapping only; not HTTP, Gateway capacity, backpressure, queue or financial qualification",
            runtime = Environment.Version.ToString(), architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os = RuntimeInformation.OSDescription, processorCount = Environment.ProcessorCount,
            serverGc = System.Runtime.GCSettings.IsServerGC,
            gcConfiguration = GC.GetConfigurationVariables().OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => System.Convert.ToString(pair.Value, CultureInfo.InvariantCulture)),
            processId = Environment.ProcessId, innerSamples = 5, freshProcessesInThisReport = 1,
            coldMetadataInitializationMeasured = false,
            implementation = "Groq client variant invokes the actual protected mapper from the bound Providers assembly; public options variant retains the pre-pilot construction path",
            parserControlsDeployed = false, parserControlsProveVirtualHookCompatibility = false,
            assemblies = new[] { typeof(ProviderMicroHarness).Assembly, typeof(GroqClient).Assembly, typeof(ChatCompletionChunk).Assembly }
                .Select(assembly => new { name = assembly.GetName().Name, sha256 = Hash(File.ReadAllBytes(assembly.Location)) }).ToArray(),
            rows, performanceAcceptanceGranted = false,
            limitations = new[]
            {
                "Each row is one batch; divide only by its explicit frame count. Five inner samples are not five independent process trials.",
                "100 identical warmup calls per variant/scenario do not guarantee steady-state tiered JIT.",
                "Static metadata creation and client construction are outside the timed loops; this does not establish first-request improvement.",
                "Current-thread allocation measures the synchronous conversion loop; process CPU/allocation include runtime/background work with no idle subtraction.",
                "Very small frame batches may be below process-CPU clock resolution; preserve their absolute counters instead of inferring precise CPU ratios.",
                "Output validation and hashing occur outside timing. Full equivalence is checked for a representative result per batch; every timed result contributes to the retained checksum.",
                "Parser controls bypass SSE framing/provider transformation hooks and are not a deployed or compatibility-qualified rewrite.",
                "The public options overload is intentionally not cached by options identity; caller mutations and custom converters remain supported.",
                "No frozen phase-0/phase-2 fixture or historical measurement is modified by this diagnostic."
            }
        };
        var json = new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        await File.WriteAllTextAsync(Path.Combine(output, "provider-micro.json"), JsonSerializer.Serialize(report, json));
        return 0;
    }

    private static void MeasureVariants(string suite, string payload, string fixture,
        (string Name, Func<ChatCompletionChunk?> Convert)[] variants, ChatCompletionChunk expected,
        Process process, List<object> rows)
    {
        foreach (var variant in variants)
        {
            ProviderFrames.RequireEquivalent(expected, variant.Convert());
            for (var warmup = 0; warmup < 100; warmup++) _ = ProviderFrames.Checksum(variant.Convert());
        }
        foreach (var frames in new[] { 10, 100, 1000, 10000 })
        for (var sample = 1; sample <= 5; sample++)
        {
            var order = sample % 2 == 0 ? variants.Reverse() : variants;
            foreach (var variant in order)
                rows.Add(new { suite, payload, sample, frames, variant = variant.Name,
                    fixtureSha256 = Hash(Encoding.UTF8.GetBytes(fixture)),
                    observation = Measure(variant.Convert, expected, frames, process) });
        }
    }

    private static object Measure(Func<ChatCompletionChunk?> convert, ChatCompletionChunk expected,
        int frames, Process process)
    {
        var expectedChecksum = checked(ProviderFrames.Checksum(expected) * frames);
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var processBefore = GC.GetTotalAllocatedBytes(precise: true);
        var threadId = Environment.CurrentManagedThreadId;
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        var pauseBefore = GC.GetTotalPauseDuration();
        var threadBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        ChatCompletionChunk? last = null;
        long checksum = 0;
        for (var index = 0; index < frames; index++)
        {
            last = convert();
            checksum += ProviderFrames.Checksum(last);
        }
        var ended = Stopwatch.GetTimestamp();
        var threadAfter = GC.GetAllocatedBytesForCurrentThread();
        var processAfter = GC.GetTotalAllocatedBytes(precise: true);
        var sameThread = threadId == Environment.CurrentManagedThreadId;
        var pauses = GC.GetTotalPauseDuration() - pauseBefore;
        var collections = new[] { GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2 };
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        ProviderFrames.RequireEquivalent(expected, last);
        if (checksum != expectedChecksum) throw new InvalidOperationException("Timed provider frame checksum differs.");
        var wire = JsonSerializer.Serialize(last!, CoreHttpJsonContext.Default.ChatCompletionChunk);
        return new
        {
            elapsedMilliseconds = (ended - started) * 1000d / Stopwatch.Frequency,
            processCpuMilliseconds = cpu.TotalMilliseconds,
            rawProcessAllocatedStartBytes = processBefore, rawProcessAllocatedEndBytes = processAfter,
            rawProcessAllocatedDeltaBytes = processAfter - processBefore,
            processAllocationMonotonic = processAfter >= processBefore,
            currentThreadAllocatedBytes = sameThread ? threadAfter - threadBefore : (long?)null,
            sameThread, collections, gcPauseMilliseconds = pauses.TotalMilliseconds,
            checksum, expectedChecksum, wireSha256 = Hash(Encoding.UTF8.GetBytes(wire)),
            hostedTools = last!.ProviderToolUsage?.Tools.Select(tool => new { tool.ToolName, tool.Count, tool.DurationSeconds }).ToArray(),
            validated = true
        };
    }

    private static string Hash(byte[] bytes) => System.Convert.ToHexStringLower(SHA256.HashData(bytes));
}
